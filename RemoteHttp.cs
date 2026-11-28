using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CuePlayer;

/// <summary>
/// Remote control over HTTP (Peter 2026-11-27: HTTP only, the player never sends anything by itself - clients ask).
/// Off by default. "local" listens on 127.0.0.1 / ::1 only, "network" on all interfaces, optionally only for listed client IPs.
/// Commands work with GET (Companion "Generic HTTP", vMix scripts, a browser link) and POST; GET /api/state and /api/cues give the state.
/// A small HTTP/1.1 server of its own on a TcpListener: HttpListener needs a URL ACL (admin) for anything but localhost.
/// Commands go into the engine's queue like the window's buttons; nothing here touches libVLC.
/// </summary>
internal sealed class RemoteHttp : IDisposable
{
    public const string Off = "off", Local = "local", Network = "network";
    public const int DefaultPort = 8095;
    private const int MaxHeaderBytes = 8192, MaxBodyBytes = 65536, MaxClients = 32;

    private readonly PlayerEngine _engine;
    private readonly ShowStore _store;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _clients = new(MaxClients);
    private List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cts;
    private HashSet<IPAddress> _allow = [];
    private string _key = "";

    /// <summary>Shown in the settings: "off", the address clients use, or why it does not listen.</summary>
    public string Status { get => _status; private set { _status = value; _engine.RemoteStatus = value; } }
    private string _status = Off;

    public RemoteHttp(PlayerEngine engine, ShowStore store) { _engine = engine; _store = store; }

    public static string NormalizeMode(string? m) => m is Local or Network ? m : Off;
    public static int NormalizePort(int p) => p is >= 1024 and <= 65535 ? p : DefaultPort;

