using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace CuePlayer;

/// <summary>
/// Plays the cues with two libVLC decks (A/B) and sends the result as NDI and OMT.
/// libVLC decodes and paces each file and hands every picture (UYVY, already at output size) and the sound to the deck.
/// A separate output thread runs on its own clock at the output frame rate: it sends the newest picture of the deck on air
/// (repeating it when the media is slower, black when stopped) and exactly one frame's worth of sound per picture.
/// Transitions: the next cue starts on the other deck; the output switches when that deck has its first picture (cut), or mixes
/// both decks over the fade time (fade). For "Cut next"/"Fade next" the next cue is prerolled (loaded and paused at its first picture)
/// a few seconds before the end, so the change is gapless.
/// </summary>
internal sealed class PlayerEngine : IDisposable
{
    private const int SampleRate = Deck.SampleRate, MaxChannels = Deck.MaxChannels;
    public const int PreviewWidth = 640, PreviewHeight = 360;   // every output format is an integer multiple (2x, 3x, 6x)
    private const long PrerollLeadMs = 2500;                    // load the next cue this long before it is needed

    private readonly ShowStore _store;
    private readonly LibVLC _vlc;
    private readonly Deck[] _decks;

    // all player commands run one after another on this thread (libVLC calls can block for a moment, and must never run inside a libVLC event)
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _commandThread;
    private readonly System.Threading.Timer _autoTimer;
    private int _autoQueued;

    // ---- which deck is on air (guarded by _switchSync; changed by commands and by the output thread when a transition completes) ----
    private readonly object _switchSync = new();
    private Deck _active;
    private Deck? _incoming;
    private bool _incomingFade;
    private bool _fadeReset;
    private bool _showBlack = true;

    // ---- output buffers (black, fade mix, preview copy) ----
    private OutputFormat _fmt = OutputFormat.All[0];
    private IntPtr _black, _mix, _previewSrc;

    // ---- cued picture: stopped + a cue chosen -> its first picture is on the outputs ----
    private volatile bool _cued;            // set by choosing a cue, cleared by Stop / "Black" / playing
    private IntPtr _cuedFrame;              // copy of the settled first picture (output thread only)
    private long _cuedKey = -1;
    private volatile string? _cuedCueId;    // cue whose picture is in _cuedFrame (null = none yet; FrozenId = last picture frozen by Stop)
    private const string FrozenId = "(frozen)";
    private int _snapRequest;               // Stop asks the output thread to freeze the picture on air into _cuedFrame
    private readonly ManualResetEventSlim _snapDone = new(false);
    private float _gain = 1f;
    private volatile int _outChannels = 2;   // audio channels sent right now (2, or up to 8 for a cue on "1-8")
    private volatile string? _lastDecodeError;

