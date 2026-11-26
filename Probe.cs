using System.Runtime.InteropServices;

namespace CuePlayer;

/// <summary>Test receiver: connects to an OMT or NDI source and prints pictures/s, size, mean brightness and sound level per second.</summary>
internal static unsafe class Probe
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: CuePlayer --probe \"HOST (Name)\" [seconds] [omt|ndi]"); return 1; }
        string address = args[1];
        int seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 8;
        bool ndi = args.Length > 3 && args[3].Equals("ndi", StringComparison.OrdinalIgnoreCase);
        return ndi ? RunNdi(address, seconds) : RunOmt(address, seconds);
    }

    private sealed class Stats
    {
        public int Frames, Width, Height, Interlaced; public double Luma; public double SumSq; public long Samples; public string Rate = "";
        public void Video(IntPtr data, int w, int h, int stride, string rate, bool interlaced)
        {
            Frames++; Width = w; Height = h; Rate = rate; Interlaced = interlaced ? 1 : 0;
            byte* row = (byte*)data + (long)(h / 2) * stride;
            long sum = 0;
            for (int x = 0; x < w; x++) sum += row[x * 2 + 1];
            Luma = (double)sum / w;
        }
        // per channel (up to 8): the audio choices are checked by the level of each output channel
        private readonly double[] _chSq = new double[8];
        private long _chSamples;
        private int _channels;

        public void Audio(IntPtr planar, int channels, int samples, int channelStrideBytes)
        {
            for (int c = 0; c < channels; c++)
            {
                float* p = (float*)((byte*)planar + (long)c * channelStrideBytes);
                double s = 0;
                for (int i = 0; i < samples; i++) s += p[i] * p[i];
                SumSq += s;
                if (c < 8) _chSq[c] += s;
            }
            Samples += (long)samples * channels;
            _chSamples += samples;
            _channels = channels;
        }
        public void Print(int second)
        {
            double rms = Samples > 0 ? Math.Sqrt(SumSq / Samples) : 0;
            var per = _chSamples > 0 ? string.Join(" ", Enumerable.Range(0, Math.Min(_channels, 8)).Select(c => Math.Sqrt(_chSq[c] / _chSamples).ToString("F3"))) : "";
            Console.WriteLine($"{second,3}s pictures={Frames} {Width}x{Height} rate={Rate} interlaced={Interlaced} midLuma={Luma:F1} audioRms={rms:F4} ch={_channels} [{per}]");
            Frames = 0; SumSq = 0; Samples = 0; _chSamples = 0; Array.Clear(_chSq);
        }
    }

    private static int RunOmt(string address, int seconds)
    {
        IntPtr r = OmtNative.omt_receive_create(address, OmtNative.FrameTypeVideo | OmtNative.FrameTypeAudio, 0, 0);
        if (r == IntPtr.Zero) { Console.WriteLine("omt_receive_create failed"); return 2; }
        var st = new Stats();
        var start = DateTime.UtcNow; int sec = 1;
        while ((DateTime.UtcNow - start).TotalSeconds < seconds)
        {
            IntPtr fp = OmtNative.omt_receive(r, OmtNative.FrameTypeVideo | OmtNative.FrameTypeAudio, 100);
            if (fp != IntPtr.Zero)
            {
                var f = Marshal.PtrToStructure<OmtNative.MediaFrame>(fp);
                if (f.Type == OmtNative.FrameTypeVideo && f.Data != IntPtr.Zero) st.Video(f.Data, f.Width, f.Height, f.Stride, $"{f.FrameRateN}/{f.FrameRateD}", (f.Flags & 1) != 0);
                else if (f.Type == OmtNative.FrameTypeAudio && f.Data != IntPtr.Zero) st.Audio(f.Data, f.Channels, f.SamplesPerChannel, f.SamplesPerChannel * 4);
            }
            if ((DateTime.UtcNow - start).TotalSeconds >= sec) st.Print(sec++);
        }
        OmtNative.omt_receive_destroy(r);
        return 0;
    }

    private static int RunNdi(string name, int seconds)
    {
        if (!NdiNative.Load()) { Console.WriteLine(NdiNative.Status); return 2; }
        IntPtr r = NdiNative.ReceiveCreate(name);
        if (r == IntPtr.Zero) { Console.WriteLine("NDI receive create failed"); return 2; }
        var st = new Stats();
        var start = DateTime.UtcNow; int sec = 1;
        while ((DateTime.UtcNow - start).TotalSeconds < seconds)
        {
            NdiNative.VideoFrame v; NdiNative.AudioFrame a;
            int t = NdiNative.Capture(r, &v, &a, 100);
            if (t == NdiNative.FrameVideo) { st.Video(v.Data, v.Width, v.Height, v.Stride, $"{v.RateN}/{v.RateD}", v.FrameFormat == 0); NdiNative.FreeVideo(r, &v); }
            else if (t == NdiNative.FrameAudio) { st.Audio(a.Data, a.Channels, a.Samples, a.ChannelStride); NdiNative.FreeAudio(r, &a); }
            if ((DateTime.UtcNow - start).TotalSeconds >= sec) st.Print(sec++);
        }
        NdiNative.ReceiveDestroy(r);
        return 0;
    }
}
