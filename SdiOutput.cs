using System.Diagnostics;
using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace CuePlayer;

/// <summary>
/// One Blackmagic DeckLink / UltraStudio output port (scheduled playback, 8-bit YUV = UYVY, 48 kHz stereo embedded).
/// The output thread hands over one picture plus its sound per frame. While SDI is on, the card's clock paces the output thread:
/// <see cref="WaitFree"/> blocks until the card has shown a frame and its buffer is free again (pattern of ID4's DeckLinkChannel).
/// Picture and sound are counted on the same clock, so they cannot drift apart.
/// </summary>
internal sealed unsafe class SdiOutput : IDeckLinkVideoOutputCallback, IDisposable
{
    private const int PoolSize = 6;
    private const int Preroll = 3;   // frames queued ahead: about 3 frames of delay on the SDI output
    private const uint AudioChannels = 2;

    private readonly IDeckLinkOutput _output;
    private readonly IDeckLinkMutableVideoFrame[] _pool = new IDeckLinkMutableVideoFrame[PoolSize];
    private readonly SemaphoreSlim _free = new(PoolSize - Preroll, PoolSize);
    private readonly short[] _audio = new short[AudioChannels * 8192];
    private readonly long _duration, _scale;
    private readonly int _bytes;
    private long _scheduled;
    private int _next;
    private volatile bool _running;

    public string PortName { get; }
    public int Late, Dropped;

    /// <summary>Display names of the DeckLink devices that can output (empty without the Desktop Video driver).</summary>
    public static List<string> Ports()
    {
        var list = new List<string>();
        try
        {
            var it = (IDeckLinkIterator)new CDeckLinkIteratorClass();
            while (true)
            {
                IDeckLink? d;
                try { it.Next(out d); } catch { break; }
                if (d is null) break;
                if (d is IDeckLinkOutput)
                {
                    d.GetDisplayName(out var name);
                    list.Add(Unique(list, string.IsNullOrWhiteSpace(name) ? "DeckLink" : name));
                }
                else Marshal.ReleaseComObject(d);
            }
        }
        catch (Exception e) { Debug.WriteLine("DeckLink not available: " + e.Message); }
        return list;
    }

