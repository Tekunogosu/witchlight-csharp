using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Posts what moves to the map service, rather than writing it to a file for the
/// service to read back.
///
/// Player positions are worth nothing once they are old, so a file was a
/// round trip through the disk for data that never needed to survive it — with a
/// write every couple of seconds for as long as the server was up.
///
/// The service listens on loopback, on whatever port the machine had free, and
/// writes both that port and a token into `api.json` beside the map. Nothing off
/// the machine can reach loopback and nothing on it can post without having read
/// that file, which is the protection a unix socket's permissions used to give —
/// on a platform where Windows has one too.
///
/// The port changes every time the service starts, so where it is is a belief
/// about the service rather than a fact about it, and the file is read again
/// whenever a post fails. That also covers the ordinary first case: the mod
/// starts the service, so for the first moments there is no file to read.
///
/// Set `WITCHLIGHT_API_BIND` and `WITCHLIGHT_API_TOKEN` to reach a service on
/// another machine, which cannot be told anything by a file beside this one; set
/// `api_bind` and `api_token` to match on the service.
///
/// Nothing here blocks the game. Posts run on the thread pool and one is dropped
/// rather than queued if the last has not finished: a position that arrived late
/// is worse than one skipped, since another follows two seconds behind.
/// </summary>
public sealed class MapService : IDisposable
{
    private const string BindVariable = "WITCHLIGHT_API_BIND";
    private const string TokenVariable = "WITCHLIGHT_API_TOKEN";

    /// <summary>Where the service says it is listening, and the word it wants.</summary>
    private sealed record Endpoint(string Url, string Token);

    private readonly HttpClient _client;
    private readonly ILogger _log;
    private readonly string _exports;

    /// <summary>
    /// The last answer read out of `api.json`, or null where there was none to
    /// read. Replaced wholesale rather than mutated, so a post already in flight
    /// finishes against the endpoint it started with.
    /// </summary>
    private volatile Endpoint? _endpoint;

    /// <summary>
    /// One kind of thing this posts, and everything that is true of it alone.
    ///
    /// Kept apart on purpose. One reading for both was worse than useless:
    /// players go every two seconds and markers every fifteen, so a succeeding
    /// player post overwrote a failing marker post almost immediately and the
    /// status line read healthy while half the data was going nowhere.
    ///
    /// A value rather than a boolean threaded through three methods. The old
    /// shape worked out which feed it was by comparing the path against a string
    /// literal, and every method that reported on one took an `isPlayers` and
    /// branched on it twice.
    /// </summary>
    private sealed class Feed
    {
        private int _sending;

        public Feed(string name, string path)
        {
            Name = name;
            Path = path;
        }

        /// <summary>What to call it when something goes wrong.</summary>
        public string Name { get; }

        /// <summary>Where it is posted.</summary>
        public string Path { get; }

        /// <summary>What happened to the last post of this kind.</summary>
        public string Health { get; set; } = "nothing sent yet";

        /// <summary>
        /// Takes the right to post, or says somebody else already has it.
        ///
        /// One at a time: a position that arrived late is worse than one skipped,
        /// since another follows two seconds behind.
        /// </summary>
        public bool Claim() => Interlocked.CompareExchange(ref _sending, 1, 0) == 0;

        /// <summary>Lets the next post of this kind start.</summary>
        public void Release() => Interlocked.Exchange(ref _sending, 0);
    }

    private readonly Feed _players = new("players", "/live/players");
    private readonly Feed _markers = new("markers", "/live/markers");
    private readonly Feed _world = new("world", "/live/world");

    public string PlayersHealth => _players.Health;

    public string MarkersHealth => _markers.Health;

    public string WorldHealth => _world.Health;

    private int _collecting;
    private readonly TimeSpan _resendMarkers;
    private string _sentMarkers = "";
    private DateTime _markersSentAt = DateTime.MinValue;
    private bool _complained;

    /// <param name="resendMarkersEvery">
    /// How long an unchanged marker list may go unsent. Injectable so a test can
    /// watch the resend happen without waiting minutes for it.
    /// </param>
    public MapService(string exports, ILogger log, TimeSpan? resendMarkersEvery = null)
    {
        _log = log;
        _exports = exports;
        _resendMarkers = resendMarkersEvery ?? TimeSpan.FromMinutes(5);

        // No BaseAddress: the port moves with every service start, and a client
        // carries its base for life. Each post names where it is going instead.
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        _endpoint = Resolve();
        log.Notification(
            "[witchlight] posting live data to {0}",
            _endpoint?.Url ?? $"whatever {ConnectionPath(exports)} names, once the service has written it");
    }

