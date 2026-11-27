using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace CuePlayer;

internal enum DeckMode { Idle, Preroll, Prerolled, Playing, Paused, Ended }

/// <summary>
/// One libVLC player with its own picture buffers (UYVY at output size) and sound queue (S16 stereo 48 kHz, kept as float with the libVLC pts).
/// The engine has two decks (A/B): the one on air and the one that prerolls the next cue, so cuts are gapless and fades can mix both.
/// Mode/CueId are changed only on the engine's command thread; the output thread reads frames and sound through the locked methods.
/// </summary>
internal sealed class Deck : IDisposable
{
    public const int SampleRate = 48000, MaxChannels = 8;
    private const int BufferCount = 6;

    // ---- audio channels ----
    // libVLC is asked (format callback) to deliver the file's own channel count (up to 8) instead of a stereo downmix, so single
    // channels/pairs can be chosen per cue (Peter). Files with several audio TRACKS play one track at a time (libVLC limit): the choice
    // then selects the track ("t1".."t8").
    public volatile int SrcChannels = 2;          // channels libVLC delivers for the current file
    public volatile string AudioSel = AudioChoice.Default;
    public volatile bool AudioOnly;               // no video track (or one libVLC cannot decode): goes on air without waiting for a picture
    public long LoadedAt;                         // when the current cue was loaded (Environment.TickCount64)
    private volatile FfmpegAudio? _sidecar;       // the sound of a multi-track file, read by FFmpeg (null = libVLC's sound)
    public bool UsesFfmpegAudio => _sidecar != null;
    /// <summary>Test helper: resyncs / underruns / seeks of the FFmpeg sound so far, and how far off it was at the last resync.</summary>
    public string FfmpegAudioStats => _sidecar is { } s ? $"resync {s.Resyncs} (last {s.LastResyncOff} ms) underrun {s.Underruns} seek {s.Seeks}" : "-";
    private volatile VmxVideo? _vmx;              // the picture of a VMX file, decoded by libvmx (libVLC plays it with ":no-video")
    private VmxVideo? _vmxPinned;                 // output thread: the VMX reader whose picture Acquire handed out

    private void DropVmx()
    {
        var v = _vmx;
        _vmx = null;
        v?.Dispose();   // waits for a picture the output thread is still sending
    }

    /// <summary>
    /// VMX preroll: "first picture" needs frame 0 decoded AND libVLC running (a play position came), otherwise the pause the engine
    /// sends next could reach libVLC before it plays and be lost. Called from the reader (frame 0) and from libVLC's time event.
    /// </summary>
    private void TryVmxPreroll()
    {
        var v = _vmx;
        if (v == null || Mode != DeckMode.Preroll) return;
        bool started; lock (_posSync) started = _evTime >= 0;
        if (!started) return;
        int serial;
        lock (_sync)
        {
            if (_firstSerial == Serial || _first == IntPtr.Zero || !v.CopyFrame0(_first, _bytes)) return;
            _firstSerial = Serial;
            serial = Serial;
        }
        PrerollPicture?.Invoke(this, serial);
    }
    public int WantTrack = -1, AppliedTrack = -1; // audio track index for "tN" choices (command thread)

    public readonly string Name;
    public readonly MediaPlayer Mp;
    private Media? _media;

    // ---- pictures (guarded by _sync) ----
    private readonly object _sync = new();
    private IntPtr[] _buf = [];
    private readonly bool[] _locked = new bool[BufferCount];
    private readonly long[] _lockSerial = new long[BufferCount];
    private readonly int[] _bufLoad = new int[BufferCount];   // Serial (load) at the time libVLC took the buffer
    // The first picture libVLC shows after a preroll Load is the cue's picture 0. It is copied aside here, because the paused picture
    // that stays on the deck is not reliably picture 0: a few pictures go by before the pause takes effect, and the jump back to 0
    // while paused shows sometimes picture 0, sometimes a later one (flip test 2026-11-26: up to 22 of 80 wrong). The outputs show this copy.
    private IntPtr _first;
    private int _firstSerial = -1, _bytes;

