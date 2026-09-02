using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The private channel the map service pulls terrain on.
///
/// Everything else between the two halves is a file the mod writes on its own
/// clock, or a post the mod sends on its own clock — this is the one channel
/// where the service asks and the mod answers. What used to decide which
/// columns to ask the game for, in what order and how fast, lived here in the
/// mod as <c>Backfill</c>; the service holds the whole map already and is where
/// a request for one more column belongs, so this exists only to answer it.
/// <see cref="Repair"/> is unrelated to that and still lives in the mod: it
/// heals columns the map once held and lost, which is a narrower question this
/// channel has no part in.
///
/// The listener is loopback, on a port the machine picks, the same reasoning
/// <see cref="MapService"/>'s own channel already rests on — nothing off this
/// machine can reach it, and nothing on it can ask without a token published in
/// a file only this mod's owner can read. Published in `mod-api.json` beside the
/// map, the mirror of the service's own `api.json`.
/// </summary>
public sealed class ModApi : IDisposable
{
    private const string BindVariable = "WITCHLIGHT_MOD_API_BIND";
    private const string TokenVariable = "WITCHLIGHT_MOD_API_TOKEN";
    private const string Bearer = "Bearer ";

    private readonly HttpListener _listener;
    private readonly ICoreServerAPI _api;
    private readonly ILogger _log;
    private readonly string _token;
    private readonly string _host;
    private readonly System.Func<int, bool> _shows;
    private readonly Microblocks _chiselled;
    private readonly string _exports;
    private volatile bool _running;

    public ModApi(
        ICoreServerAPI api, ILogger log, string exports, System.Func<int, bool> shows, Microblocks chiselled)
    {
        _api = api;
        _log = log;
        _exports = exports;
        _shows = shows;
        _chiselled = chiselled;

        var bind = Environment.GetEnvironmentVariable(BindVariable);
        _host = string.IsNullOrEmpty(bind) ? "127.0.0.1" : bind;
        _token = Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } given
            ? given
            : RandomNumberGenerator.GetHexString(32, lowercase: true);

        _listener = new HttpListener();
    }

    /// <summary>Starts listening, and publishes where. Safe to call once.</summary>
    public void Start()
    {
        // `HttpListener` takes a prefix, not a socket, and has no way to ask what
        // port a wildcard bound to — so a free one is found the way anything else
        // on this machine would, by asking a socket for one and closing it before
        // the listener claims the same number. A window exists between the two
        // where something else could take it; a service on a box with something
        // else furiously binding ephemeral ports has larger problems than this.
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Parse(_host), 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        _listener.Prefixes.Add($"http://{_host}:{port}/");
        _listener.Start();
        _running = true;

        Publish(port);
        _log.Notification("[witchlight] the map service may pull terrain from {0}", $"http://{_host}:{port}/");

        Task.Run(Loop);
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception) when (!_running)
            {
                // The listener was told to stop while a call was waiting on it.
                return;
            }
            catch (Exception error)
            {
                _log.Warning("[witchlight] terrain pull listener error: {0}", error.Message);
                continue;
            }

            // One request at a time is enough: a column request is a handful of
            // main-thread work, and the service already paces how many it asks
            // for at once. A thread apiece would only let more of them queue up
            // on the main thread behind one another regardless.
            Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            if (!Authorized(context.Request))
            {
                Respond(context, 401, "{}");
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "";
            var parts = path.Trim('/').Split('/');

            if (parts.Length == 3 && parts[0] == "column"
                && int.TryParse(parts[1], out var cx) && int.TryParse(parts[2], out var cz))
            {
                HandleColumn(context, cx, cz);
                return;
            }

            if (parts.Length == 3 && parts[0] == "exists"
                && int.TryParse(parts[1], out var ex) && int.TryParse(parts[2], out var ez))
            {
                HandleExists(context, ex, ez);
                return;
            }

            if (parts.Length == 3 && parts[0] == "load" && context.Request.HttpMethod == "POST"
                && int.TryParse(parts[1], out var lx) && int.TryParse(parts[2], out var lz))
            {
                HandleLoad(context, lx, lz);
                return;
            }

            Respond(context, 404, "{}");
        }
        catch (Exception error)
        {
            _log.Warning("[witchlight] terrain pull request failed: {0}", error.Message);
            try
            {
                Respond(context, 500, "{}");
            }
            catch (Exception)
            {
                // The response could not be sent either. Nothing left to do.
            }
        }
    }

    /// <summary>
    /// One column's surface, read on the main thread because everything the game
    /// answers this with — the map chunk, the blocks under it — is only safe to
    /// read there.
    /// </summary>
    private void HandleColumn(HttpListenerContext context, int cx, int cz)
    {
        OnGameThread(() =>
        {
            var record = ColumnPump.ReadOne(_api, cx, cz, _shows, _chiselled);
            if (record is null)
            {
                Respond(context, 404, "{}");
                return;
            }

            Respond(context, 200, JsonConvert.SerializeObject(new
            {
                X = cx,
                Z = cz,
                Record = Convert.ToBase64String(record),
            }));
        });
    }

    private void HandleExists(HttpListenerContext context, int cx, int cz)
    {
        _api.WorldManager.TestMapChunkExists(cx, cz, saved =>
            Respond(context, 200, JsonConvert.SerializeObject(new { Exists = saved })));
    }

    private void HandleLoad(HttpListenerContext context, int cx, int cz)
    {
        OnGameThread(() => _api.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
        {
            OnLoaded = () => Respond(context, 200, "{}"),
        }));
    }

    /// <summary>
    /// Runs game-API work on the server's main thread, the way <see cref="Repair"/>
    /// already had to: the game's own chunk and column reads are not safe from a
    /// thread pool worker.
    /// </summary>
    private void OnGameThread(Action work) => _api.Event.EnqueueMainThreadTask(work, "witchlight-terrain-pull");

    private bool Authorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var offered = header[Bearer.Length..].Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(_token));
    }

    private static void Respond(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    private void Publish(int port)
    {
        var body = JsonConvert.SerializeObject(new
        {
            Port = port,
            Token = _token,
            Version = typeof(ModApi).Assembly.GetName().Version?.ToString() ?? "",
            // What the game itself already limits chunk loading to, so the
            // puller's own default reach is never wider than ground a player
            // standing there could ever have caused to load in the first place.
            MaxChunkRadius = _api.Server.Config.MaxChunkRadius,
        });
        Disk.Write(PathIn(_exports), body);
    }

    /// <summary>Where the service should look to learn where this is listening.</summary>
    public static string PathIn(string exports) => System.IO.Path.Combine(exports, "mod-api.json");

    public void Dispose()
    {
        _running = false;
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // Already stopped, or never started. Either is fine to dispose.
        }

        try
        {
            System.IO.File.Delete(PathIn(_exports));
        }
        catch (Exception)
        {
            // A published address nothing will read again is harmless left
            // behind — the next start overwrites it — but worth clearing so a
            // stale file cannot outlive the listener it named.
        }
    }
}
