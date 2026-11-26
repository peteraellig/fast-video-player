using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace CuePlayer;

/// <summary>
/// Test helper <c>--sdi-in [seconds] [format] [port]</c>: captures the SDI input of a DeckLink / UltraStudio (with SDI out looped
/// to SDI in it receives what the player sends) and prints per second: frames, mid-line luma (16 = black), a picture signature
/// (changes = moving picture) and the sound level per channel.
/// </summary>
internal sealed unsafe class SdiProbe : IDeckLinkInputCallback
{
    private int _frames, _noSignal;
    private double _luma;
    private long _sig;
    private double _sumL, _sumR;
    private long _samples;
    private readonly object _sync = new();

    public static int Run(string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10;
        var fmt = OutputFormat.Find(args.Length > 2 ? args[2] : "1080p50");
        string wanted = args.Length > 3 ? args[3] : "";

        var it = (IDeckLinkIterator)new CDeckLinkIteratorClass();
        IDeckLinkInput? input = null; string name = "";
        while (input == null)
        {
            IDeckLink? d;
            try { it.Next(out d); } catch { break; }
            if (d is null) break;
            d.GetDisplayName(out var n);
            if (d is IDeckLinkInput i && (wanted == "" || n == wanted)) { input = i; name = n; }
        }
        if (input == null) { Console.WriteLine("no DeckLink input found"); return 1; }

        input.GetDisplayModeIterator(out var modes);
        _BMDDisplayMode? mode = null;
        while (mode == null)
        {
            IDeckLinkDisplayMode? m;
            try { modes.Next(out m); } catch { break; }
            if (m is null) break;
            m.GetFrameRate(out var dur, out var scale);
            var dom = m.GetFieldDominance();
            bool il = dom is _BMDFieldDominance.bmdLowerFieldFirst or _BMDFieldDominance.bmdUpperFieldFirst;
            if (m.GetWidth() == fmt.Width && m.GetHeight() == fmt.Height && il == fmt.Interlaced && scale * fmt.RateD == dur * fmt.RateN) mode = m.GetDisplayMode();
        }
        if (mode == null) { Console.WriteLine($"{fmt.Id} not offered by {name}"); return 1; }

        var probe = new SdiProbe();
        input.SetCallback(probe);
        input.EnableVideoInput(mode.Value, _BMDPixelFormat.bmdFormat8BitYUV, _BMDVideoInputFlags.bmdVideoInputFlagDefault);
        input.EnableAudioInput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 2);
        input.StartStreams();
        Console.WriteLine($"capturing {fmt.Id} from {name}");
        for (int t = 1; t <= seconds; t++)
        {
            Thread.Sleep(1000);
            lock (probe._sync)
            {
                double rmsL = probe._samples > 0 ? Math.Sqrt(probe._sumL / probe._samples) : 0;
                double rmsR = probe._samples > 0 ? Math.Sqrt(probe._sumR / probe._samples) : 0;
                string Db(double r) => r > 0 ? $"{20 * Math.Log10(r):0.0} dB" : "silent";
                Console.WriteLine($"{t,3}s frames={probe._frames} nosignal={probe._noSignal} luma={probe._luma:0} sig={probe._sig:X} audio L {Db(rmsL)} R {Db(rmsR)}");
                probe._frames = 0; probe._noSignal = 0; probe._sumL = probe._sumR = 0; probe._samples = 0;
            }
        }
        input.StopStreams();
        input.DisableAudioInput();
        input.DisableVideoInput();
        input.SetCallback(null);
        Marshal.FinalReleaseComObject(input);
        return 0;
    }

    public void VideoInputFormatChanged(_BMDVideoInputFormatChangedEvents events, IDeckLinkDisplayMode mode, _BMDDetectedVideoInputFormatFlags flags) { }

    public void VideoInputFrameArrived(IDeckLinkVideoInputFrame video, IDeckLinkAudioInputPacket audio)
    {
        lock (_sync)
        {
            if (video != null)
            {
                _frames++;
                if ((video.GetFlags() & _BMDFrameFlags.bmdFrameHasNoInputSource) != 0) _noSignal++;
                var buf = (IDeckLinkVideoBuffer)video;
                buf.StartAccess(_BMDBufferAccessFlags.bmdBufferAccessRead);
                try
                {
                    buf.GetBytes(out var p);
                    byte* b = (byte*)p;
                    int w = video.GetWidth(), h = video.GetHeight(), rb = video.GetRowBytes();
                    byte* row = b + (h / 2) * rb;
                    long sum = 0, sig = 17;
                    for (int x = 0; x < w; x++) sum += row[x * 2 + 1];
                    for (int y = 0; y < h; y += h / 16) for (int x = 0; x < w; x += w / 16) sig = sig * 31 + b[y * rb + x * 2 + 1];
                    _luma = (double)sum / w;
                    _sig = sig & 0xFFFFFF;
                }
                finally { buf.EndAccess(_BMDBufferAccessFlags.bmdBufferAccessRead); }
            }
            if (audio != null)
            {
                int n = audio.GetSampleFrameCount();
                audio.GetBytes(out var ap);
                short* s = (short*)ap;
                for (int i = 0; i < n; i++)
                {
                    double l = s[i * 2] / 32768.0, r = s[i * 2 + 1] / 32768.0;
                    _sumL += l * l; _sumR += r * r;
                }
                _samples += n;
            }
        }
    }
}