    /// <summary>The first picture of the cue loaded with preroll is there (see <see cref="CopyFirstPicture"/>).</summary>
    public bool HasFirstPicture => Volatile.Read(ref _firstSerial) == Serial;

    /// <summary>Copies the cue's first picture into <paramref name="dst"/>. False = not there (yet).</summary>
    public unsafe bool CopyFirstPicture(IntPtr dst, int bytes)
    {
        lock (_sync)
        {
            if (_firstSerial != Serial || _first == IntPtr.Zero || bytes != _bytes) return false;
            Buffer.MemoryCopy((void*)_first, (void*)dst, bytes, bytes);
            return true;
        }
    }
    private long _serial;
    private int _latest = -1, _sending = -1;
    private long _displaySerial;
    private int _decodedThisSecond;

    // ---- sound (guarded by _audioSync) ----
    private sealed class AudioChunk { public long Pts; public float[] Data = []; public int Frames; public int Offset; public int Channels; }
    private readonly object _audioSync = new();
    private readonly Queue<AudioChunk> _audio = new();
    private bool _audioPaused;
    private long _pausePts;

    // ---- state (written on the command thread) ----
    public volatile DeckMode Mode = DeckMode.Idle;
    public volatile string? CueId;
    public int Serial;                       // +1 for every load; events carry it so late events of an old cue are ignored
    public int Jumps;                        // +1 for every load and every seek: the page restarts its countdown clock when it changes

    /// <summary>libVLC reached the end (raised on a libVLC thread: only queue work, never call the player here).</summary>
    public event Action<Deck, int>? Ended;
    /// <summary>The first picture of a preroll arrived (libVLC thread, same rule).</summary>
    public event Action<Deck, int>? PrerollPicture;
    public event Action<Deck, int>? Failed;

    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;
    private readonly MediaPlayer.LibVLCAudioPlayCb _playCb;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCb;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCb;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCb;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCb;
    private readonly MediaPlayer.LibVLCAudioSetupCb _setupCb;
    private readonly MediaPlayer.LibVLCAudioCleanupCb _cleanupCb;

    public Deck(LibVLC vlc, string name)
    {
        Name = name;
        Mp = new MediaPlayer(vlc);
        _lockCb = OnLock; _unlockCb = (_, _, _) => { }; _displayCb = OnDisplay;
        _playCb = OnAudioPlay; _pauseCb = OnAudioPause; _resumeCb = OnAudioResume; _flushCb = (_, _) => { lock (_audioSync) _audio.Clear(); }; _drainCb = _ => { };
        _setupCb = OnAudioSetup; _cleanupCb = _ => { };
        Mp.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
        Mp.SetAudioFormatCallback(_setupCb, _cleanupCb);
        Mp.SetAudioCallbacks(_playCb, _pauseCb, _resumeCb, _flushCb, _drainCb);
        Mp.EndReached += (_, _) => Ended?.Invoke(this, Serial);
        Mp.EncounteredError += (_, _) => Failed?.Invoke(this, Serial);
        // still pictures: libVLC's clock keeps running while paused and ignores the jump back to 0 (1384 ms at GO after a
        // 0.9 s preroll wait), so their position runs on the deck's own clock only (anchored at load / seek / play / pause)
        Mp.TimeChanged += (_, e) => { if (StillMs < 0) OnTimeChanged(e.Time); };
    }