    private static string Unique(List<string> list, string name)
    {
        if (!list.Contains(name)) return name;
        for (int i = 2; ; i++) if (!list.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>The device by its name in <see cref="Ports"/> (the first one if the name is empty or gone).</summary>
    private static IDeckLinkOutput? Find(string wanted, out string name)
    {
        name = "";
        var ports = new List<string>();
        IDeckLinkOutput? first = null; string firstName = "";
        var it = (IDeckLinkIterator)new CDeckLinkIteratorClass();
        while (true)
        {
            IDeckLink? d;
            try { it.Next(out d); } catch { break; }
            if (d is null) break;
            if (d is not IDeckLinkOutput o) { Marshal.ReleaseComObject(d); continue; }
            d.GetDisplayName(out var n);
            n = Unique(ports, string.IsNullOrWhiteSpace(n) ? "DeckLink" : n);
            ports.Add(n);
            if (n == wanted) { if (first != null) Marshal.ReleaseComObject(first); name = n; return o; }
            if (first == null) { first = o; firstName = n; } else Marshal.ReleaseComObject(d);
        }
        name = firstName;
        return first;   // no name chosen yet, or the chosen one is gone: take the first
    }

    public SdiOutput(string port, OutputFormat fmt)
    {
        var output = Find(port, out var name) ?? throw new InvalidOperationException("no SDI device found");
        _output = output;
        PortName = name;

        // the card's own mode for this format: same size, same frame rate, same scan
        _output.GetDisplayModeIterator(out var modes);
        _BMDDisplayMode? found = null;
        while (found == null)
        {
            IDeckLinkDisplayMode? m;
            try { modes.Next(out m); } catch { break; }
            if (m is null) break;
            m.GetFrameRate(out var dur, out var scale);
            var dom = m.GetFieldDominance();
            bool interlaced = dom is _BMDFieldDominance.bmdLowerFieldFirst or _BMDFieldDominance.bmdUpperFieldFirst;
            if (m.GetWidth() == fmt.Width && m.GetHeight() == fmt.Height && interlaced == fmt.Interlaced && scale * fmt.RateD == dur * fmt.RateN)
            {
                _output.DoesSupportVideoMode(_BMDVideoConnection.bmdVideoConnectionUnspecified, m.GetDisplayMode(), _BMDPixelFormat.bmdFormat8BitYUV,
                    _BMDVideoOutputConversionMode.bmdNoVideoOutputConversion, _BMDSupportedVideoModeFlags.bmdSupportedVideoModeDefault, out _, out var ok);
                if (ok != 0) { found = m.GetDisplayMode(); _duration = dur; _scale = scale; }
            }
            Marshal.ReleaseComObject(m);
        }
        Marshal.ReleaseComObject(modes);
        if (found == null)
        {
            Marshal.FinalReleaseComObject(_output);
            throw new InvalidOperationException($"{fmt.Id} not supported by {name}");
        }

        _bytes = fmt.Width * fmt.Height * 2;
        for (int i = 0; i < PoolSize; i++)
        {
            _output.CreateVideoFrame(fmt.Width, fmt.Height, fmt.Width * 2, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, out _pool[i]);
            Write(_pool[i], p => { uint* d = (uint*)p; for (int k = 0; k < _bytes / 4; k++) d[k] = 0x10801080; });   // black
        }

        try
        {
            _output.SetScheduledFrameCompletionCallback(this);
            _output.EnableVideoOutput(found.Value, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
            _output.EnableAudioOutput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger,
                AudioChannels, _BMDAudioOutputStreamType.bmdAudioOutputStreamContinuous);

            // a few black frames and the same time of silence ahead, then the card runs
            for (int i = 0; i < Preroll; i++)
            {
                _output.ScheduleVideoFrame(_pool[i], _scheduled * _duration, _duration, _scale);
                _scheduled++;
            }
            _next = Preroll;
            _output.BeginAudioPreroll();
            int silence = (int)Math.Round(Preroll * fmt.FrameSeconds * 48000);
            Array.Clear(_audio);
            fixed (short* p = _audio) _output.ScheduleAudioSamples((IntPtr)p, (uint)Math.Min(silence, _audio.Length / AudioChannels), 0, 0, out _);
            _output.EndAudioPreroll();
            _running = true;
            _output.StartScheduledPlayback(0, _scale, 1.0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Output thread: waits until the card has room for the next frame. False = the card did not take one in time.</summary>
    public bool WaitFree(int ms) => _free.Wait(ms);

    /// <summary>Output thread, after <see cref="WaitFree"/> returned true: one picture (UYVY at the output size).</summary>
    public void SendVideo(IntPtr pic)
    {
        var frame = _pool[_next];
        _next = (_next + 1) % PoolSize;
        Write(frame, p => Buffer.MemoryCopy((void*)pic, p, _bytes, _bytes));

        // fell behind the card (the output thread was held up): continue a little ahead of what it shows now
        _output.GetScheduledStreamTime(_scale, out var now, out _);
        if (_scheduled * _duration < now + _duration) _scheduled = now / _duration + 2;
        _output.ScheduleVideoFrame(frame, _scheduled * _duration, _duration, _scale);
        _scheduled++;
    }

    /// <summary>Output thread: the sound of the frame just sent (continuous stream, n samples per frame as counted by the output thread).</summary>
    public void SendAudio(float[] planar, int channels, int n)
    {
        // planar float -> interleaved 16 bit; the first two channels (the output is stereo)
        n = Math.Min(n, _audio.Length / (int)AudioChannels);
        int ch2 = Math.Min(channels, 2);
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < AudioChannels; c++)
            {
                float v = c < ch2 ? planar[c * n + i] : (ch2 == 1 ? planar[i] : 0f);
                _audio[i * AudioChannels + c] = (short)Math.Clamp((int)MathF.Round(v * 32767f), -32768, 32767);
            }
        }
        fixed (short* p = _audio) _output.ScheduleAudioSamples((IntPtr)p, (uint)n, 0, 0, out _);
    }

    // ---- IDeckLinkVideoOutputCallback: card thread, keep short ----

    public void ScheduledFrameCompleted(IDeckLinkVideoFrame completedFrame, _BMDOutputFrameCompletionResult result)
    {
        if (result == _BMDOutputFrameCompletionResult.bmdOutputFrameDisplayedLate) Interlocked.Increment(ref Late);
        else if (result == _BMDOutputFrameCompletionResult.bmdOutputFrameDropped) Interlocked.Increment(ref Dropped);
        try { _free.Release(); } catch (SemaphoreFullException) { }
    }

    public void ScheduledPlaybackHasStopped() { }

    private delegate void FrameAction(byte* p);

    private static void Write(IDeckLinkMutableVideoFrame frame, FrameAction action)
    {
        var buffer = (IDeckLinkVideoBuffer)frame;
        buffer.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite);
        try
        {
            buffer.GetBytes(out var ptr);
            action((byte*)ptr);
        }
        finally { buffer.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessWrite); }
    }

    public void Dispose()
    {
        if (_running)
        {
            _running = false;
            try { _output.StopScheduledPlayback(0, out _, 0); } catch (Exception e) { Log.Write("SDI stop: " + e.Message); }
        }
        try { _output.SetScheduledFrameCompletionCallback(null); } catch { }
        try { _output.DisableAudioOutput(); } catch { }
        try { _output.DisableVideoOutput(); } catch { }
        foreach (var f in _pool) if (f != null) try { Marshal.FinalReleaseComObject(f); } catch { }
        try { Marshal.FinalReleaseComObject(_output); } catch { }
        _free.Dispose();
    }
}