    /// <summary>Where the connection file is. The service writes it as it binds.</summary>
    public static string ConnectionPath(string exports) => Path.Combine(exports, "api.json");

    /// <summary>
    /// Where to post, reading the connection file again if the last look found
    /// nothing. Three callers asked this the same way and each spelled out the
    /// assignment back into the field.
    /// </summary>
    private Endpoint? Where() => _endpoint ?? (_endpoint = Resolve());

    /// <summary>
    /// Where to post, from the environment if an operator said, and otherwise from
    /// the file the service wrote.
    ///
    /// Null rather than a guess where there is nothing to read. A service that has
    /// not started yet and one that will never start look the same from here, and
    /// both are answered by trying again on the next tick.
    /// </summary>
    private Endpoint? Resolve()
    {
        var bind = Environment.GetEnvironmentVariable(BindVariable) ?? "";
        var token = Environment.GetEnvironmentVariable(TokenVariable) ?? "";
        if (bind.Length > 0)
        {
            return new Endpoint($"http://{bind}", token);
        }

        try
        {
            var path = ConnectionPath(_exports);
            if (!File.Exists(path))
            {
                return null;
            }

            var read = JObject.Parse(File.ReadAllText(path));
            var port = (int?)read["Port"] ?? 0;
            var word = (string?)read["Token"] ?? "";
            if (port <= 0 || word.Length == 0)
            {
                return null;
            }

            return new Endpoint($"http://127.0.0.1:{port}", word);
        }
        catch (Exception error)
        {
            // A half-written or unreadable file is the same as no file: something
            // else is wrong and saying so every two seconds would not help.
            _log.Debug("[witchlight] could not read the connection file: {0}", error.Message);
            return null;
        }
    }

    /// <summary>
    /// One post on the API channel that answers with something rather than
    /// merely being accepted.
    ///
    /// Three things ask rather than tell — a login word, what one player has set
    /// for themselves on the map, and the keeping of one preset — and they
    /// differ in the address and the body alone. Each happens when somebody
    /// types or presses something rather than every two seconds, so none of them
    /// rides the tick, and each is awaited by its caller off the game thread: it
    /// is an HTTP round trip, and the game does not wait for the map.
    ///
    /// Null where there is nothing to give back, including where the service is
    /// not answering. That is ordinary — it is a separate program and the game
    /// does not depend on it — and the address goes with it, so the next ask
    /// reads where the next service bound.
    /// </summary>
    private async Task<string?> Ask(string path, object body)
    {
        var endpoint = Where();
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            var asked = JsonConvert.SerializeObject(body);
            using var content = new StringContent(asked, Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + path)
            {
                Content = content,
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

            var reply = await _client.SendAsync(message).ConfigureAwait(false);
            if (reply.StatusCode == HttpStatusCode.Unauthorized)
            {
                _endpoint = null;
            }
            if (!reply.IsSuccessStatusCode)
            {
                return null;
            }

            return await reply.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _endpoint = null;
            _log.Warning("[witchlight] the map did not answer {0}: {1}", path, error.Message);
            return null;
        }
    }

    /// <summary>
    /// Asks the service for a login word for one player, and gives back the whole
    /// address to hand them — or null where there is none to give.
    /// </summary>
    public async Task<string?> Link(string uid, string name, string where)
    {
        var body = await Ask("/auth/mint", new { Uid = uid, Name = name }).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        try
        {
            var word = (string?)JObject.Parse(body)["Token"];
            return string.IsNullOrEmpty(word) ? null : $"{where.TrimEnd('/')}/login?t={word}";
        }
        catch (Exception error)
        {
            _log.Warning("[witchlight] the map's login word could not be read: {0}", error.Message);
            return null;
        }
    }

    /// <summary>
    /// What one player has set for themselves on the map: their presets, and
    /// where a new marker of theirs starts.
    ///
    /// The map's own form reads this over the public port under a session cookie.
    /// A game client has neither, so the mod asks on its behalf — it is the only
    /// party that knows which uid is which player, which is the same trust
    /// minting a login word already needs.
    ///
    /// Asked at the moment somebody marks something rather than held and
    /// refreshed. A preset made in a browser a minute ago must apply to the next
    /// press of the key, and a cache with a clock on it is a cache that is wrong
    /// for exactly as long as that clock says.
    /// </summary>
    public Task<string?> Presets(string uid) => Ask("/presets/of", new { Uid = uid });

