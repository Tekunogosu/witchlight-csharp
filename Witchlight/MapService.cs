using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// The service listens on a unix socket in `/tmp`, named after the export
/// directory so that both sides find it without being told and two game servers
/// on one machine do not collide. A socket is how two programs talk rather than
/// something either keeps, so it belongs with the running system and not beside
/// the map. Set `WITCHLIGHT_API_SOCKET` to a `host:port` or another path to move
/// it, and set `api_socket` to the same value on the service.
///
/// Nothing here blocks the game. Posts run on the thread pool and one is dropped
/// rather than queued if the last has not finished: a position that arrived late
/// is worse than one skipped, since another follows two seconds behind.
/// </summary>
public sealed class MapService : IDisposable
{
    private const string Variable = "WITCHLIGHT_API_SOCKET";

    private readonly HttpClient _client;
    private readonly ILogger _log;
    private readonly string _where;

    /// <summary>
    /// What happened to the last post of each kind, for `/witchlight status`.
    ///
    /// Kept apart on purpose. One reading for both was worse than useless: players
    /// go every two seconds and markers every fifteen, so a succeeding player post
    /// overwrote a failing marker post almost immediately and the line read
    /// healthy while half the data was going nowhere.
    /// </summary>
    public string PlayersHealth { get; private set; } = "nothing sent yet";

    public string MarkersHealth { get; private set; } = "nothing sent yet";

    private int _sendingPlayers;
    private int _sendingMarkers;
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
        _resendMarkers = resendMarkersEvery ?? TimeSpan.FromMinutes(5);
        var setting = Environment.GetEnvironmentVariable(Variable) ?? "";
        var address = setting.Length > 0 ? setting : DefaultSocket(exports);

        // A colon and no separator is an address; anything else is a socket path.
        // The service reads its own setting the same way.
        if (address.Contains(':') && !address.Contains(Path.DirectorySeparatorChar))
        {
            _where = $"http://{address}";
            _client = new HttpClient { BaseAddress = new Uri(_where) };
        }
        else
        {
            _where = address;
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), token);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            // The host is ignored for a socket, but HttpClient insists on one.
            _client = new HttpClient(handler) { BaseAddress = new Uri("http://witchlight") };
        }

        _client.Timeout = TimeSpan.FromSeconds(5);
        log.Notification("[witchlight] posting live data to {0}", _where);
    }

    /// <summary>Who is online and where. Held in memory by the service.</summary>
    public void Players(string json) => Post("/live/players", json, ref _sendingPlayers);

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
        Post("/live/markers", json, ref _sendingMarkers);
    }

    private void Post(string path, string json, ref int sending)
    {
        if (Interlocked.CompareExchange(ref sending, 1, 0) != 0)
        {
            return;
        }

        // Captured by reference is not possible for a field in a lambda, so the
        // flag is cleared through a local copy of which one it was.
        var isPlayers = path == "/live/players";

        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var reply = await _client.PostAsync(path, content).ConfigureAwait(false);
                if (reply.IsSuccessStatusCode)
                {
                    if (isPlayers)
                    {
                        PlayersHealth = $"reaching {_where}";
                    }
                    else
                    {
                        _sentMarkers = json;
                        _markersSentAt = DateTime.UtcNow;
                        MarkersHealth = $"reaching {_where}, {json.Length} bytes accepted";
                    }

                    if (_complained)
                    {
                        _log.Notification("[witchlight] the map service is taking live data again");
                        _complained = false;
                    }
                }
                else
                {
                    Complain(isPlayers, $"refused with {(int)reply.StatusCode}");
                }
            }
            catch (Exception error)
            {
                // The service being down is ordinary — it is a separate program
                // and the game does not depend on it.
                Complain(isPlayers, $"could not reach {_where}: {error.Message}");
            }
            finally
            {
                if (isPlayers)
                {
                    Interlocked.Exchange(ref _sendingPlayers, 0);
                }
                else
                {
                    Interlocked.Exchange(ref _sendingMarkers, 0);
                }
            }
        });
    }

    /// <summary>
    /// Where the service listens unless told otherwise. The service derives the
    /// same name from the same path, so neither side needs configuring; both say
    /// what they resolved, because a mismatch is otherwise silent.
    /// </summary>
    public static string DefaultSocket(string exports)
    {
        var full = Path.GetFullPath(exports).TrimEnd(Path.DirectorySeparatorChar);
        return $"/tmp/witchlight-{Tag(full):x8}.sock";
    }

    /// <summary>
    /// FNV-1a, 32 bits. Short, and simple enough that the service computes the
    /// same number from the same path without either side sharing code.
    /// </summary>
    private static uint Tag(string text)
    {
        unchecked
        {
            var hash = 0x811c9dc5u;
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                hash ^= b;
                hash *= 0x01000193u;
            }
            return hash;
        }
    }

    /// <summary>Says it once, and says when it stops being true.</summary>
    private void Complain(bool isPlayers, string what)
    {
        if (isPlayers)
        {
            PlayersHealth = what;
        }
        else
        {
            MarkersHealth = what;
        }

        if (_complained)
        {
            return;
        }

        _complained = true;
        _log.Warning(
            "[witchlight] {0}: {1} — that half will not show on the map until it is back",
            isPlayers ? "players" : "markers",
            what);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
