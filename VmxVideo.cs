using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace CuePlayer;

/// <summary>
/// The picture of a VMX file (vMix recordings, fourcc VMX1; libVLC cannot decode it). libVLC still plays the file with ":no-video" and
/// gives sound, play position, pause, jumps and the end as for any file. This reads the video packets with FFmpeg, decodes them with
/// libvmx (intra-only, ~1.3 ms per 1080 picture) into a small ring at the output size, and hands out the picture that belongs to the
/// deck's play position - the same idea as the FFmpeg sound of multi-track files.
/// </summary>
internal sealed unsafe class VmxVideo : IDisposable
{
    private const int Slots = 8, Ahead = 5;
    private const uint TagVmx1 = 0x31584D56;   // 'V' 'M' 'X' '1'

    /// <summary>Does the file's first video stream use VMX? (FFmpeg reads the header only.)</summary>
    public static bool IsVmx(string path)
    {
        _ = FfmpegAudio.Status;   // sets ffmpeg.RootPath
        if (!FfmpegAudio.Available) return false;
        AVFormatContext* fmt = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return false;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return false;
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var p = fmt->streams[i]->codecpar;
                if (p->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) return p->codec_tag == TagVmx1 || p->codec_id == AVCodecID.AV_CODEC_ID_VMIX;
            }
            return false;
        }
        catch { return false; }
        finally { if (fmt != null) ffmpeg.avformat_close_input(&fmt); }
    }

    /// <summary>Thumbnail of a VMX file: its first picture decoded by libvmx, scaled to the thumbnail size.</summary>
    public static bool Thumb(string path, string name)
    {
        _ = FfmpegAudio.Status;
        AVFormatContext* fmt = null;
        AVPacket* pkt = null;
        IntPtr vmx = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return false;
            ffmpeg.avformat_find_stream_info(fmt, null);
            int vs = -1;
            for (int i = 0; i < fmt->nb_streams; i++)
                if (vs < 0 && fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) vs = i; else fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            if (vs < 0) return false;
            int w = fmt->streams[vs]->codecpar->width, h = fmt->streams[vs]->codecpar->height;
            vmx = VmxNative.VMX_Create(new VmxNative.VmxSize { Width = w, Height = h }, VmxNative.ProfileDefault, VmxNative.ColorSpaceBt709);
            if (vmx == IntPtr.Zero) return false;
            buf = Marshal.AllocHGlobal(w * h * 2);
            pkt = ffmpeg.av_packet_alloc();
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                if (pkt->stream_index != vs) { ffmpeg.av_packet_unref(pkt); continue; }
                bool ok = VmxNative.VMX_LoadFrom(vmx, pkt->data, pkt->size) == 0 && VmxNative.VMX_DecodeUYVY(vmx, (byte*)buf, w * 2) == 0;
                ffmpeg.av_packet_unref(pkt);
                return ok && Thumbs.SaveUyvyScaled(buf, w, h, name);
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (vmx != IntPtr.Zero) VmxNative.VMX_Destroy(vmx);
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    private readonly string _path;
    private readonly OutputFormat _out;
    private readonly int _bytes;
    private readonly IntPtr[] _slot = new IntPtr[Slots];
    private readonly long[] _slotFrame = new long[Slots];   // frame number in the slot, -1 = empty (guarded by _sync)
    private readonly object _sync = new();
    private int _pinned = -1;          // slot the output thread is sending (never overwritten or freed while pinned)
    private long _want;                // frame the output asked for last
    private long _next;                // next frame number the reader will decode (after a seek: the first one it keeps)
    private long _seekTo = -1;
    private long _lastServed = -1;
    private double _fps = 25;
    private readonly Thread _thread;
    private volatile bool _stop;
    public string? Error { get; private set; }
    public int NewPictures;            // pictures handed out for the first time (for the "fps file" display)

    /// <summary>Frame 0 has been decoded and is in the ring.</summary>
    public event Action? FirstPicture;

    public VmxVideo(string path, OutputFormat output)
    {
        _path = path;
        _out = output;
        _bytes = output.Width * output.Height * 2;
        for (int i = 0; i < Slots; i++) { _slot[i] = Marshal.AllocHGlobal(_bytes); _slotFrame[i] = -1; }
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "vmx video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private long FrameAt(long ms) => (long)Math.Floor(ms * _fps / 1000.0 + 0.001);

    private void ReadLoop()
    {
        AVFormatContext* fmt = null;
        AVPacket* pkt = null;
        IntPtr vmx = IntPtr.Zero, decoded = IntPtr.Zero;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, _path, null, null) < 0) { Error = "FFmpeg cannot open the file"; return; }
            ffmpeg.avformat_find_stream_info(fmt, null);
            int vs = -1;
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                if (vs < 0 && fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) vs = i;
                else fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
            }
            if (vs < 0) { Error = "no video stream"; return; }
            var st = fmt->streams[vs];
            int w = st->codecpar->width, h = st->codecpar->height;
            var rate = st->avg_frame_rate.num > 0 ? st->avg_frame_rate : st->r_frame_rate;
            if (rate.num > 0 && rate.den > 0) _fps = rate.num / (double)rate.den;
            double tb = ffmpeg.av_q2d(st->time_base);
            vmx = VmxNative.VMX_Create(new VmxNative.VmxSize { Width = w, Height = h }, VmxNative.ProfileDefault, VmxNative.ColorSpaceBt709);
            if (vmx == IntPtr.Zero) { Error = "libvmx could not start"; return; }
            bool same = w == _out.Width && h == _out.Height;
            if (!same) decoded = Marshal.AllocHGlobal(w * h * 2);
            pkt = ffmpeg.av_packet_alloc();
            bool eof = false, firstDone = false;

            while (!_stop)
            {
                long seek, want;
                lock (_sync) { seek = _seekTo; _seekTo = -1; want = _want; }
                if (seek >= 0)
                {
                    // intra-only codec: every packet is a picture of its own, so a jump needs no key frame search
                    ffmpeg.av_seek_frame(fmt, vs, (long)(seek / _fps / tb), ffmpeg.AVSEEK_FLAG_BACKWARD);
                    lock (_sync) { _next = seek; for (int i = 0; i < Slots; i++) if (i != _pinned) _slotFrame[i] = -1; }
                    eof = false;
                }

                long next; lock (_sync) next = _next;
                if (eof || next >= want + Ahead) { Thread.Sleep(2); continue; }

                if (ffmpeg.av_read_frame(fmt, pkt) < 0) { eof = true; continue; }
                if (pkt->stream_index != vs) { ffmpeg.av_packet_unref(pkt); continue; }
                long pts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                long frame = pts == ffmpeg.AV_NOPTS_VALUE ? next : (long)Math.Round(pts * tb * _fps);
                if (frame < next) { ffmpeg.av_packet_unref(pkt); continue; }   // before the jump target (seek lands a little early)

                // a slot that is free or holds a picture the output has passed (never the pinned one)
                int slot = -1;
                lock (_sync)
                {
                    long oldest = long.MaxValue;
                    for (int i = 0; i < Slots; i++)
                    {
                        if (i == _pinned) continue;
                        if (_slotFrame[i] < 0) { slot = i; break; }
                        if (_slotFrame[i] < oldest && _slotFrame[i] < _want) { oldest = _slotFrame[i]; slot = i; }
                    }
                    if (slot >= 0) _slotFrame[slot] = -1;
                }
                if (slot < 0) { ffmpeg.av_packet_unref(pkt); Thread.Sleep(2); lock (_sync) _seekTo = Math.Max(frame, _want); continue; }

                IntPtr dst = same ? _slot[slot] : decoded;
                int e1 = VmxNative.VMX_LoadFrom(vmx, pkt->data, pkt->size);
                int e2 = e1 == 0 ? VmxNative.VMX_DecodeUYVY(vmx, (byte*)dst, w * 2) : e1;
                ffmpeg.av_packet_unref(pkt);
                if (e2 != 0) { Error ??= $"libvmx error {e2} at picture {frame}"; continue; }
                if (!same) Fit((byte*)decoded, w, h, (byte*)_slot[slot]);
                lock (_sync) { _slotFrame[slot] = frame; _next = frame + 1; }
                if (frame == 0 && !firstDone) { firstDone = true; FirstPicture?.Invoke(); }
            }
        }
        catch (Exception e) { Error = "VMX picture failed: " + e.Message; }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (vmx != IntPtr.Zero) VmxNative.VMX_Destroy(vmx);
            if (decoded != IntPtr.Zero) Marshal.FreeHGlobal(decoded);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            if (Error != null) Log.Write($"vmx {Path.GetFileName(_path)}: {Error}");
        }
    }

    /// <summary>Another picture size than the output: scaled to fit (nearest pixel pair), black bars around.</summary>
    private void Fit(byte* src, int w, int h, byte* dst)
    {
        int ow = _out.Width, oh = _out.Height;
        double scale = Math.Min(ow / (double)w, oh / (double)h);
        int fw = ((int)(w * scale)) & ~1, fh = (int)(h * scale);
        int x0 = ((ow - fw) / 2) & ~1, y0 = (oh - fh) / 2;
        uint* d32 = (uint*)dst;
        for (int i = 0; i < _bytes / 4; i++) d32[i] = 0x10801080;
        for (int y = 0; y < fh; y++)
        {
            int sy = Math.Min(h - 1, (int)(y / scale));
            uint* srow = (uint*)(src + sy * w * 2);
            uint* drow = (uint*)(dst + (y0 + y) * ow * 2) + x0 / 2;
            for (int x = 0; x < fw / 2; x++) drow[x] = srow[Math.Min(w / 2 - 1, (int)(x * 2 / scale) / 2)];
        }
    }

    /// <summary>Frame 0 into dst (the deck's first picture). False = not decoded (yet).</summary>
    public bool CopyFrame0(IntPtr dst, int bytes)
    {
        lock (_sync)
        {
            if (bytes != _bytes) return false;
            for (int i = 0; i < Slots; i++)
                if (_slotFrame[i] == 0) { Buffer.MemoryCopy((void*)_slot[i], (void*)dst, bytes, bytes); return true; }
            return false;
        }
    }

    /// <summary>The picture for play position ms into dst (Hold: the deck keeps it after the reader is gone). False = none.</summary>
    public bool CopyAt(long ms, IntPtr dst, int bytes)
    {
        long want = FrameAt(ms);
        lock (_sync)
        {
            if (bytes != _bytes) return false;
            int best = -1;
            for (int i = 0; i < Slots; i++)
                if (_slotFrame[i] >= 0 && _slotFrame[i] <= want && (best < 0 || _slotFrame[i] > _slotFrame[best])) best = i;
            if (best < 0 || _slot[best] == IntPtr.Zero) return false;
            Buffer.MemoryCopy((void*)_slot[best], (void*)dst, bytes, bytes);
            return true;
        }
    }

    public bool HasPicture { get { lock (_sync) { foreach (var f in _slotFrame) if (f >= 0) return true; return false; } } }

    /// <summary>
    /// Output thread: the picture for play position <paramref name="ms"/> (the newest decoded one not after it), pinned until
    /// <see cref="Release"/>. Asks the reader to jump when the position is outside what it has.
    /// </summary>
    public IntPtr Acquire(long ms, out long serial)
    {
        long want = FrameAt(ms);
        lock (_sync)
        {
            _want = want;
            int best = -1;
            long lo = long.MaxValue, hi = long.MinValue;
            for (int i = 0; i < Slots; i++)
            {
                long f = _slotFrame[i];
                if (f < 0) continue;
                lo = Math.Min(lo, f); hi = Math.Max(hi, f);
                if (f <= want && (best < 0 || f > _slotFrame[best])) best = i;
            }
            // jumped back, or far ahead of what is decoded (seek, Last 10 s): the reader starts again at the wanted picture
            if (_seekTo < 0 && (lo == long.MaxValue ? want != _next && want > 0 : want < lo || want > Math.Max(hi, _next) + _fps))
                _seekTo = want;
            if (best < 0) { serial = _lastServed; _pinned = -1; return IntPtr.Zero; }
            long frame = _slotFrame[best];
            if (frame != _lastServed) { _lastServed = frame; NewPictures++; }
            _pinned = best;
            serial = frame;
            return _slot[best];
        }
    }

    public void Release() { lock (_sync) _pinned = -1; }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        lock (_sync)
        {
            // the output thread may still send the pinned picture: wait a moment for it
            for (int k = 0; k < 50 && _pinned >= 0; k++) { Monitor.Wait(_sync, 2); }
            for (int i = 0; i < Slots; i++) { if (_slot[i] != IntPtr.Zero) Marshal.FreeHGlobal(_slot[i]); _slot[i] = IntPtr.Zero; _slotFrame[i] = -1; }
        }
    }
}