    /// <summary>Starts, restarts or stops the server for these settings (nothing happens when they did not change).</summary>
    public void Apply(ShowSettings s)
    {
        string mode = NormalizeMode(s.RemoteMode);
        int port = NormalizePort(s.RemotePort);
        string allow = mode == Network ? (s.RemoteAllow ?? "").Trim() : "";
        lock (_sync)
        {
            string key = $"{mode}|{port}|{allow}";
            if (key == _key) return;
            _key = key;
            StopListening();
            if (mode == Off) { Status = Off; Log.Write("remote http: off"); return; }

            var allowSet = new HashSet<IPAddress>();
            foreach (var part in allow.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (IPAddress.TryParse(part, out var ip)) allowSet.Add(Plain(ip));
                else { Status = $"not started: '{part}' is not an IP address"; Log.Write("remote http: " + Status); return; }
            }
            _allow = allowSet;

            var listeners = new List<TcpListener>();
            try
            {
                if (mode == Local)
                {
                    listeners.Add(Listen(new TcpListener(IPAddress.Loopback, port)));
                    try { listeners.Add(Listen(new TcpListener(IPAddress.IPv6Loopback, port))); } catch (SocketException) { }   // no IPv6: 127.0.0.1 is enough
                }
                else
                {
                    var l = new TcpListener(IPAddress.IPv6Any, port);
                    l.Server.DualMode = true;   // IPv4 and IPv6 on one socket
                    listeners.Add(Listen(l));
                }
            }
            catch (SocketException e)
            {
                foreach (var l in listeners) l.Stop();
                // with ExclusiveAddressUse a port another program holds gives AccessDenied (10013), not AddressAlreadyInUse (measured);
                // 10013 also comes for ports Windows reserves (Hyper-V / WSL excluded ranges)
                Status = e.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => $"port {port} is already in use",
                    SocketError.AccessDenied => $"port {port} is in use by another program or reserved by Windows",
                    _ => $"not started on port {port} (socket error {(int)e.SocketErrorCode})",
                };
                Log.Write("remote http: " + Status);
                return;
            }
            _listeners = listeners;
            _cts = new CancellationTokenSource();
            foreach (var l in listeners) _ = AcceptLoop(l, _cts.Token);
            string host = mode == Local ? "127.0.0.1" : LanAddress() ?? "this PC";
            Status = $"http://{host}:{port}/api" + (mode == Network && allowSet.Count > 0 ? $" (only {allowSet.Count} IP{(allowSet.Count == 1 ? "" : "s")})" : "");
            Log.Write($"remote http: {mode} on port {port}, allowed: {(allowSet.Count == 0 ? "all" : string.Join(", ", allowSet))}");
        }
    }

    private static TcpListener Listen(TcpListener l)
    {
        l.Server.ExclusiveAddressUse = true;   // a second program on the same port must fail, not share it
        l.Start();
        return l;
    }

    private void StopListening()
    {
        _cts?.Cancel();
        _cts = null;
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
        _listeners = [];
    }

    public void Dispose() { lock (_sync) { StopListening(); _key = ""; Status = Off; } }

    private static IPAddress Plain(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>The first IPv4 address of a working network card (for the address shown in the settings).</summary>
    private static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(n => n.GetIPProperties().GatewayAddresses.Count == 0 ? 1 : 0)   // cards with a gateway first (the real LAN)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))?.ToString();
        }
        catch { return null; }
    }

    // ================= connections =================

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }   // stopped
            if (!_clients.Wait(0)) { client.Dispose(); continue; }   // too many at once
            _ = Task.Run(async () =>
            {
                try { await Serve(client, ct); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                catch (Exception e) { Log.Write("remote http: " + e.Message); }
                finally { client.Dispose(); _clients.Release(); }
            });
        }
    }

    /// <summary>One connection: requests one after the other (keep-alive), closed after 10 s without a request.</summary>
    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint is IPEndPoint ep ? Plain(ep.Address) : IPAddress.None;
        client.NoDelay = true;
        using var stream = client.GetStream();
        var allow = _allow;
        bool refused = allow.Count > 0 && !IPAddress.IsLoopback(remote) && !allow.Contains(remote);
        var buf = new byte[MaxHeaderBytes];
        int have = 0;
        while (!ct.IsCancellationRequested)
        {
            // read up to the end of the header (the bytes after it may be the next request)
            int end;
            while ((end = HeaderEnd(buf, have)) < 0)
            {
                if (have == buf.Length) { await Respond(stream, 431, Json(new { ok = false, error = "header too large" }), false, false, ct); return; }
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(have == 0 ? 10_000 : 5_000);
                int n = await stream.ReadAsync(buf.AsMemory(have), idle.Token);
                if (n == 0) return;   // client closed
                have += n;
            }
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in lines.Skip(1))
            {
                int c = h.IndexOf(':');
                if (c > 0) headers[h[..c].Trim()] = h[(c + 1)..].Trim();
            }
            int used = end + 4;
            // a body (POST) is read and ignored: all parameters are in the address
            long bodyLen = headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var bl) ? bl : 0;
            if (bodyLen < 0 || bodyLen > MaxBodyBytes || headers.ContainsKey("Transfer-Encoding"))
            {
                await Respond(stream, 413, Json(new { ok = false, error = "body not supported, put the parameters in the address" }), false, false, ct);
                return;
            }
            long skip = bodyLen;
            int inBuf = (int)Math.Min(skip, have - used);
            used += inBuf; skip -= inBuf;
            while (skip > 0)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(skip, buf.Length)), ct);
                if (n == 0) return;
                skip -= n;
            }
            Buffer.BlockCopy(buf, used, buf, 0, have - used);
            have -= used;

            bool http10 = first.Length > 2 && first[2] == "HTTP/1.0";
            bool keep = headers.TryGetValue("Connection", out var conn)
                ? !conn.Equals("close", StringComparison.OrdinalIgnoreCase) && !(http10 && !conn.Equals("keep-alive", StringComparison.OrdinalIgnoreCase))
                : !http10;
            if (first.Length < 3) { await Respond(stream, 400, Json(new { ok = false, error = "bad request" }), false, false, ct); return; }
            // refused only after the request was read: closing with unread bytes resets the connection and the client never sees the 403
            if (refused)
            {
                await Respond(stream, 403, Json(new { ok = false, error = $"{remote} is not allowed (player settings: Remote)" }), false, false, ct);
                Log.Write($"remote http: refused {remote}");
                return;
            }
            string method = first[0].ToUpperInvariant();
            int status; string body;
            // a request a browser makes for ANOTHER web page (a picture or form pointing at the player): refused, else any page open on
            // this PC could press GO / Stop (review 2026-11-27). Browsers mark those with Sec-Fetch-Site cross-site / same-site;
            // Companion, vMix, curl and an address typed into the browser do not ("none" or no header at all).
            if (headers.TryGetValue("Sec-Fetch-Site", out var site) && site is "cross-site" or "same-site")
            {
                Log.Write($"remote http {remote}: refused a request from a web page ({site}) {first[1]}");
                await Respond(stream, 403, Json(new { ok = false, error = "requests from other web pages are not accepted" }), false, false, ct);
                return;
            }
            if (method is "GET" or "POST" or "HEAD")
            {
                try { (status, body) = Handle(method, first[1], remote); }
                catch (Exception e) { Log.Write("remote http: " + e); (status, body) = (500, Json(new { ok = false, error = e.Message })); }
            }
            else (status, body) = (405, Json(new { ok = false, error = "use GET or POST" }));
            await Respond(stream, status, body, keep, method == "HEAD", ct);
            if (!keep) return;
        }
    }

    private static int HeaderEnd(byte[] b, int len)
    {
        for (int i = 3; i < len; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n') return i - 3;
        return -1;
    }

    private static async Task Respond(NetworkStream s, int status, string body, bool keep, bool headOnly, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 413 => "Payload Too Large", 431 => "Request Header Fields Too Large", _ => "Error" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {data.Length}\r\n" +
            $"Cache-Control: no-store\r\nConnection: {(keep ? "keep-alive" : "close")}\r\n" + (status == 405 ? "Allow: GET, POST, HEAD\r\n" : "") + "\r\n");
        await s.WriteAsync(head, ct);
        if (!headOnly) await s.WriteAsync(data, ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);

    // ================= the API =================

    private static readonly string[] Endpoints =
    [
        "GET  /api/state                       what is on air, next, cued, times (poll this)",
        "GET  /api/cues                        the cue list",
        "GET  /api/cue/{n}  or  /api/cue/id/{id}   one cue (n = position in the list, 1 = first)",
        "GET|POST /api/go                      play the NEXT cue (like the GO button)",
        "GET|POST /api/stop                    stop, cue the next one",
        "GET|POST /api/pause  /api/resume  /api/togglepause",
        "GET|POST /api/next  /api/prev         play the next / previous cue",
        "GET|POST /api/last10                  jump to 10 s before the end",
        "GET|POST /api/seek/{ms}               jump in the cue on air",
        "GET|POST /api/fade/{ms}               fade time of the playlist (100-1000)",
        "GET|POST /api/cue/{n}/play            play this cue now (cut)",
        "GET|POST /api/cue/{n}/select          make this cue NEXT (stopped: its first picture goes on air)",
        "GET|POST /api/cue/{n}/endaction/{a}   a = hold, black, next, fade, loop, loopfade",
        "GET|POST /api/cue/{n}/endaction/jump/{m}   at the end jump to cue m (jumpfade/{m}: with a fade)",
        "  (every /api/cue/{n}/... also as /api/cue/id/{id}/...)",
    ];

    private (int, string) Handle(string method, string target, IPAddress remote)
    {
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (seg.Count > 0 && seg[0].Equals("api", StringComparison.OrdinalIgnoreCase)) seg.RemoveAt(0);
        else if (seg.Count > 0) return NotFound(path);
        string Word(int i) => i < seg.Count ? seg[i].ToLowerInvariant() : "";

        if (seg.Count == 0) return Ok(new { name = MainForm.AppTitle, version = Version, endpoints = Endpoints });
        switch (Word(0))
        {
            case "state" when seg.Count == 1: return Ok(State());
            case "cues" when seg.Count == 1:
            {
                var cues = Cues();
                return Ok(new { count = cues.Count, cues = cues.Select((c, i) => CueInfo(c, i, cues)) });
            }
            case "go" when seg.Count == 1: _engine.Go(); return Done("go", remote);
            case "stop" when seg.Count == 1: _engine.Stop(); return Done("stop", remote);
            case "pause" when seg.Count == 1: _engine.SetPaused(true); return Done("pause", remote);
            case "resume" when seg.Count == 1: _engine.SetPaused(false); return Done("resume", remote);
            case "togglepause" when seg.Count == 1: _engine.TogglePause(); return Done("togglepause", remote);
            case "next" when seg.Count == 1: _engine.Next(); return Done("next", remote);
            case "prev" when seg.Count == 1: _engine.Prev(); return Done("prev", remote);
            case "last10" when seg.Count == 1: _engine.LastTen(); return Done("last10", remote);
            case "seek" when seg.Count == 2:
                if (!long.TryParse(seg[1], out var ms) || ms < 0) return Bad("seek needs milliseconds, e.g. /api/seek/30000");
                _engine.Seek(ms);
                return Done($"seek {ms}", remote);
            case "fade" when seg.Count == 2:
                if (!int.TryParse(seg[1], out var f)) return Bad("fade needs milliseconds, e.g. /api/fade/500");
                int fade = Fade.Normalize(f);
                _store.Change(s => { s.FadeMs = fade; s.Modified = true; });
                return Done($"fade {fade}", remote);
            case "cue": return CueCommand(seg, remote);
        }
        return NotFound(path);
    }

    private (int, string) CueCommand(List<string> seg, IPAddress remote)
    {
        // /cue/{n}/... or /cue/id/{id}/... (ids are 8 hex digits and may look like a number, so they get their own word)
        var cues = Cues();
        int at; int i;
        if (seg.Count >= 3 && seg[1].Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            at = cues.FindIndex(c => c.Id.Equals(seg[2], StringComparison.OrdinalIgnoreCase));
            i = 3;
            if (at < 0) return (404, Json(new { ok = false, error = $"no cue with id '{seg[2]}'" }));
        }
        else if (seg.Count >= 2 && int.TryParse(seg[1], out var n))
        {
            at = n - 1;
            i = 2;
            if (at < 0 || at >= cues.Count) return (404, Json(new { ok = false, error = $"no cue {n} (the list has {cues.Count})" }));
        }
        else return Bad("use /api/cue/{n}/... (n = position, 1 = first) or /api/cue/id/{id}/...");

        var cue = cues[at];
        string verb = i < seg.Count ? seg[i].ToLowerInvariant() : "";
        string label = $"cue {at + 1} '{cue.Name}'";
        switch (verb)
        {
            case "" when seg.Count == i: return Ok(CueInfo(cue, at, cues));
            case "play" when seg.Count == i + 1: _engine.Play(cue.Id); return Done($"play {label}", remote);
            case "select" when seg.Count == i + 1: _engine.Select(cue.Id); return Done($"select {label}", remote);
            case "endaction" when seg.Count == i + 3 && seg[i + 1].ToLowerInvariant() is "jump" or "jumpfade":
            {
                // jump to cue m (its position now; stored by id, so later reordering keeps the target)
                if (!int.TryParse(seg[i + 2], out var m) || m < 1 || m > cues.Count) return Bad($"jump target: a cue number 1-{cues.Count}");
                string ja = seg[i + 1].ToLowerInvariant() == "jump" ? EndAction.Jump : EndAction.JumpFade, target = cues[m - 1].Id;
                _store.Change(s => { var c = s.Cues.FirstOrDefault(c => c.Id == cue.Id); if (c != null) { c.EndAction = ja; c.JumpTo = target; s.Modified = true; } });
                return Done($"endaction {ja} -> cue {m} {label}", remote);
            }
            case "endaction" when seg.Count == i + 2:
                string? a = seg[i + 1].ToLowerInvariant() switch
                {
                    "hold" => EndAction.Hold, "black" or "stop" => EndAction.Stop, "next" or "cut" => EndAction.Next,
                    "fade" => EndAction.Fade, "loop" => EndAction.Loop, "loopfade" => EndAction.LoopFade, _ => null,
                };
                if (a == null) return Bad("end action: hold, black, next, fade, loop, loopfade, or jump/{m} / jumpfade/{m}");
                _store.Change(s => { var c = s.Cues.FirstOrDefault(c => c.Id == cue.Id); if (c != null) { c.EndAction = a; s.Modified = true; } });
                return Done($"endaction {a} {label}", remote);
        }
        return Bad($"unknown cue command '{string.Join('/', seg.Skip(i))}' (play, select, endaction/...)");
    }

    private List<Cue> Cues() => _store.Read(s => s.Cues.ToList());

    private static string Version => typeof(RemoteHttp).Assembly.GetName().Version?.ToString() ?? "";

    private static (int, string) Ok(object o) => (200, Json(o));
    private static (int, string) Bad(string why) => (400, Json(new { ok = false, error = why }));
    private static (int, string) NotFound(string path) => (404, Json(new { ok = false, error = $"unknown address {path} - see /api" }));

    private static (int, string) Done(string what, IPAddress remote)
    {
        Log.Write($"remote http {remote}: {what}");
        return (200, Json(new { ok = true, command = what }));
    }

    private static object CueInfo(Cue c, int index, List<Cue> all) => new
    {
        number = index + 1,
        id = c.Id,
        name = c.Name,
        durationMs = c.DurationMs,
        endAction = c.EndAction,
        // jump / jumpfade: number of the target cue (null = no target, or it was removed: the cue then holds)
        jumpTo = EndAction.IsJump(c.EndAction) && all.FindIndex(x => x.Id == c.JumpTo) is >= 0 and var j ? j + 1 : (int?)null,
        isStill = c.IsStill,
        missing = c.Missing,
        unsupported = c.Unsupported != null,
    };

    /// <summary>
    /// The state for polling. Times are taken at the moment of the answer; serverTimeMs lets a client run the clock on between polls.
    /// countdown = whole seconds 10..1 in the last 10 s of a cue that does not loop (like the big number in the window), else null.
    /// </summary>
    private object State()
    {
        var (cues, listPath, modified, fadeMs) = _store.Read(s => (s.Cues.ToList(), s.PlaylistPath, s.Modified, Fade.Normalize(s.FadeMs)));
        var p = _engine.ReadPlayState(cues);
        object? Info(string? id)
        {
            int i = id == null ? -1 : cues.FindIndex(c => c.Id == id);
            return i < 0 ? null : CueInfo(cues[i], i, cues);
        }
        object? onAir = null;
        int cur = p.CurrentId == null ? -1 : cues.FindIndex(c => c.Id == p.CurrentId);
        if (cur >= 0)
        {
            var c = cues[cur];
            long? remaining = p.LengthMs > 0 ? Math.Max(0, p.LengthMs - p.PositionMs) : null;
            bool loops = c.EndAction is EndAction.Loop or EndAction.LoopFade || (c.IsStill && c.StillSeconds == 0)
                         || (EndAction.IsJump(c.EndAction) && c.JumpTo == c.Id);   // a jump to itself is a loop
            int? countdown = !loops && remaining is > 0 and <= 10_000 && p.State is "playing" or "paused" ? (int)Math.Ceiling(remaining.Value / 1000.0) : null;
            onAir = new
            {
                number = cur + 1, id = c.Id, name = c.Name, endAction = c.EndAction, isStill = c.IsStill,
                positionMs = p.PositionMs, lengthMs = p.LengthMs, remainingMs = remaining, countdown,
            };
        }
        // NEXT = the chosen cue, or the first one (like the list in the window)
        string? nextId = p.SelectedId != null && cues.Any(c => c.Id == p.SelectedId) ? p.SelectedId : cues.FirstOrDefault()?.Id;
        return new
        {
            version = Version,
            serverTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            state = p.State,   // stopped, starting, playing, paused, ended (= last picture held)
            onAir,
            next = Info(nextId),
            cued = Info(p.CuedId),          // stopped: this cue's first picture is on the outputs
            incoming = Info(p.IncomingId),  // starting / fading in
            fading = p.Fading,
            playlist = new { name = MainForm.PlaylistName(listPath), count = cues.Count, modified, fadeMs },
            outputs = _engine.OutputStatus(),
        };
    }
}
