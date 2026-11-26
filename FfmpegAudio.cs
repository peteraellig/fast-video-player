using FFmpeg.AutoGen;

namespace CuePlayer;

/// <summary>
/// Reads the sound of a file with several audio tracks (e.g. MXF with 8 mono tracks) with FFmpeg - libVLC plays only one track at a time
/// (Peter: pairs such as 1+2 must work for MXF). The file is read once; all audio tracks (up to 8 channels in total, counted across the
/// tracks) are decoded into a ring buffer indexed by their timestamps (48 kHz samples from the start of the file).
/// The output thread takes the samples at the play position of the picture (the libVLC deck), so the sound follows play, pause, jumps and
/// Last 10 s. libVLC's own sound is switched off for such files.
/// </summary>
internal sealed unsafe class FfmpegAudio : IDisposable
{
    public const int Rate = 48000;
    private const int RingSamples = Rate * 8, AheadSamples = Rate * 3, ResyncSamples = Rate / 20;   // 8 s ring, 3 s read ahead, 50 ms

    public static bool Available { get; }
    public static string Status { get; } = "not loaded";

    static FfmpegAudio()
    {
        try
        {
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            Status = "FFmpeg " + ffmpeg.av_version_info();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);   // no notes such as "non-interleaved AVI" on every open
            Available = true;
        }
        catch (Exception e) { Status = "FFmpeg not available: " + e.Message; }
    }

    public int Channels { get; }
    public int Resyncs, Underruns, Seeks;   // diagnostics (test mode prints them)
    public long LastResyncOff;              // ms the sound was off the picture at the last resync (+ = ahead)
    public string? Error { get; private set; }

    private readonly string _path;
    private readonly float[][] _ring;
    private readonly object _sync = new();
    private long _start, _end;                 // decoded range (absolute samples), guarded by _sync
    private long[] _streamEnd = [];            // per used stream
    private long _cursor = long.MinValue;      // next sample the output takes
    private long _seekTarget = -1;             // asked by the output thread, done by the reader thread
    private readonly Thread _thread;
    private volatile bool _stop;

    public FfmpegAudio(string path, int channels)
    {
        _path = path;
        Channels = Math.Clamp(channels, 1, Deck.MaxChannels);
        _ring = new float[Channels][];
        for (int c = 0; c < Channels; c++) _ring[c] = new float[RingSamples];
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private sealed class Track { public int Stream; public AVCodecContext* Ctx; public int FirstChannel; public int Channels; public AVRational TimeBase; public int Slot; }

    private void ReadLoop()
    {
        AVFormatContext* fmt = null;
        var tracks = new List<Track>();
        AVPacket* pkt = null;
        AVFrame* frame = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, _path, null, null) < 0) { Error = "FFmpeg cannot open the file"; return; }
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) { Error = "FFmpeg cannot read the streams"; return; }
            int ch = 0;
            var byStream = new Dictionary<int, Track>();
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO || ch >= Channels) { st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                if (st->codecpar->sample_rate != Rate) { Error = $"audio track {tracks.Count + 1}: {st->codecpar->sample_rate} Hz (only 48 kHz)"; st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
                if (codec == null) { st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                var ctx = ffmpeg.avcodec_alloc_context3(codec);
                ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar);
                if (ffmpeg.avcodec_open2(ctx, codec, null) < 0) { ffmpeg.avcodec_free_context(&ctx); st->discard = AVDiscard.AVDISCARD_ALL; continue; }
                int n = Math.Min(Math.Max(1, st->codecpar->ch_layout.nb_channels), Channels - ch);
                var t = new Track { Stream = i, Ctx = ctx, FirstChannel = ch, Channels = n, TimeBase = st->time_base, Slot = tracks.Count };
                tracks.Add(t);
                byStream[i] = t;
                ch += n;
            }
            if (tracks.Count == 0) { Error ??= "no audio track FFmpeg can decode"; return; }
            lock (_sync) _streamEnd = new long[tracks.Count];
            double startSec = fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : fmt->start_time / (double)ffmpeg.AV_TIME_BASE;
            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            bool eof = false;

            while (!_stop)
            {
                long target;
                lock (_sync) { target = _seekTarget; _seekTarget = -1; }
                if (target >= 0)
                {
                    long ts = (long)((startSec + target / (double)Rate - 0.1) * ffmpeg.AV_TIME_BASE);
                    ffmpeg.av_seek_frame(fmt, -1, Math.Max(0, ts), ffmpeg.AVSEEK_FLAG_BACKWARD);
                    foreach (var t in tracks) ffmpeg.avcodec_flush_buffers(t.Ctx);
                    lock (_sync) { _start = _end = target; Array.Fill(_streamEnd, target); }
                    eof = false;
                }

                bool full;
                lock (_sync) full = _end - Math.Max(_cursor, _start) > AheadSamples || _end - _start > RingSamples - Rate;
                if (full || eof) { Thread.Sleep(10); continue; }

                int r = ffmpeg.av_read_frame(fmt, pkt);
                if (r < 0) { eof = true; continue; }
                if (!byStream.TryGetValue(pkt->stream_index, out var tr)) { ffmpeg.av_packet_unref(pkt); continue; }
                ffmpeg.avcodec_send_packet(tr.Ctx, pkt);
                ffmpeg.av_packet_unref(pkt);
                while (ffmpeg.avcodec_receive_frame(tr.Ctx, frame) == 0)
                {
                    long pts = frame->best_effort_timestamp;
                    if (pts != ffmpeg.AV_NOPTS_VALUE) Write(tr, frame, (long)Math.Round((pts * ffmpeg.av_q2d(tr.TimeBase) - startSec) * Rate));
                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        catch (Exception e) { Error = "FFmpeg audio failed: " + e.Message; Log.Write(Error + " (" + _path + ")"); }
        finally
        {
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            foreach (var t in tracks) { var c = t.Ctx; ffmpeg.avcodec_free_context(&c); }
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
            if (Error != null) Log.Write($"ffmpeg audio {Path.GetFileName(_path)}: {Error}");
        }
    }

    /// <summary>Converts one decoded frame to float and stores it at its position in the ring.</summary>
    private void Write(Track tr, AVFrame* f, long pos)
    {
        int nb = f->nb_samples, fch = Math.Max(1, f->ch_layout.nb_channels);
        var sf = (AVSampleFormat)f->format;
        bool planar = ffmpeg.av_sample_fmt_is_planar(sf) != 0;
        lock (_sync)
        {
            for (int i = 0; i < nb; i++)
            {
                long s = pos + i;
                if (s < _start || s >= _start + RingSamples) continue;   // before a jump target / would overwrite unread sound
                int slot = (int)(s % RingSamples);
                for (int c = 0; c < tr.Channels && c < fch; c++)
                    _ring[tr.FirstChannel + c][slot] = Sample(f, sf, planar, fch, c, i);
            }
            if (pos + nb > _streamEnd[tr.Slot]) _streamEnd[tr.Slot] = pos + nb;
            long end = long.MaxValue;
            foreach (var e in _streamEnd) end = Math.Min(end, e);
            _end = Math.Max(_end, end);
        }
    }

    private static float Sample(AVFrame* f, AVSampleFormat sf, bool planar, int fch, int c, int i)
    {
        byte* p = planar ? f->extended_data[c] : f->extended_data[0];
        int idx = planar ? i : i * fch + c;
        return sf switch
        {
            AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P => ((short*)p)[idx] / 32768f,
            AVSampleFormat.AV_SAMPLE_FMT_S32 or AVSampleFormat.AV_SAMPLE_FMT_S32P => ((int*)p)[idx] / 2147483648f,   // 24-bit PCM arrives as S32
            AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP => ((float*)p)[idx],
            AVSampleFormat.AV_SAMPLE_FMT_DBL or AVSampleFormat.AV_SAMPLE_FMT_DBLP => (float)((double*)p)[idx],
            AVSampleFormat.AV_SAMPLE_FMT_U8 or AVSampleFormat.AV_SAMPLE_FMT_U8P => (p[idx] - 128) / 128f,
            _ => 0f,
        };
    }

    /// <summary>
    /// Output thread: adds the n samples at the picture's play position (<paramref name="mediaMs"/>) into planar, routed by file channel.
    /// Small differences are bridged by continuing where the last call stopped; more than 50 ms off (jump, start) = resync, and
    /// a position outside the decoded range makes the reader seek there. Not decoded yet / paused = silence. Peaks go into peaks[].
    /// </summary>
    public void Mix(float[] planar, int n, int outCh, (int src, int dst)[] routes, float gain, long mediaMs, bool playing, float[] peaks)
    {
        if (!playing) return;
        long target = mediaMs * Rate / 1000;
        lock (_sync)
        {
            if (_cursor == long.MinValue || Math.Abs(_cursor - target) > ResyncSamples)
            {
                if (_cursor != long.MinValue) { Resyncs++; LastResyncOff = (_cursor - target) * 1000 / Rate; }
                _cursor = target;
                if (target < _start || target > _end + Rate / 2) { _seekTarget = target; Seeks++; }
            }
            long c0 = _cursor;
            _cursor += n;
            if (c0 < _start || c0 + n > _end) { Underruns++; return; }   // not there yet
            for (int ch = 0; ch < Channels && ch < peaks.Length; ch++)
            {
                var ring = _ring[ch];
                float pk = peaks[ch];
                for (int i = 0; i < n; i++) { float v = Math.Abs(ring[(int)((c0 + i) % RingSamples)]); if (v > pk) pk = v; }
                peaks[ch] = pk;
            }
            foreach (var (src, dst) in routes)
            {
                if (src >= Channels || dst >= outCh) continue;
                var ring = _ring[src];
                int o = dst * n;
                for (int i = 0; i < n; i++) planar[o + i] += ring[(int)((c0 + i) % RingSamples)] * gain;
            }
            if (c0 - _start > Rate) _start = c0 - Rate / 2;   // keep half a second behind the cursor, free the rest for reading ahead
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}