    /// <summary>New buffers for an output format. Only while the deck is stopped.</summary>
    public void Allocate(OutputFormat fmt)
    {
        lock (_sync)
        {
            foreach (var b in _buf) Marshal.FreeHGlobal(b);
            int size = fmt.Width * fmt.Height * 2;
            _buf = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++) { _buf[i] = Marshal.AllocHGlobal(size); _locked[i] = false; }
            _latest = -1; _sending = -1;
            if (_first != IntPtr.Zero) Marshal.FreeHGlobal(_first);
            _first = Marshal.AllocHGlobal(size);
            _bytes = size;
            _firstSerial = -1;
        }
        Mp.SetVideoFormat("UYVY", (uint)fmt.Width, (uint)fmt.Height, (uint)(fmt.Width * 2));
    }

    /// <summary>Starts a cue. With <paramref name="preroll"/> the engine pauses it at its first picture (see PrerollPicture).</summary>
    public void Load(LibVLC vlc, Cue cue, OutputFormat fmt, bool preroll)
    {
        // pictures: played from a copy at exactly the output size with bars (see Still.Prepared); videos: canvas filter
        string playPath = cue.IsStill ? Still.Prepared(cue.Path, fmt.Width, fmt.Height) ?? cue.Path : cue.Path;
        var media = new Media(vlc, playPath, FromType.FromPath);
        if (!cue.IsStill)
        {
            // letterbox/pillarbox into the output size instead of stretching
            media.AddOption(":video-filter=canvas");
            media.AddOption($":canvas-width={fmt.Width}");
            media.AddOption($":canvas-height={fmt.Height}");
            media.AddOption($":canvas-aspect={fmt.Width}:{fmt.Height}");
            media.AddOption(":canvas-padd");
        }
        // interlaced output: keep the fields woven; progressive output: deinterlace interlaced files (field rate when the output is >= 50)
        if (fmt.Interlaced) media.AddOption(":deinterlace=0");
        else
        {
            media.AddOption(":deinterlace=-1");
            media.AddOption(fmt.RateN / (double)fmt.RateD >= 49 ? ":deinterlace-mode=yadif2x" : ":deinterlace-mode=yadif");
        }
        // no ":input-repeat" for Loop: it is fixed at load time (a later change of the end action never reached libVLC, and libVLC
        // then never reported the end). The engine loops itself by prerolling the same cue on the other deck.

        // audio: the cue's choice. A file with several audio tracks is read by FFmpeg (all tracks at once, libVLC's sound off);
        // without FFmpeg (or other than 48 kHz) libVLC plays the chosen track only.
        _sidecar?.Dispose();
        _sidecar = null;
        AudioSel = AudioChoice.Normalize(cue.Audio, cue);
        AudioOnly = cue.HasVideo == false;
        LoadedAt = Environment.TickCount64;
        if (cue.UsesFfmpegAudio)
        {
            // libVLC's sound off - except for VMX files: there libVLC has no picture either (":no-video"), and with neither it never
            // starts its clock (stays "starting", no length). Its sound then only drives the clock; what is heard comes from FFmpeg.
            if (cue.Vmx != true) media.AddOption(":no-audio");
            _sidecar = new FfmpegAudio(cue.Path, cue.AudioTracks!.Sum());
            SrcChannels = _sidecar.Channels;
            WantTrack = AppliedTrack = -1;
        }
        else
        {
            WantTrack = AudioChoice.TrackIndex(AudioSel);
            AppliedTrack = WantTrack;
            if (WantTrack >= 0) media.AddOption($":audio-track={WantTrack}");
        }

        // the deck still has a media open (prerolled, paused, playing): stop it first. libVLC's Stop returns when its threads are done,
        // so no picture of the old media can arrive after the new Serial is set
        if (Mode is DeckMode.Preroll or DeckMode.Prerolled or DeckMode.Playing or DeckMode.Paused) Mp.Stop();
        DropVmx();
        lock (_audioSync) { _audio.Clear(); _audioPaused = false; }
        lock (_sync) _latest = -1;
        ResetPosition();
        Interlocked.Increment(ref Jumps);
        Interlocked.Increment(ref Serial);
        CueId = cue.Id;
        Mode = preroll ? DeckMode.Preroll : DeckMode.Playing;
        // still picture: libVLC shows it endlessly (its clock runs); the engine ends it after the chosen time (StillMs)
        if (cue.IsStill) media.AddOption(":image-duration=-1");
        StillMs = cue.IsStill ? cue.StillSeconds * 1000 : -1;
        if (cue.IsStill) lock (_posSync) { _evTime = 0; _evAt = _posClock.ElapsedMilliseconds; }   // own clock starts now
        if (cue.Vmx == true)
        {
            // VMX: libVLC gives sound, clock, pause, jumps and the end; the picture comes from libvmx at the play position
            media.AddOption(":no-video");
            var v = new VmxVideo(cue.Path, fmt);
            v.FirstPicture += TryVmxPreroll;
            _vmx = v;
        }
        Mp.Play(media);
        var old = _media;
        _media = media;
        old?.Dispose();
    }

    /// <summary>Stops libVLC. <paramref name="keepPicture"/>: the last picture stays available (hold at the end).</summary>
    public void Stop(bool keepPicture = false)
    {
        Interlocked.Increment(ref Serial);
        Mp.Stop();
        lock (_audioSync) _audio.Clear();
        _sidecar?.Dispose();
        _sidecar = null;
        // VMX and Hold: the last picture is copied into the deck's own buffer, so it stays on air after the reader is gone
        if (keepPicture && _vmx is { } v)
        {
            lock (_sync)
            {
                int k = _sending == 0 ? 1 : 0;
                if (_buf.Length > k && v.CopyAt(SmoothTimeMs, _buf[k], _bytes)) { _latest = k; _bufLoad[k] = Serial; }
            }
        }
        DropVmx();
        if (!keepPicture) { lock (_sync) _latest = -1; CueId = null; Mode = DeckMode.Idle; ResetPosition(); }
        else Mode = DeckMode.Ended;
    }

    /// <summary>Command thread: switches to the wanted audio track while playing (choice "tN" changed on a multi-track file).</summary>
    public void ApplyAudioTrack()
    {
        if (WantTrack < 0 || WantTrack == AppliedTrack || Mode is DeckMode.Idle or DeckMode.Ended) return;
        var ids = (Mp.AudioTrackDescription ?? []).Where(d => d.Id >= 0).Select(d => d.Id).ToList();   // -1 = "Disable"
        if (WantTrack < ids.Count && Mp.SetAudioTrack(ids[WantTrack])) AppliedTrack = WantTrack;
    }

    public void Pause(bool on)
    {
        if (on && Mode == DeckMode.Playing) { Reanchor(); Mp.SetPause(true); Mode = DeckMode.Paused; }
        else if (!on && Mode is DeckMode.Paused or DeckMode.Prerolled) { Reanchor(); Mp.SetPause(false); Mode = DeckMode.Playing; }
    }

    /// <summary>
    /// Before play/pause changes: the clock's anchor moves to now (playing: the time run on so far is added). Without it, GO on a
    /// prerolled deck ran the clock on from the moment of the preroll - the position jumped ahead by the waiting time (0.75 s seen),
    /// and the FFmpeg sound jumped with it.
    /// </summary>
    private void Reanchor()
    {
        lock (_posSync)
        {
            if (_evTime < 0) return;
            long now = _posClock.ElapsedMilliseconds;
            if (Mode == DeckMode.Playing) _evTime += Math.Min(RunOnLimit, now - _evAt);
            _evAt = now;
        }
    }

    public long TimeMs => Math.Max(0, Mp.Time);

    /// <summary>Jump inside the cue; the smooth position starts again from the new time.</summary>
    public void SeekTo(long ms)
    {
        long target = Math.Max(0, ms);
        Interlocked.Increment(ref Jumps);
        ResetPosition();
        lock (_posSync) { _guardMin = target - 500; _guardUntil = _posClock.ElapsedMilliseconds + 1500; }
        Mp.Time = target;
        OnTimeChanged(target);
    }

    // ---- smooth position ----
    // libVLC 3 updates Time only about every 250 ms. Polling it gives a value that is already up to 250 ms old, so a clock anchored on a
    // polled value jumps forward at every update (uneven seconds in the countdown - Peter). The TimeChanged event comes at the moment of the
    // update with the exact new time: the clock is anchored there and runs at play speed in between. While playing it never goes backwards
    // by less than 400 ms (small corrections are absorbed); a real jump (seek, new cue) resets it.
    private readonly object _posSync = new();
    private readonly System.Diagnostics.Stopwatch _posClock = System.Diagnostics.Stopwatch.StartNew();
    private long _evTime = -1, _evAt, _lastPos;
    private long _guardMin, _guardUntil;   // after a seek: late events from before the jump are ignored for a moment

    private void OnTimeChanged(long time)
    {
        lock (_posSync)
        {
            long now = _posClock.ElapsedMilliseconds;
            if (now < _guardUntil && time < _guardMin) return;
            if (TraceTime) Log.Write($"time event {Name}: {time} ms at {now} (expected {(_evTime < 0 ? -1 : _evTime + (Mode == DeckMode.Playing ? now - _evAt : 0))})");
            _evTime = time; _evAt = now;
        }
        if (_vmx != null && Mode == DeckMode.Preroll) TryVmxPreroll();
    }

    // how far the clock runs on without a libVLC time event; still pictures get none (own clock), so no limit for them
    private long RunOnLimit => StillMs >= 0 ? long.MaxValue / 4 : 1500;

    public static readonly bool TraceTime = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_TIMEEV") == "1";

    private void ResetPosition() { lock (_posSync) { _evTime = -1; _lastPos = 0; } }

    /// <summary>Even, monotonic play position (ms) for countdown, fade timing and display. Safe from any thread.</summary>
    public long SmoothTimeMs
    {
        get
        {
            lock (_posSync)
            {
                if (_evTime < 0) return 0;
                long p = _evTime;
                // runs on from the last event; the limit only catches a real stall. It was 400 ms: libVLC sometimes skips one of its
                // ~250 ms events (MOV from the NAS: 1652 -> 2151 ms), the clock then stood still for 100 ms and jumped - the FFmpeg sound
                // followed every jump (resync ±60-90 ms, 2-3 per second = chirping and dropouts, Peter 2026-11-26)
                if (Mode == DeckMode.Playing) p += Math.Min(RunOnLimit, _posClock.ElapsedMilliseconds - _evAt);
                if (p < _lastPos && _lastPos - p < 400) p = _lastPos;   // never step back by a small amount
                _lastPos = p;
                return p;
            }
        }
    }
    /// <summary>Still picture: its display time in ms (0 = until the operator goes on); -1 = not a still. Updated live by the engine.</summary>
    public volatile int StillMs = -1;

    /// <summary>Length of the cue; a still picture counts as long as its display time (infinite = 0: no end, no countdown).</summary>
    public long LengthMs => StillMs >= 0 ? StillMs : Math.Max(0, Mp.Length);

    // ================= output side =================

    public bool HasPicture { get { var v = _vmx; if (v != null) return v.HasPicture; lock (_sync) return _latest >= 0; } }

    // when the deck was paused on its first picture. libVLC keeps redrawing a paused picture (every 16-60 ms, seen in the log),
    // so "no new picture for a while" never happens; the picture counts as settled a moment after the pause + jump back to 0.
    private long _prerolledAt = long.MaxValue;
    public void MarkPrerolled() => Volatile.Write(ref _prerolledAt, Environment.TickCount64);
    public long MsSincePrerolled => Mode == DeckMode.Prerolled ? Math.Max(0, Environment.TickCount64 - Volatile.Read(ref _prerolledAt)) : 0;

    /// <summary>The newest picture, protected from libVLC until <see cref="Release"/>. Zero when there is none.</summary>
    public IntPtr Acquire(out long serial)
    {
        var v = _vmx;
        if (v != null)
        {
            _vmxPinned = v;
            int before = v.NewPictures;
            IntPtr p = v.Acquire(SmoothTimeMs, out serial);
            if (v.NewPictures != before) Interlocked.Increment(ref _decodedThisSecond);
            return p;
        }
        lock (_sync)
        {
            serial = _displaySerial;
            if (_latest < 0) { _sending = -1; return IntPtr.Zero; }
            _sending = _latest;
            return _buf[_latest];
        }
    }

    public void Release()
    {
        var v = _vmxPinned;
        if (v != null) { _vmxPinned = null; v.Release(); return; }
        lock (_sync) _sending = -1;
    }

    public int TakeDecodedCount() => Interlocked.Exchange(ref _decodedThisSecond, 0);

    private readonly float[] _peaks = new float[MaxChannels];   // guarded by _audioSync, libVLC buffer order

    /// <summary>Highest sample of each file channel since the last call (file order, for the level meters), then reset.</summary>
    public float[] TakePeaks()
    {
        lock (_audioSync)
        {
            int n = Math.Min(SrcChannels, MaxChannels);
            var r = new float[n];
            bool fileOrder = _sidecar != null;   // FFmpeg sound is already in file order
            for (int f = 0; f < n; f++) r[f] = _peaks[fileOrder ? f : AudioChoice.VlcIndex(f, n)];
            Array.Clear(_peaks);
            return r;
        }
    }

    /// <summary>How many output channels this deck needs with its current choice: all its channels for "all", else 2.</summary>
    public int OutputChannelsNeeded => AudioSel == AudioChoice.All ? Math.Max(2, SrcChannels) : 2;

    /// <summary>
    /// Adds the <paramref name="n"/> samples per channel that are due now into <paramref name="planar"/> (outCh planes of n floats),
    /// routed by the cue's audio choice. Muted sound is still taken from the queue, so it stays in time.
    /// </summary>
    public void MixAudio(float[] planar, int n, int outCh, long nowUs, long frameUs, float gain)
    {
        var side = _sidecar;
        if (side != null)
        {
            // FFmpeg sound: taken at the picture's play position (the deck's smooth time), already in file order
            var fileRoutes = AudioChoice.Routes(AudioSel, SrcChannels, remap: false);
            // only once libVLC has reported a position: before that SmoothTimeMs stays 0 while the sound would already run, and the first
            // event then pulled it back (the start was heard twice, resync of ~0.7 s on the MOV)
            bool known; lock (_posSync) known = _evTime >= 0;
            lock (_audioSync) side.Mix(planar, n, outCh, fileRoutes, gain, SmoothTimeMs, Mode == DeckMode.Playing && known, _peaks);
            return;
        }
        var routes = AudioChoice.Routes(AudioSel, SrcChannels);
        lock (_audioSync)
        {
            if (_audioPaused) return;
            int filled = 0;
            while (filled < n && _audio.Count > 0)
            {
                var c = _audio.Peek();
                long cursor = c.Pts + c.Offset * 1_000_000L / SampleRate;
                if (cursor > nowUs + frameUs) break;                         // not due yet
                if (cursor < nowUs - 200_000) { _audio.Dequeue(); continue; } // far too late: drop
                int take = Math.Min(n - filled, c.Frames - c.Offset);
                // sample peaks of every channel of the file for the level meters (also when muted or not routed)
                for (int j = 0; j < c.Channels && j < MaxChannels; j++)
                {
                    float pk = _peaks[j];
                    for (int i = 0; i < take; i++) { float v = Math.Abs(c.Data[(c.Offset + i) * c.Channels + j]); if (v > pk) pk = v; }
                    _peaks[j] = pk;
                }
                foreach (var (src, dst) in routes)
                {
                    if (src >= c.Channels || dst >= outCh) continue;
                    int o = dst * n + filled;
                    for (int i = 0; i < take; i++) planar[o + i] += c.Data[(c.Offset + i) * c.Channels + src] * gain;
                }
                c.Offset += take;
                filled += take;
                if (c.Offset >= c.Frames) _audio.Dequeue();
            }
        }
    }

    // ================= libVLC callbacks =================

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        int i;
        lock (_sync)
        {
            i = -1;
            for (int k = 0; k < BufferCount; k++)
                if (k != _latest && k != _sending && !_locked[k]) { i = k; break; }
            if (i < 0)
            {
                // libVLC dropped pictures without showing them: take back the oldest locked one
                long best = long.MaxValue;
                for (int k = 0; k < BufferCount; k++)
                    if (k != _latest && k != _sending && _lockSerial[k] < best) { best = _lockSerial[k]; i = k; }
            }
            _locked[i] = true;
            _lockSerial[i] = ++_serial;
            _bufLoad[i] = Serial;
            Marshal.WriteIntPtr(planes, _buf[i]);
        }
        return i + 1;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        int i = (int)picture - 1;
        lock (_sync)
        {
            if (i < 0 || i >= BufferCount) return;
            _locked[i] = false;
            // a picture of the media before the last Load/Stop (libVLC redraws a paused picture every 16-60 ms): not this cue's.
            // Taking it made a new cue "prerolled" with the old cue's picture - the outputs then kept showing the previous clip
            // although the new one was marked CUED (Peter 2026-11-26; flip test: 2 of 30 wrong before this check)
            if (_bufLoad[i] != Serial) return;
            _latest = i;
            if (Mode == DeckMode.Preroll && _firstSerial != Serial && _first != IntPtr.Zero)
            {
                unsafe { Buffer.MemoryCopy((void*)_buf[i], (void*)_first, _bytes, _bytes); }
                _firstSerial = Serial;
            }
            _displaySerial++;
        }
        Interlocked.Increment(ref _decodedThisSecond);
        if (Mode == DeckMode.Preroll) PrerollPicture?.Invoke(this, Serial);
    }

    /// <summary>
    /// libVLC asks for the format when it opens the sound: the format stays "S16N" (amem's default, not touched), the rate is
    /// fixed to 48 kHz, the channel count of the file is kept (1..8) instead of a stereo downmix.
    /// </summary>
    private int OnAudioSetup(ref IntPtr opaque, ref IntPtr format, ref uint rate, ref uint channels)
    {
        rate = SampleRate;
        channels = Math.Clamp(channels, 1u, (uint)MaxChannels);
        if (_sidecar == null) SrcChannels = (int)channels;   // with FFmpeg sound its channel count counts
        return 0;
    }

    private unsafe void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_sidecar != null) return;   // FFmpeg gives the sound (VMX: libVLC's sound only keeps its clock running)
        int ch = SrcChannels;
        var c = new AudioChunk { Pts = pts, Frames = (int)count, Channels = ch, Data = new float[count * ch] };
        short* s = (short*)samples;
        for (int i = 0; i < c.Data.Length; i++) c.Data[i] = s[i] / 32768f;
        lock (_audioSync)
        {
            _audio.Enqueue(c);
            while (_audio.Count > 400) _audio.Dequeue();   // safety: never grow without limit
        }
    }

    private void OnAudioPause(IntPtr data, long pts) { lock (_audioSync) { _audioPaused = true; _pausePts = pts; } }

    private void OnAudioResume(IntPtr data, long pts)
    {
        lock (_audioSync)
        {
            long shift = pts - _pausePts;
            foreach (var c in _audio) c.Pts += shift;
            _audioPaused = false;
        }
    }

    public void Dispose()
    {
        try { Mp.Stop(); } catch { }
        _sidecar?.Dispose();
        DropVmx();
        Mp.Dispose();
        _media?.Dispose();
        lock (_sync)
        {
            foreach (var b in _buf) Marshal.FreeHGlobal(b);
            _buf = [];
            if (_first != IntPtr.Zero) { Marshal.FreeHGlobal(_first); _first = IntPtr.Zero; }
        }
    }
}
