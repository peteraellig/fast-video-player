using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace CuePlayer;

/// <summary>
/// Test helper <c>--vmx FILE [frames]</c>: demuxes the video packets with FFmpeg, decodes them with libvmx and reports codec tag,
/// size, frame rate, decode time per picture; saves picture 50 as %TEMP%\cueplayer_vmx.png.
/// </summary>
internal static unsafe class VmxProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: CuePlayer --vmx FILE [frames]"); return 1; }
        int frames = args.Length > 2 && int.TryParse(args[2], out var f) ? f : 100;
        _ = FfmpegAudio.Status;   // sets ffmpeg.RootPath
        AVFormatContext* fmt = null;
        if (ffmpeg.avformat_open_input(&fmt, args[1], null, null) < 0) { Console.WriteLine("cannot open"); return 1; }
        ffmpeg.avformat_find_stream_info(fmt, null);
        int vs = -1;
        for (int i = 0; i < fmt->nb_streams; i++)
            if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) { vs = i; break; }
        if (vs < 0) { Console.WriteLine("no video stream"); return 1; }
        var st = fmt->streams[vs];
        uint tag = st->codecpar->codec_tag;
        string fourcc = new string(new[] { (char)(tag & 0xFF), (char)((tag >> 8) & 0xFF), (char)((tag >> 16) & 0xFF), (char)(tag >> 24) });
        int w = st->codecpar->width, h = st->codecpar->height;
        Console.WriteLine($"video stream {vs}: fourcc '{fourcc}' codec {st->codecpar->codec_id} {w}x{h} rate {st->avg_frame_rate.num}/{st->avg_frame_rate.den} tb {st->time_base.num}/{st->time_base.den} duration {fmt->duration / 1000} ms");
        for (int i = 0; i < fmt->nb_streams; i++) if (i != vs) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

        var vmx = VmxNative.VMX_Create(new VmxNative.VmxSize { Width = w, Height = h }, VmxNative.ProfileDefault, VmxNative.ColorSpaceBt709);
        if (vmx == IntPtr.Zero) { Console.WriteLine("VMX_Create failed"); return 1; }
        var buf = Marshal.AllocHGlobal(w * h * 2);
        var pkt = ffmpeg.av_packet_alloc();
        var sw = new Stopwatch();
        int n = 0; double maxMs = 0, sumMs = 0;
        while (n < frames && ffmpeg.av_read_frame(fmt, pkt) >= 0)
        {
            if (pkt->stream_index != vs) { ffmpeg.av_packet_unref(pkt); continue; }
            sw.Restart();
            int e1 = VmxNative.VMX_LoadFrom(vmx, pkt->data, pkt->size);
            int e2 = VmxNative.VMX_DecodeUYVY(vmx, (byte*)buf, w * 2);
            double ms = sw.Elapsed.TotalMilliseconds;
            sumMs += ms; maxMs = Math.Max(maxMs, ms);
            if (n < 3 || e1 != 0 || e2 != 0) Console.WriteLine($"packet {n}: {pkt->size} bytes pts {pkt->pts} load {e1} decode {e2} {ms:0.0} ms, mid luma {((byte*)buf)[(h / 2 * w + w / 2) * 2 + 1]}");
            if (n == 50) Save(buf, w, h, Path.Combine(Path.GetTempPath(), "cueplayer_vmx.png"));
            n++;
            ffmpeg.av_packet_unref(pkt);
        }
        Console.WriteLine($"{n} pictures, decode avg {sumMs / Math.Max(1, n):0.0} ms, max {maxMs:0.0} ms");
        ffmpeg.av_packet_free(&pkt);
        VmxNative.VMX_Destroy(vmx);
        Marshal.FreeHGlobal(buf);
        ffmpeg.avformat_close_input(&fmt);
        return 0;
    }

    private static void Save(IntPtr uyvy, int w, int h, string file)
    {
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        byte* s = (byte*)uyvy;
        for (int y = 0; y < h; y++)
        {
            byte* row = s + y * w * 2;
            byte* dst = (byte*)d.Scan0 + y * d.Stride;
            for (int x = 0; x < w; x++)
            {
                int u = row[(x & ~1) * 2] - 128, v = row[(x & ~1) * 2 + 2] - 128, yy = row[x * 2 + 1] - 16;
                int r = (int)(1.164 * yy + 1.793 * v), g = (int)(1.164 * yy - 0.213 * u - 0.533 * v), b = (int)(1.164 * yy + 2.112 * u);
                dst[x * 4] = (byte)Math.Clamp(b, 0, 255); dst[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255); dst[x * 4 + 2] = (byte)Math.Clamp(r, 0, 255);
            }
        }
        bmp.UnlockBits(d);
        bmp.Save(file);
        Console.WriteLine("saved " + file);
    }
}