    private readonly Deck _checker;
    private static readonly OutputFormat CheckFormat = new("check", 160, 90, 25, 1, false);
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    /// <summary>
    /// Plays a file with a video track on the small check player until its first picture (up to 4 s). No picture = the format is not
    /// supported by libVLC (e.g. VMX1 from vMix): returns the reason, else null. One check at a time.
    /// </summary>
    public async Task<string?> CheckDecodableAsync(string path, string? thumbName = null)
    {
        await _checkLock.WaitAsync();
        try
        {
            _lastDecodeError = null;
            var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPicture(Deck d, int s) => got.TrySetResult(true);
            _checker.PrerollPicture += OnPicture;
            try
            {
                _checker.Load(_vlc, new Cue { Path = path, Name = Path.GetFileName(path), HasVideo = true }, CheckFormat, preroll: true);
                bool ok = await Task.WhenAny(got.Task, Task.Delay(4000)) == got.Task;
                // the first picture (160x90, letterboxed like on air) is also the thumbnail of the cue list
                if (ok && thumbName != null)
                {
                    var buf = Marshal.AllocHGlobal(Thumbs.Width * Thumbs.Height * 2);
                    try { if (_checker.CopyFirstPicture(buf, Thumbs.Width * Thumbs.Height * 2)) Thumbs.SaveUyvy(buf, thumbName); }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return ok ? null : (_lastDecodeError ?? "no picture could be decoded");
            }
            finally
            {
                _checker.PrerollPicture -= OnPicture;
                await Task.Run(() => _checker.Stop());   // never stop a player on a libVLC thread
            }
        }
        finally { _checkLock.Release(); }
    }
    private float _transitionFade = 0.3f;   // seconds of the running fade (guarded by _switchSync), taken from the playlist when it starts

    // ---- outputs ----
    private Thread? _sendThread;
    private CancellationTokenSource? _sendStop;
    private NdiSender? _ndi;
    private OmtSender? _omt;
    private string _ndiStatus = "", _omtStatus = "";
    private SdiOutput? _sdi;
    private volatile string _sdiStatus = "off";
    private List<string> _sdiPorts = new();
    private volatile AudioMonitor? _monitor;
    private string _monitorId = "", _monitorStatus = "off";
    private List<(string id, string name)> _monitorDevices = new();

    // ---- state ----
    private volatile string? _selectedId;
    private volatile string _message = "";

    // ---- measurements ----
    private int _sentThisSecond, _skipped;
    private volatile int _sentFps, _decodedFps;

    // ---- preview: every new output picture (at the media's rate, every picture during a fade) as RGBA 640x360 for the window ----
    private readonly object _previewSync = new();
    private readonly byte[][] _previewRgba = [new byte[PreviewWidth * PreviewHeight * 4], new byte[PreviewWidth * PreviewHeight * 4]];
    private int _previewHalf, _previewSerial;
    private volatile bool _previewBusy;
    private readonly AutoResetEvent _previewWake = new(false);
    private readonly Thread _previewThread;
    private volatile bool _disposed;

    /// <summary>Raised on the preview thread with the new picture (RGBA, PreviewWidth x PreviewHeight), its double-buffer half and serial.
    /// The array is reused two pictures later, so copy it at once.</summary>
    public event Action<byte[], int, int>? PreviewReady;

    // libVLC clock (microseconds) - the time base of the audio timestamps
    private static unsafe delegate* unmanaged[Cdecl]<long> _vlcClock;

    public PlayerEngine(ShowStore store)
    {
        _store = store;
        _vlc = new LibVLC("--no-osd", "--no-video-title-show", "--no-sub-autodetect-file", "--no-spu", "--no-stats", "--no-snapshot-preview");
        _vlc.Log += (_, e) =>
        {
            if (e.Level != LogLevel.Error) return;
            Log.Write($"vlc {e.Module}: {e.Message}");
            // e.g. 'VLC could not decode the format "VMX1"' (vMix/OMT codec): shown when a cue gives no picture
            if (e.Message?.Contains("could not decode the format", StringComparison.OrdinalIgnoreCase) == true) _lastDecodeError = e.Message;
        };
        LoadVlcClock();

        _decks = [new Deck(_vlc, "A"), new Deck(_vlc, "B")];
        // a third, small player that only checks whether a new file's picture can be decoded (never on air)
        _checker = new Deck(_vlc, "check");
        _checker.Allocate(CheckFormat);
        foreach (var d in _decks)
        {
            d.Ended += (deck, serial) => Post(() => OnEnd(deck, serial));
            d.Failed += (deck, serial) => Post(() => { _message = "Playback error"; OnEnd(deck, serial); });
            d.PrerollPicture += (deck, serial) => Post(() => OnPrerollPicture(deck, serial));
        }
        _active = _decks[0];

        _commandThread = new Thread(CommandLoop) { IsBackground = true, Name = "player commands" };
        _commandThread.Start();
        _previewThread = new Thread(PreviewLoop) { IsBackground = true, Name = "preview", Priority = ThreadPriority.BelowNormal };
        _previewThread.Start();
        _commands.Add(RebuildOutputs);
        // watches the deck on air for the preroll / fade moments of "Cut next" and "Fade next"
        _autoTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _autoQueued, 1) == 0) _commands.Add(() => { Interlocked.Exchange(ref _autoQueued, 0); CheckAuto(); });
        }, null, 500, 40);
    }

    private static unsafe void LoadVlcClock()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "libvlc.dll");
        if (NativeLibrary.TryLoad(path, out var lib) && NativeLibrary.TryGetExport(lib, "libvlc_clock", out var fn))
            _vlcClock = (delegate* unmanaged[Cdecl]<long>)fn;
        else
            Log.Write("libvlc_clock not found, audio timing falls back to arrival time");
    }

    private static unsafe long VlcNow() => _vlcClock != null ? _vlcClock() : Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

    private void CommandLoop()
    {
        foreach (var a in _commands.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception e) { Log.Write("command failed: " + e); _message = e.Message; }
        }
    }

    private Deck Other(Deck d) => d == _decks[0] ? _decks[1] : _decks[0];
    private Cue? FindCue(string? id) => id == null ? null : _store.Read(s => s.Cues.FirstOrDefault(c => c.Id == id));

    // ================= commands (public, queued) =================

    /// <summary>From libVLC's threads: queue for the command thread; while shutting down the queue is closed and the event is dropped
    /// (an Add there threw inside libVLC's picture callback and ended the program at exit).</summary>
    private void Post(Action a)
    {
        try { if (!_commands.IsAddingCompleted) _commands.Add(a); } catch (InvalidOperationException) { }
    }

    public void Play(string id) => _commands.Add(() => PlayNow(id, false));
    public void Go() => _commands.Add(GoNow);
    /// <summary>
    /// The Stop button (Peter): the cue on air stops, the NEXT cue is chosen and its first picture goes on the outputs (cued, ready for GO).
    /// The end action "Black" uses StopNow instead and stays black.
    /// </summary>
    public void Stop() => _commands.Add(() =>
    {
        var current = CurrentId();
        StopToCue(current != null ? Neighbour(current, +1) ?? _selectedId : _selectedId, "stop");
    });

    /// <summary>
    /// Stops what is on air and cues <paramref name="next"/> (its first picture goes on the outputs, ready for GO).
    /// The picture on air stays frozen until that first picture is ready, so there is no black in between. No next cue = black.
    /// Used by Stop and by choosing another cue while the last picture is held.
    /// </summary>
    private void StopToCue(string? next, string why)
    {
        var o = Other(Active());
        bool keep = next != null && o.CueId == next && o.Mode is DeckMode.Preroll or DeckMode.Prerolled;   // already loaded: keep it
        bool onAir; lock (_switchSync) onAir = !_showBlack;
        if (next != null && onAir)
        {
            // freeze the picture on air until the next cue's first picture is ready (loading takes ~0.5 s - no black in between)
            _snapDone.Reset();
            Interlocked.Exchange(ref _snapRequest, 1);
            _snapDone.Wait(150);
        }
        else _cuedCueId = null;
        _cued = next != null;
        lock (_switchSync) { _incoming = null; _showBlack = true; }
        foreach (var d in _decks) if (!(keep && d == o)) d.Stop();
        if (next != null) _selectedId = next;
        Log.Write($"{why} -> cued {FindCue(next)?.Name}");
    }
    public void TogglePause() => _commands.Add(TogglePauseNow);
    public void Next() => _commands.Add(() => { var id = Neighbour(CurrentId() ?? _selectedId, +1); if (id != null) PlayNow(id, false); });
    public void Prev() => _commands.Add(() => { var id = Neighbour(CurrentId() ?? _selectedId, -1); if (id != null) PlayNow(id, false); });
    public void Seek(long ms) => _commands.Add(() => { var a = Active(); if (a.Mode is DeckMode.Playing or DeckMode.Paused) a.SeekTo(ms); });
    /// <summary>Jumps to 10 s before the end of the cue on air and plays on (also out of a pause). Clips of 10 s or less: nothing.</summary>
    public void LastTen() => _commands.Add(() =>
    {
        var a = Active();
        if (a.Mode is not (DeckMode.Playing or DeckMode.Paused)) return;
        long len = a.LengthMs, target = len - 10_000;
        if (len <= 10_000 || a.SmoothTimeMs >= target) return;
        a.SeekTo(target);
        if (a.Mode == DeckMode.Paused) a.Pause(false);
        Log.Write($"last 10 s: {FindCue(a.CueId)?.Name} -> {target} ms");
    });
    /// <summary>Choosing a cue (click, arrow keys). While stopped this puts its first picture on the outputs.</summary>
    public void Select(string id)
    {
        _selectedId = id;
        _cued = true;
        // the last picture is held (end action Hold): choosing another cue ends the hold and cues it (Peter)
        _commands.Add(() =>
        {
            Deck a; Deck? inc; bool black;
            lock (_switchSync) { a = _active; inc = _incoming; black = _showBlack; }
            if (!black && inc == null && a.Mode == DeckMode.Ended && _selectedId == id) StopToCue(id, "hold released");
        });
    }
    public void ApplySettings() => _commands.Add(() => { StopNow(); RebuildOutputs(); });

    /// <summary>Opens the listening device from the settings (or closes it); the outputs keep running.</summary>
    public void ApplyMonitor() => _commands.Add(() => ApplyMonitorNow(force: true));

    private void ApplyMonitorNow(bool force)
    {
        _monitorDevices = AudioMonitor.Devices();
        string id = _store.Read(s => s.Settings.MonitorDevice) ?? "";
        if (!force && id == _monitorId && (_monitor != null || id == "")) return;
        var old = _monitor; _monitor = null; old?.Dispose();
        _monitorId = id;
        _monitorStatus = "off";
        if (id == "") return;
        if (!_monitorDevices.Any(d => d.id == id)) { _monitorStatus = "device not found"; return; }
        try { _monitor = new AudioMonitor(id); _monitorStatus = "on"; Log.Write("monitor on " + _monitor.DeviceName); }
        catch (Exception e) { _monitorStatus = "failed: " + e.Message; Log.Write("monitor failed: " + e); }
    }
    public void SetGain(double db) => _gain = (float)Math.Pow(10, db / 20.0);

    private Deck Active() { lock (_switchSync) return _active; }
    private string? CurrentId() { lock (_switchSync) return _showBlack && _incoming == null ? null : _active.CueId; }

    private void GoNow()
    {
        var cues = _store.Read(s => s.Cues.Select(c => c.Id).ToList());
        if (cues.Count == 0) return;
        var id = _selectedId != null && cues.Contains(_selectedId) ? _selectedId : cues[0];
        PlayNow(id, false);
        _selectedId = Neighbour(id, +1) ?? id;
    }

    private string? Neighbour(string? id, int step)
    {
        var cues = _store.Read(s => s.Cues.Select(c => c.Id).ToList());
        int i = id == null ? -1 : cues.IndexOf(id);
        int j = i + step;
        if (i < 0) j = step > 0 ? 0 : cues.Count - 1;
        return j >= 0 && j < cues.Count ? cues[j] : null;
    }

    /// <summary>Starts a cue on the deck that is not on air; the output changes over when its first picture is there.</summary>
    private void PlayNow(string id, bool fade)
    {
        var cue = FindCue(id);
        if (cue == null) return;
        if (!File.Exists(cue.Path)) { _message = "File not found: " + cue.Path; return; }

        // a transition still running is finished at once, so "the other deck" is free
        Deck? finishedOld = null;
        lock (_switchSync)
        {
            if (_incoming != null && _incoming.HasPicture) { finishedOld = _active; _active = _incoming; _incoming = null; _showBlack = false; }
            else _incoming = null;
        }
        if (finishedOld != null) finishedOld.Stop();

        var target = Other(Active());
        if (!(target.CueId == id && target.Mode == DeckMode.Prerolled))
            target.Load(_vlc, cue, _fmt, preroll: false);
        StartIncoming(target, fade);
        _message = "";
        Log.Write($"play {cue.Name} on {target.Name} ({(fade ? "fade" : "cut")}, end: {cue.EndAction})");
    }

    private void StartIncoming(Deck deck, bool fade)
    {
        if (deck.Mode == DeckMode.Prerolled) deck.Pause(false);
        float seconds = Fade.Normalize(_store.Read(s => s.FadeMs)) / 1000f;
        lock (_switchSync) { _incoming = deck; _incomingFade = fade; _transitionFade = seconds; _fadeReset = true; }
    }

    private void StopNow()
    {
        _cued = false; _cuedCueId = null;   // Stop / "Black" = black, until a cue is chosen again
        lock (_switchSync) { _incoming = null; _showBlack = true; }
        foreach (var d in _decks) d.Stop();
    }

    private void TogglePauseNow()
    {
        Deck a; Deck? inc;
        lock (_switchSync) { a = _active; inc = _incoming; }
        if (a.Mode == DeckMode.Playing || inc?.Mode == DeckMode.Playing)
        {
            a.Pause(true);
            inc?.Pause(true);
        }
        else if (a.Mode == DeckMode.Paused || inc?.Mode == DeckMode.Paused)
        {
            a.Pause(false);
            if (inc?.Mode == DeckMode.Paused) inc.Pause(false);
        }
    }

    private void OnPrerollPicture(Deck deck, int serial)
    {
        if (serial != deck.Serial || deck.Mode != DeckMode.Preroll) return;
        deck.Mp.SetPause(true);
        deck.SeekTo(0);   // a few pictures went by before the pause took effect: back to the very first one
        deck.MarkPrerolled();
        deck.Mode = DeckMode.Prerolled;   // after the jump, so the output thread never sees "prerolled" with the pre-jump picture as current
        Log.Write($"prerolled {FindCue(deck.CueId)?.Name} on {deck.Name}");
    }

    /// <summary>
    /// What follows the end of a cue: Cut next / Fade next go to the next cue, Loop / Loop fade to the cue itself (Peter: a fade is
    /// nicer than a jump for clips without a clean end). Loop is done here and not by libVLC (input-repeat was fixed at load time, so
    /// a change while playing never reached it). Null = nothing follows (Hold, Black, or no next cue).
    /// </summary>
    private (string? target, bool fade, bool loop) Follow(Cue cue) => cue.EndAction switch
    {
        EndAction.Next => (Neighbour(cue.Id, +1), false, false),
        EndAction.Fade => (Neighbour(cue.Id, +1), true, false),
        EndAction.Loop => (cue.Id, false, true),
        EndAction.LoopFade => (cue.Id, true, true),
        _ => (null, false, false),
    };

    /// <summary>
    /// Every 40 ms: near the end of the cue on air, preroll what follows it and start the fade in time; while stopped, keep the chosen
    /// cue prerolled. The end action is read here every time, so changing it while the cue plays always applies: a deck prerolled
    /// for another follow-up is dropped and the right one loaded.
    /// </summary>
    private void CheckAuto()
    {
        Deck a; Deck? inc; bool black;
        lock (_switchSync) { a = _active; inc = _incoming; black = _showBlack; }
        // the audio choice can change while a cue plays: follow it on the decks that are heard
        if (!black) SyncAudio(a);
        if (inc != null) SyncAudio(inc);
        // still pictures: the display time can change while the picture is on air
        foreach (var d in _decks)
            if (d.StillMs >= 0 && FindCue(d.CueId) is { } sc) d.StillMs = Still.Normalize(sc.StillSeconds) * 1000;
        // a cue that has a video track libVLC cannot decode (e.g. VMX1 from vMix) never delivers a picture: after 2 s it goes on air
        // with a black picture and its sound instead of waiting forever
        if (inc != null && !inc.HasPicture && !inc.AudioOnly && inc.Mode == DeckMode.Playing && Environment.TickCount64 - inc.LoadedAt > 2000)
        {
            inc.AudioOnly = true;
            var name = FindCue(inc.CueId)?.Name;
            _message = $"No picture from {name}: {_lastDecodeError ?? "video could not be decoded"} - sound only";
            Log.Write(_message);
        }
        if (black && inc == null) { PrerollChosen(a); return; }
        if (inc != null || a.Mode != DeckMode.Playing) return;
        var cue = FindCue(a.CueId);
        if (cue == null) return;
        // a still picture never ends by itself (libVLC shows it endlessly): its time is up = the end of the cue (all end actions)
        if (a.StillMs > 0 && a.SmoothTimeMs >= a.StillMs) { OnEnd(a, a.Serial); return; }
        var o = Other(a);
        var (target, fade, loop) = Follow(cue);
        bool oLoading = o.Mode is DeckMode.Preroll or DeckMode.Prerolled;
        if (target == null)
        {
            if (oLoading) { o.Stop(); Log.Write($"{cue.Name}: end action now {cue.EndAction}, prerolled deck {o.Name} dropped"); }
            return;
        }
        if (oLoading && o.CueId != target)
        {
            o.Stop();   // the end action changed: loaded for another follow-up
            Log.Write($"{cue.Name}: end action now {cue.EndAction}, prerolled deck {o.Name} dropped");
            return;
        }
        long len = a.LengthMs;
        if (len <= 0) return;
        long rem = len - a.SmoothTimeMs;
        long fadeMs = fade ? Fade.Normalize(_store.Read(s => s.FadeMs)) : 0;

        if (rem < fadeMs + PrerollLeadMs && o.Mode == DeckMode.Idle)
        {
            var next = FindCue(target);
            // audio-only files are not prerolled (the preroll waits for a picture); they start at the end instead
            if (next != null && File.Exists(next.Path) && next.HasVideo != false && next.Unsupported == null)
            {
                o.Load(_vlc, next, _fmt, preroll: true);
                Log.Write($"preroll {next.Name} on {o.Name} ({rem} ms before the end of {cue.Name}, {cue.EndAction})");
            }
        }
        if (fade && rem <= fadeMs && o.CueId == target && o.Mode == DeckMode.Prerolled)
        {
            StartIncoming(o, fade: true);
            if (!loop) _selectedId = Neighbour(target, +1) ?? target;
            Log.Write($"fade {cue.Name} -> {FindCue(target)?.Name} ({fadeMs} ms, {cue.EndAction}; length {len}, estimated {len - rem})");
        }
    }

    private string? _prerollFailedPath;

    /// <summary>
    /// Stopped (output black): the chosen cue is loaded on the deck that is not on air and paused at its first picture.
    /// The preview shows that picture (Peter: choosing a clip in stop mode must show its first frame); the outputs stay black.
    /// GO then starts it at once, because PlayNow takes a prerolled deck as it is.
    /// </summary>
    private void PrerollChosen(Deck active)
    {
        var ids = _store.Read(s => s.Cues.Select(c => c.Id).ToList());
        string? want = _selectedId != null && ids.Contains(_selectedId) ? _selectedId : ids.FirstOrDefault();
        var o = Other(active);
        if (want == null) { if (o.Mode != DeckMode.Idle) o.Stop(); return; }
        var cue = FindCue(want);
        // a chosen cue whose picture cannot be decoded: black instead of the previous cue's picture (Peter: the old frame stayed)
        if (cue?.Unsupported != null || (o.CueId == want && o.Mode == DeckMode.Preroll && Environment.TickCount64 - o.LoadedAt > 3000))
        {
            if (_cuedCueId != null) _cuedCueId = null;
            if (cue?.Unsupported != null && o.Mode != DeckMode.Idle) o.Stop();
            return;
        }
        if (o.CueId == want && o.Mode is DeckMode.Preroll or DeckMode.Prerolled) return;
        if (cue == null || !File.Exists(cue.Path) || cue.HasVideo == false)
        {
            if (o.Mode != DeckMode.Idle) o.Stop();
            if (cue != null && cue.HasVideo != false && _prerollFailedPath != cue.Path) { _prerollFailedPath = cue.Path; Log.Write("preview: file not found " + cue.Path); }
            return;
        }
        o.Load(_vlc, cue, _fmt, preroll: true);
    }

    /// <summary>Command thread: the deck follows the current audio choice of its cue (channels at once, a track via libVLC).</summary>
    private void SyncAudio(Deck d)
    {
        var c = FindCue(d.CueId);
        if (c == null) return;
        var sel = AudioChoice.Normalize(c.Audio, c);
        if (d.AudioSel != sel)
        {
            d.AudioSel = sel;
            d.WantTrack = AudioChoice.TrackIndex(sel);
            Log.Write($"audio {c.Name} on {d.Name}: {sel}");
        }
        d.ApplyAudioTrack();
    }

    private void OnEnd(Deck deck, int serial)
    {
        if (serial != deck.Serial) return;   // a newer command already changed this deck
        Deck a; Deck? inc;
        lock (_switchSync) { a = _active; inc = _incoming; }
        if (deck != a)
        {
            // the incoming deck ended before it went on air (very short file) - nothing to do, it keeps its last picture
            deck.Stop(keepPicture: true);
            return;
        }
        var cue = FindCue(deck.CueId);
        var action = cue?.EndAction ?? EndAction.Stop;
        Log.Write($"end of {cue?.Name} on {deck.Name} -> {action} (length {deck.LengthMs}, last estimate {deck.SmoothTimeMs})");
        if (action is EndAction.Next or EndAction.Fade or EndAction.Loop or EndAction.LoopFade)
        {
            if (inc != null) { deck.Stop(keepPicture: true); return; }   // fade already running: hold the last picture under it
            var (target, fade, loop) = Follow(cue!);
            if (target != null)
            {
                var o = Other(deck);
                if (o.CueId == target && o.Mode == DeckMode.Prerolled) StartIncoming(o, fade);
                else PlayNow(target, fade);
                if (!loop) _selectedId = Neighbour(target, +1) ?? target;
                deck.Stop(keepPicture: true);
                return;
            }
            action = EndAction.Hold;
        }
        if (action == EndAction.Stop) { StopNow(); return; }
        // hold: the last picture stays on the output
        deck.Stop(keepPicture: true);
    }

    // ================= outputs =================

    private void RebuildOutputs()
    {
        // stop the old output thread and senders
        _sendStop?.Cancel();
        _sendThread?.Join(2000);
        _ndi?.Dispose(); _ndi = null;
        _omt?.Dispose(); _omt = null;
        _sdi?.Dispose(); _sdi = null;
        StopNow();

        var settings = _store.Read(s => s.Settings);
        var fmt = OutputFormat.Find(settings.Format);
        SetGain(settings.GainDb);

        // the preview thread may still read the preview copy: wait for it before freeing
        for (int w = 0; w < 100 && _previewBusy; w++) Thread.Sleep(5);
        _previewBusy = true;
        int size = fmt.Width * fmt.Height * 2;
        foreach (var p in new[] { _black, _mix, _previewSrc, _cuedFrame }) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        _black = Marshal.AllocHGlobal(size); FillBlack(_black, size);
        _mix = Marshal.AllocHGlobal(size);
        _previewSrc = Marshal.AllocHGlobal(size);
        _cuedFrame = Marshal.AllocHGlobal(size); FillBlack(_cuedFrame, size);
        _cuedKey = -1; _cuedCueId = null;
        foreach (var d in _decks) d.Allocate(fmt);
        _fmt = fmt;
        _previewBusy = false;

        string name = string.IsNullOrWhiteSpace(settings.SourceName) ? ShowSettings.DefaultSourceName : settings.SourceName.Trim();
        _ndiStatus = "off";
        if (settings.Ndi)
        {
            if (!NdiNative.Load()) _ndiStatus = NdiNative.Status;
            else
            {
                try { _ndi = new NdiSender(name); _ndiStatus = "on"; }
                catch
                {
                    // NDIlib_send_create gives no reason; the usual one is a second sender with the same name on this PC
                    _ndiStatus = $"name '{name}' already in use on this PC";
                }
            }
        }
        _omtStatus = "off";
        if (settings.Omt)
        {
            try { _omt = new OmtSender(name); _omtStatus = "on"; }
            catch (Exception e) { _omtStatus = "failed: " + e.Message; }
        }
        // SDI: the ports are looked up again on every rebuild (an UltraStudio can be plugged in later)
        _sdiPorts = SdiOutput.Ports();
        _sdiStatus = "off";
        if (settings.Sdi)
        {
            if (_sdiPorts.Count == 0) _sdiStatus = "no device (Blackmagic Desktop Video)";
            else
            {
                try { _sdi = new SdiOutput(settings.SdiPort, fmt); _sdiStatus = "on"; }
                catch (Exception e) { _sdiStatus = e.Message; Log.Write("SDI failed: " + e); }
            }
        }
        Log.Write($"outputs {fmt.Id} name '{name}': NDI {_ndiStatus}, OMT {_omtStatus} {_omt?.Address}, SDI {_sdiStatus} {_sdi?.PortName}");
        ApplyMonitorNow(force: false);   // stays open across output changes; opened here at start

        _sendStop = new CancellationTokenSource();
        var token = _sendStop.Token;
        var ndi = _ndi; var omt = _omt; var sdi = _sdi;
        _sendThread = new Thread(() => SendLoop(fmt, ndi, omt, sdi, token)) { IsBackground = true, Name = "output", Priority = ThreadPriority.AboveNormal };
        _sendThread.Start();
    }

    private static unsafe void FillBlack(IntPtr p, int size)
    {
        uint* d = (uint*)p;
        for (int i = 0; i < size / 4; i++) d[i] = 0x10801080;   // U=0x80 Y=0x10 V=0x80 Y=0x10 (little endian)
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void SendLoop(OutputFormat fmt, NdiSender? ndi, OmtSender? omt, SdiOutput? sdi, CancellationToken ct)
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            double frame = fmt.FrameSeconds;
            long frameUs = (long)(frame * 1_000_000);
            int bytes = fmt.Width * fmt.Height * 2;
            long tick = 0, tickBase = 0, lastPreviewKey = long.MinValue;
            double fadeElapsed = 0, secondStart = 0;
            var planar = new float[MaxChannels * 4096];
            while (!ct.IsCancellationRequested)
            {
                double due = tick * frame;   // stream time of this frame (time stamps, sound sample positions)
                if (sdi != null)
                {
                    // SDI on: the card's clock sets the pace - wait until it has shown a frame and has room for the next one
                    if (!sdi.WaitFree(500))
                    {
                        if (ct.IsCancellationRequested) break;
                        Log.Write("SDI output stopped taking frames, continuing on the PC clock");
                        _sdiStatus = "stopped (device gone?)";
                        sdi = null;
                        tickBase = tick; sw.Restart();
                        continue;
                    }
                }
                else
                {
                    double localDue = (tick - tickBase) * frame;
                    double wait = localDue - sw.Elapsed.TotalSeconds;
                    if (wait > 0.002) { Thread.Sleep((int)((wait - 0.0015) * 1000)); continue; }
                    while (sw.Elapsed.TotalSeconds < localDue) Thread.SpinWait(20);

                    double late = sw.Elapsed.TotalSeconds - localDue;
                    if (late > 3 * frame)
                    {
                        long skip = (long)(late / frame);
                        tick += skip;
                        Interlocked.Add(ref _skipped, (int)skip);
                        continue;
                    }
                }

                // ---- which deck(s) and how much of each ----
                Deck a; Deck? inc; bool fade, black; float fadeSeconds;
                lock (_switchSync)
                {
                    if (_fadeReset) { fadeElapsed = 0; _fadeReset = false; }
                    a = _active; inc = _incoming; fade = _incomingFade; black = _showBlack; fadeSeconds = _transitionFade;
                }
                float alpha = -1;   // >= 0: share of the incoming deck in the mix
                if (inc != null && (inc.HasPicture || inc.AudioOnly) && inc.Mode is DeckMode.Playing or DeckMode.Paused)
                {
                    if (!fade) CompleteSwitch(a, inc, ref a, ref inc, ref black);
                    else
                    {
                        if (inc.Mode == DeckMode.Playing) fadeElapsed += frame;   // a pause freezes the fade
                        alpha = (float)Math.Min(1.0, fadeElapsed / Math.Max(0.02, fadeSeconds));
                        if (alpha >= 1f) { CompleteSwitch(a, inc, ref a, ref inc, ref black); alpha = -1; }
                    }
                }

                // ---- picture ----
                long serialA = 0;
                IntPtr pa = black ? IntPtr.Zero : a.Acquire(out serialA);
                IntPtr pic;
                long key;
                if (alpha >= 0 && inc != null)
                {
                    IntPtr pb = inc.Acquire(out _);
                    Blend(pa == IntPtr.Zero ? _black : pa, pb == IntPtr.Zero ? _black : pb, _mix, bytes, alpha);
                    pic = _mix;
                    key = tick | (1L << 62);   // every picture of a fade is new
                }
                else
                {
                    pic = pa == IntPtr.Zero ? _black : pa;
                    key = pa == IntPtr.Zero ? -1 : serialA * 2 + (a == _decks[1] ? 1 : 0);
                }
                // stopped and a cue was chosen: the outputs show the first picture of that cue (Peter: the director sees the clip is ready,
                // nobody starts on black). Copied from the prerolled deck only once its picture has settled (the preroll shows a few
                // pictures before the pause and the jump back to the start take effect - those must not go on air). Until a new one has
                // settled, the last cued picture stays, so choosing another cue never flashes black.
                if (Volatile.Read(ref _snapRequest) == 1)
                {
                    unsafe { Buffer.MemoryCopy((void*)pic, (void*)_cuedFrame, bytes, bytes); }
                    _cuedKey = -2;
                    _cuedCueId = FrozenId;
                    Interlocked.Exchange(ref _snapRequest, 0);
                    _snapDone.Set();
                }
                if (black && (_cued || inc != null))
                {
                    // a new picture is taken only while waiting (cued); once GO started a cue, the cued picture just stays until it runs
                    var o = Other(a);
                    // the deck's copy of the cue's very first picture (taken by the deck when libVLC showed it) - once per loaded cue
                    long cueKey = (long)o.Serial * 2 + (o == _decks[1] ? 1 : 0);
                    var cueId = o.CueId;
                    if (inc == null && o.Mode is DeckMode.Preroll or DeckMode.Prerolled && o.HasFirstPicture
                        && (cueKey != _cuedKey || cueId != _cuedCueId) && o.CopyFirstPicture(_cuedFrame, bytes))
                    {
                        _cuedKey = cueKey;
                        _cuedCueId = cueId;
                        Log.Write($"cued picture of {FindCue(cueId)?.Name} from deck {o.Name}");
                    }
                    if (_cuedCueId != null) { pic = _cuedFrame; key = _cuedKey | (1L << 61); }
                }
                IntPtr previewPic = pic;
                long ts = (long)(due * 10_000_000);
                try
                {
                    sdi?.SendVideo(pic);
                    omt?.SendVideo(pic, fmt, ts);
                    ndi?.SendVideo(pic, fmt);
                    // a new picture also goes to the preview, unless it is still busy with the last one
                    if (key != lastPreviewKey && !_previewBusy)
                    {
                        lastPreviewKey = key;
                        unsafe { Buffer.MemoryCopy((void*)previewPic, (void*)_previewSrc, bytes, bytes); }
                        _previewBusy = true;
                        _previewWake.Set();
                    }
                }
                finally
                {
                    a.Release();
                    inc?.Release();
                }

                // ---- sound for exactly this picture's interval ----
                long s0 = (long)Math.Round(tick * frame * SampleRate);
                long s1 = (long)Math.Round((tick + 1) * frame * SampleRate);
                int n = (int)Math.Min(s1 - s0, 4096);
                // stereo, unless the cue on air (or the one fading in) sends all its channels ("1-8")
                int outCh = 2;
                if (!black) outCh = Math.Max(outCh, a.OutputChannelsNeeded);
                if (alpha >= 0 && inc != null) outCh = Math.Max(outCh, inc.OutputChannelsNeeded);
                outCh = Math.Min(outCh, MaxChannels);
                Array.Clear(planar, 0, outCh * n);
                long now = VlcNow();
                float gain = _gain;
                if (!black) a.MixAudio(planar, n, outCh, now, frameUs, alpha >= 0 ? gain * (1 - alpha) : gain);
                if (alpha >= 0 && inc != null) inc.MixAudio(planar, n, outCh, now, frameUs, gain * alpha);
                sdi?.SendAudio(planar, outCh, n);
                _monitor?.Write(planar, outCh, n);
                omt?.SendAudio(planar, outCh, n, SampleRate, ts);
                ndi?.SendAudio(planar, outCh, n, SampleRate);
                _outChannels = outCh;

                tick++;
                Interlocked.Increment(ref _sentThisSecond);
                double t = sw.Elapsed.TotalSeconds;
                if (t - secondStart >= 1.0)
                {
                    _sentFps = Interlocked.Exchange(ref _sentThisSecond, 0);
                    var act = Active();
                    _decodedFps = act.TakeDecodedCount();
                    Other(act).TakeDecodedCount();
                    secondStart = t;
                }
            }
        }
        catch (Exception e)
        {
            Log.Write("output loop failed: " + e);
            _message = "Output failed: " + e.Message;
        }
        finally { timeEndPeriod(1); }
    }

    /// <summary>Output thread: the incoming deck goes on air; the old one is stopped on the command thread.</summary>
    private void CompleteSwitch(Deck oldDeck, Deck newDeck, ref Deck a, ref Deck? inc, ref bool black)
    {
        lock (_switchSync)
        {
            if (_incoming != newDeck) return;   // a command changed it meanwhile
            _active = newDeck; _incoming = null; _showBlack = false;
        }
        a = newDeck; inc = null; black = false;
        if (oldDeck != newDeck)
            _commands.Add(() => { lock (_switchSync) if (oldDeck == _active || oldDeck == _incoming) return; oldDeck.Stop(); });
    }

    /// <summary>dst = a * (1 - alpha) + b * alpha, byte by byte (fine for UYVY: Y, U and V are all linear around their offsets).</summary>
    private static unsafe void Blend(IntPtr a, IntPtr b, IntPtr dst, int bytes, float alpha)
    {
        ushort w = (ushort)Math.Clamp((int)(alpha * 256), 0, 256), iw = (ushort)(256 - w);
        byte* pa = (byte*)a, pb = (byte*)b, pd = (byte*)dst;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vw = new Vector<ushort>(w);
            var viw = new Vector<ushort>(iw);
            int vs = Vector<byte>.Count;
            for (; i <= bytes - vs; i += vs)
            {
                Vector.Widen(*(Vector<byte>*)(pa + i), out var al, out var ah);
                Vector.Widen(*(Vector<byte>*)(pb + i), out var bl, out var bh);
                var rl = Vector.ShiftRightLogical(al * viw + bl * vw, 8);
                var rh = Vector.ShiftRightLogical(ah * viw + bh * vw, 8);
                *(Vector<byte>*)(pd + i) = Vector.Narrow(rl, rh);
            }
        }
        for (; i < bytes; i++) pd[i] = (byte)((pa[i] * iw + pb[i] * w) >> 8);
    }

    // ================= preview =================

    private void PreviewLoop()
    {
        while (!_disposed)
        {
            _previewWake.WaitOne();
            if (_disposed) return;
            int half = 1 - _previewHalf;
            var dst = _previewRgba[half];
            try { ConvertPreview(_previewSrc, _fmt, dst); }
            finally { _previewBusy = false; }
            int serial;
            lock (_previewSync) { _previewHalf = half; serial = ++_previewSerial; }
            try { PreviewReady?.Invoke(dst, half, serial); } catch (Exception e) { Log.Write("preview handler failed: " + e.Message); }
        }
    }

    /// <summary>UYVY at output size -> RGBA 640x360 with an area filter (average over each f x f block, f = 2/3/6), BT.709 limited range.</summary>
    private static unsafe void ConvertPreview(IntPtr pic, OutputFormat fmt, byte[] dst)
    {
        int f = Math.Max(1, fmt.Width / PreviewWidth);
        int step = f >= 4 ? 2 : 1;                      // 2160p: every other pixel of the 6x6 block is enough
        int stride = fmt.Width * 2;
        byte* src = (byte*)pic;
        fixed (byte* d = dst)
        {
            for (int py = 0; py < PreviewHeight; py++)
            {
                byte* o = d + py * PreviewWidth * 4;
                for (int px = 0; px < PreviewWidth; px++, o += 4)
                {
                    int sy = 0, n = 0, su = 0, sv = 0, nc = 0;
                    int x0 = px * f, xc = x0 + f / 2;
                    for (int yy = 0; yy < f; yy += step)
                    {
                        byte* row = src + (long)(py * f + yy) * stride;
                        for (int xx = 0; xx < f; xx += step)
                        {
                            int x = x0 + xx;
                            sy += row[(x >> 1) * 4 + ((x & 1) == 0 ? 1 : 3)];
                            n++;
                        }
                        byte* qc = row + (xc >> 1) * 4;
                        su += qc[0]; sv += qc[2]; nc++;
                    }
                    float c = 1.164f * (sy / (float)n - 16), u = su / (float)nc - 128, v = sv / (float)nc - 128;
                    o[0] = Clamp(c + 1.793f * v);
                    o[1] = Clamp(c - 0.213f * u - 0.533f * v);
                    o[2] = Clamp(c + 2.112f * u);
                    o[3] = 255;
                }
            }
        }
    }

    private static byte Clamp(float v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    /// <summary>The newest preview picture (RGBA, PreviewWidth x PreviewHeight) and its serial number.</summary>
    public (byte[] rgba, int serial, int width, int height) Preview() { lock (_previewSync) return (_previewRgba[_previewHalf], _previewSerial, PreviewWidth, PreviewHeight); }

    // ================= state for the page =================

    public object Snapshot()
    {
        var (cues, settings, listPath, modified, fadeMs) = _store.Read(s => (s.Cues.ToList(), s.Settings, s.PlaylistPath, s.Modified, Fade.Normalize(s.FadeMs)));
        Deck a; Deck? inc; bool black, fade;
        lock (_switchSync) { a = _active; inc = _incoming; black = _showBlack; fade = _incomingFade; }

        string state;
        if (black && inc == null) state = "stopped";
        else if (black) state = "starting";
        else state = a.Mode switch
        {
            DeckMode.Playing => "playing",
            DeckMode.Paused => "paused",
            DeckMode.Ended => "ended",
            _ => inc != null ? "starting" : "stopped",
        };
        string? currentId = black ? null : a.CueId;
        long pos = 0, len = 0;
        // smooth position (libVLC itself updates only every ~250 ms): the countdown of the last 10 s must tick evenly
        if (a.Mode is DeckMode.Playing or DeckMode.Paused && !black) { len = a.LengthMs; pos = Math.Min(a.SmoothTimeMs, len > 0 ? len : long.MaxValue); }
        var cur = cues.FirstOrDefault(c => c.Id == currentId);
        if (len == 0 && cur != null) len = cur.DurationMs;
        var other = Other(a);
        return new
        {
            type = "state",
            state,
            currentId,
            selectedId = _selectedId,
            incomingId = inc?.CueId,
            fading = inc != null && fade && inc.HasPicture,
            prerolledId = inc == null && other.Mode == DeckMode.Prerolled ? other.CueId : null,
            positionMs = pos,
            jump = $"{a.Name}:{a.Jumps}",   // changes on every new cue and every seek
            cuedId = black && (_cued || inc != null) && _cuedCueId != FrozenId ? _cuedCueId : null,   // stopped: this cue's first picture is on the outputs
            lengthMs = len,
            message = _message,
            fps = _sentFps,
            decodedFps = _decodedFps,
            skipped = _skipped,
            format = _fmt.Id,
            audioChannels = _outChannels,
            // sample peaks of the file on air, per channel in file order (0..1), since the last state message: the level meters
            meter = black ? Array.Empty<float>() : a.TakePeaks(),
            ndi = new { status = _ndiStatus, connections = SafeConnections(() => _ndi?.Connections ?? 0) },
            omt = new { status = _omtStatus, address = _omt?.Address ?? "", connections = SafeConnections(() => _omt?.Connections ?? 0) },
            monitor = new { status = _monitorStatus, devices = _monitorDevices.Select(d => new { d.id, d.name }) },
            sdi = new { status = _sdiStatus, port = _sdi?.PortName ?? "", ports = _sdiPorts, late = _sdi?.Late ?? 0, dropped = _sdi?.Dropped ?? 0 },
            settings,
            cues,
            // what each file offers for the audio dropdown (the rules live in AudioChoice only)
            audioOptions = cues.ToDictionary(c => c.Id, c => AudioChoice.Options(c).Select(o => new { value = o.value, label = o.label })),
            playlist = new { name = MainForm.PlaylistName(listPath), path = listPath ?? "", modified, fadeMs },
            formats = OutputFormat.All.Select(f => f.Id),
            version = typeof(PlayerEngine).Assembly.GetName().Version?.ToString() ?? "",
        };
    }

    private static int SafeConnections(Func<int> f) { try { return f(); } catch { return 0; } }

    /// <summary>Test helper: a signature of the picture the outputs show while stopped (luma samples of the cued picture).</summary>
    public unsafe long CuedSignature()
    {
        if (_cuedFrame == IntPtr.Zero || _cuedCueId == null) return 0;
        byte* p = (byte*)_cuedFrame;
        int w = _fmt.Width, h = _fmt.Height;
        long sig = 17;
        for (int y = h / 32; y < h; y += h / 16) for (int x = w / 32; x < w; x += w / 16) sig = sig * 31 + p[(y * w + x) * 2 + 1] / 8;
        return sig & 0xFFFFFF;
    }

    /// <summary>Position of the cue on air the way the window gets it (test helper for the countdown measurement).</summary>
    /// <summary>Test helper: FFmpeg sound diagnostics of the deck on air.</summary>
    public string AudioStats() => Active().FfmpegAudioStats;

    public (bool playing, long pos, long len) PositionInfo()
    {
        Deck a; bool black;
        lock (_switchSync) { a = _active; black = _showBlack; }
        if (black || a.Mode is not (DeckMode.Playing or DeckMode.Paused)) return (false, 0, 0);
        long len = a.LengthMs;
        return (a.Mode == DeckMode.Playing, Math.Min(a.SmoothTimeMs, len > 0 ? len : long.MaxValue), len);
    }

    /// <summary>Reads the length of a file (for the cue list), without playing it.</summary>
    public async Task<long> ProbeDurationAsync(string path)
    {
        using var m = new Media(_vlc, path, FromType.FromPath);
        await m.Parse(MediaParseOptions.ParseLocal, 5000);
        // the await resumes on libVLC's event thread (inside the "parsed" event); disposing the media there crashes libvlc.dll
        // (access violation, reproduced with --duration). Leave that thread first.
        await Task.Yield();
        return Math.Max(0, m.Duration);
    }

    /// <summary>Length, channels of each audio track and whether there is video (for the cue list and the audio choices).</summary>
    public async Task<(long durationMs, List<int> audioTracks, bool hasVideo, bool rates48)> ProbeInfoAsync(string path)
    {
        using var m = new Media(_vlc, path, FromType.FromPath);
        await m.Parse(MediaParseOptions.ParseLocal, 5000);
        await Task.Yield();   // see ProbeDurationAsync
        var tracks = m.Tracks;
        var audioTracks = tracks.Where(t => t.TrackType == TrackType.Audio).ToList();
        var audio = audioTracks.Select(t => (int)Math.Max(1, t.Data.Audio.Channels)).ToList();
        bool video = tracks.Any(t => t.TrackType == TrackType.Video);
        bool rates48 = audioTracks.All(t => t.Data.Audio.Rate == 48000);
        return (Math.Max(0, m.Duration), audio, video, rates48);
    }

    /// <summary>Track list of a file (test helper for --duration).</summary>
    public async Task<string> ProbeTracksAsync(string path)
    {
        using var m = new Media(_vlc, path, FromType.FromPath);
        await m.Parse(MediaParseOptions.ParseLocal, 5000);
        await Task.Yield();
        return string.Join(", ", m.Tracks.Select(t => t.TrackType == TrackType.Audio
            ? $"audio {t.Codec:X} {t.Data.Audio.Rate} Hz {t.Data.Audio.Channels} ch"
            : t.TrackType == TrackType.Video ? $"video {t.Data.Video.Width}x{t.Data.Video.Height} {t.Data.Video.FrameRateNum}/{t.Data.Video.FrameRateDen}" : t.TrackType.ToString()));
    }

    public void Dispose()
    {
        _disposed = true;
        _autoTimer.Dispose();
        _previewWake.Set();
        _previewThread.Join(1000);
        _sendStop?.Cancel();
        _sendThread?.Join(2000);
        // the DeckLink device was opened on the command thread (MTA); COM refuses it from the window thread (STA): free it there
        try { _commands.Add(() => { _sdi?.Dispose(); _sdi = null; var m = _monitor; _monitor = null; m?.Dispose(); }); } catch (InvalidOperationException) { }
        _commands.CompleteAdding();
        _commandThread.Join(2000);
        foreach (var d in _decks) d.Dispose();
        _checker.Dispose();
        _ndi?.Dispose();
        _omt?.Dispose();
        _vlc.Dispose();
    }
}