    /// <summary>
    /// Keeps one preset for one player, and gives back everything they have set.
    ///
    /// One preset rather than the whole document: this side knows the one made in
    /// front of somebody in game and nothing else about what they have kept, and
    /// writing a whole document back from that would delete every preset they
    /// made in a browser.
    /// </summary>
    public Task<string?> KeepPreset(string uid, object preset) =>
        Ask("/presets/keep", new { Uid = uid, Preset = preset });

    /// <summary>
    /// Takes the markers somebody asked for on the web, and leaves the service
    /// holding none.
    ///
    /// The channel between the halves only runs one way — the mod posts, the
    /// service answers — so a marker typed into the web form cannot be pushed at
    /// the game and waits to be collected instead. Collecting empties the queue,
    /// which means a reply that never reaches the caller loses what was in it;
    /// that is the right trade for a marker, where asking again is one more form
    /// and holding one twice is two markers on the map.
    ///
    /// Null where there is nothing to say, including where the service is not
    /// answering. One request at a time: the tick that asks is faster than a round
    /// trip on a busy server, and asking twice would collect the same queue twice.
    /// </summary>
    public async Task<string?> Pending()
    {
        if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            var endpoint = Where();
            if (endpoint is null)
            {
                return null;
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + "/markers/pending");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

            var reply = await _client.SendAsync(message).ConfigureAwait(false);
            if (reply.StatusCode == HttpStatusCode.Unauthorized)
            {
                _endpoint = null;
            }
            if (!reply.IsSuccessStatusCode)
            {
                return null;
            }

            return await reply.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _endpoint = null;
            _log.Debug("[witchlight] could not collect markers from the map: {0}", error.Message);
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _collecting, 0);
        }
    }

    /// <summary>Who is online and where. Held in memory by the service.</summary>
    public void Players(string json) => Post(_players, json);

    /// <summary>What the world's clock says, on its way to whoever is looking.</summary>
    public void World(string json) => Post(_world, json);

    /// <summary>
    /// Every marker, when they are not what was sent last.
    ///
    /// Markers change a few times an hour and are the bulk of what there is to
    /// send, so posting them on the same timer as positions would be the same
    /// tens of kilobytes over and over.
    /// </summary>
    public void Markers(string json)
    {
        // What was sent last is a belief about the service, not a fact about it.
        // A service that restarts having lost its markers still gets the same
        // unchanged list offered, and skipping it leaves the map without markers
        // for as long as nobody moves one — which is to say, indefinitely. So the
        // list goes again on a slow timer whether or not it has changed, and the
        // desync heals itself within one interval.
        var overdue = DateTime.UtcNow - _markersSentAt >= _resendMarkers;
        if (json == _sentMarkers && !overdue)
        {
            return;
        }

        // Recorded when it lands, not when it is attempted. A post dropped because
        // the last one is still in flight would otherwise be remembered as sent.
        Post(_markers, json);
    }

    private void Post(Feed feed, string json)
    {
        if (!feed.Claim())
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // Read again where the last attempt found nothing. The service may
            // simply not have finished starting; it is the mod that started it.
            var endpoint = Where();
            if (endpoint is null)
            {
                Complain(feed, $"no service yet at {ConnectionPath(_exports)}");
                feed.Release();
                return;
            }

            try
            {
                await Send(feed, endpoint, json).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // The service being down is ordinary — it is a separate program
                // and the game does not depend on it. Its address is dropped with
                // it, so the next tick reads where the next one bound.
                _endpoint = null;
                Complain(feed, $"could not reach {endpoint.Url}: {error.Message}");
            }
            finally
            {
                feed.Release();
            }
        });
    }

    private async Task Send(Feed feed, Endpoint endpoint, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + feed.Path)
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

        var reply = await _client.SendAsync(message).ConfigureAwait(false);
        if (!reply.IsSuccessStatusCode)
        {
            // A rejected token means the service restarted and minted a new one,
            // which is the same fix as a refused connection.
            if (reply.StatusCode == HttpStatusCode.Unauthorized)
            {
                _endpoint = null;
            }
            Complain(feed, $"refused with {(int)reply.StatusCode}");
            return;
        }

        if (feed == _markers)
        {
            _sentMarkers = json;
            _markersSentAt = DateTime.UtcNow;
        }

        feed.Health = $"reaching {endpoint.Url}, {json.Length} bytes accepted";
        if (_complained)
        {
            _log.Notification("[witchlight] the map service is taking live data again");
            _complained = false;
        }
    }

    /// <summary>Says it once, and says when it stops being true.</summary>
    private void Complain(Feed feed, string what)
    {
        feed.Health = what;
        if (_complained)
        {
            return;
        }

        _complained = true;
        _log.Warning(
            "[witchlight] {0}: {1} — that half will not show on the map until it is back",
            feed.Name,
            what);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
