using System.Text.Json;

namespace CuePlayer;

internal sealed record OutputFormat(string Id, int Width, int Height, int RateN, int RateD, bool Interlaced)
{
    // Interlaced = woven full-height picture at the FRAME rate with the interlaced flag (1080i50 = 25 pictures/s), like PresenterOmt.
    public static readonly OutputFormat[] All =
    [
        new("1080p50", 1920, 1080, 50, 1, false),
        new("1080p25", 1920, 1080, 25, 1, false),
        new("1080i50", 1920, 1080, 25, 1, true),
        new("1080p59.94", 1920, 1080, 60000, 1001, false),
        new("1080i59.94", 1920, 1080, 30000, 1001, true),
        new("1080p60", 1920, 1080, 60, 1, false),
        new("720p50", 1280, 720, 50, 1, false),
        new("720p59.94", 1280, 720, 60000, 1001, false),
        new("2160p50", 3840, 2160, 50, 1, false),
        new("2160p25", 3840, 2160, 25, 1, false),
    ];

    public static OutputFormat Find(string? id) => All.FirstOrDefault(f => f.Id == id) ?? All[0];

    public double FrameSeconds => (double)RateD / RateN;
}

/// <summary>What happens when a cue reaches its end.</summary>
internal static class EndAction
{
    public const string Stop = "stop";   // output goes black
    public const string Hold = "hold";   // last picture stays
    public const string Next = "next";   // next cue starts with a cut (prerolled: gapless)
    public const string Fade = "fade";   // next cue fades in over the fade time (prerolled)
    public const string Loop = "loop";   // cue repeats (cut back to its start, prerolled: gapless)
    public const string LoopFade = "loopfade";   // cue repeats with a fade into its own start (for clips without a clean end)
    // Peter 2026-11-28 (Mitti: "Goto after End"): another cue follows, chosen per cue (Cue.JumpTo) - e.g. the last cue jumps to the first
    // and the whole playlist repeats. A target that is gone = Hold.
    public const string Jump = "jump";           // cut to the target cue (prerolled: gapless)
    public const string JumpFade = "jumpfade";   // fade into the target cue
    public static readonly string[] All = [Stop, Hold, Next, Fade, Loop, LoopFade, Jump, JumpFade];
    public static string Normalize(string? a) => All.Contains(a) ? a! : Hold;
    public static bool IsJump(string? a) => a is Jump or JumpFade;
}

/// <summary>
/// Still pictures (Peter 2026-11-27). libVLC shows them as a video (":image-duration=-1" = endless, its clock runs); the engine ends
/// them after the chosen time itself, so a change of the time applies also while the picture is on air.
/// </summary>
internal static class Still
{
    // TIFF: libVLC cannot decode it, but Prepared reads it with Windows' own decoder and hands libVLC a BMP
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];
    public const int DefaultSeconds = 10, MaxSeconds = 30;
    public static bool Is(string? path) => path != null && Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    public static int Normalize(int s) => Math.Clamp(s, 0, MaxSeconds);   // 0 = infinite

    private static readonly string CacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fastvideoplayer_stills");

    /// <summary>
    /// The picture at exactly the output size: scaled to fit, black bars, turned upright by its EXIF orientation; written once as BMP
    /// into a cache (key = file, its time and size, output size) and played by libVLC instead of the original. libVLC's picture decoders
    /// deliver RGB, and its canvas/croppadd filters only work on YUV - they were dropped silently and a 4:3 picture came out stretched
    /// to 16:9. Null = could not be read (libVLC then plays the original).
    /// </summary>
    public static string? Prepared(string path, int outW, int outH)
    {
        try
        {
            var fi = new FileInfo(path);
            string key = $"{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{outW}x{outH}";
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            Directory.CreateDirectory(CacheDir);
            string file = System.IO.Path.Combine(CacheDir, $"{hash}_{outW}x{outH}.bmp");
            if (File.Exists(file)) return file;

            using var src = System.Drawing.Image.FromFile(path);
            // EXIF orientation (photos from phones/cameras are often stored sideways)
            const int OrientationTag = 0x0112;
            if (src.PropertyIdList.Contains(OrientationTag))
            {
                var flip = BitConverter.ToUInt16(src.GetPropertyItem(OrientationTag)!.Value!, 0) switch
                {
                    2 => System.Drawing.RotateFlipType.RotateNoneFlipX, 3 => System.Drawing.RotateFlipType.Rotate180FlipNone,
                    4 => System.Drawing.RotateFlipType.Rotate180FlipX, 5 => System.Drawing.RotateFlipType.Rotate90FlipX,
                    6 => System.Drawing.RotateFlipType.Rotate90FlipNone, 7 => System.Drawing.RotateFlipType.Rotate270FlipX,
                    8 => System.Drawing.RotateFlipType.Rotate270FlipNone, _ => System.Drawing.RotateFlipType.RotateNoneFlipNone,
                };
                src.RotateFlip(flip);
            }
            double scale = Math.Min(outW / (double)src.Width, outH / (double)src.Height);
            int w = (int)Math.Round(src.Width * scale), h = (int)Math.Round(src.Height * scale);
            using var dst = new System.Drawing.Bitmap(outW, outH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(dst))
            {
                g.Clear(System.Drawing.Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, (outW - w) / 2, (outH - h) / 2, w, h);   // transparent parts end up on black
            }
            string tmp = file + ".tmp";
            dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Bmp);
            File.Move(tmp, file, true);
            return file;
        }
        catch (Exception e) { Log.Write($"still {path}: {e.Message}"); return null; }
    }

    /// <summary>Removes prepared pictures older than two days (called at start).</summary>
    public static void CleanCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var f in Directory.GetFiles(CacheDir))
                if (File.GetLastAccessTimeUtc(f) < DateTime.UtcNow.AddDays(-2)) File.Delete(f);
        }
        catch { }
    }
}

internal sealed class Cue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long DurationMs { get; set; }
    public string EndAction { get; set; } = CuePlayer.EndAction.Hold;

    /// <summary>End action Jump / Jump (fade): the Id of the cue that follows (an Id, so reordering keeps it right; null = none chosen).</summary>
    public string? JumpTo { get; set; }

    /// <summary>File not found when the playlist was opened (shown in the list).</summary>
    public bool Missing { get; set; }

    /// <summary>Still picture: shown for this many seconds, then the end action (0 = until the operator goes on). Peter: 1-30 s or infinite.</summary>
    public int StillSeconds { get; set; } = Still.DefaultSeconds;

    /// <summary>The file is a still picture (by its extension). Sent to the page, so the row shows the time instead of the audio choice.</summary>
    public bool IsStill => Still.Is(Path);

    /// <summary>Audio choice: "12", "34", "56", "78", "1".."8", "t1".."t8" (track of a multi-track file) or "mute".</summary>
    public string Audio { get; set; } = AudioChoice.Default;

    /// <summary>Channels of each audio track of the file (read once with libVLC; null = not read yet, empty = no audio).</summary>
    public List<int>? AudioTracks { get; set; }

    /// <summary>The file has a video track (null = not read yet). Audio-only files go on air without waiting for a picture.</summary>
    public bool? HasVideo { get; set; }

    /// <summary>Why the picture cannot be decoded (e.g. VMX1 from vMix), found by a short test decode when added; null = fine / not checked.</summary>
    public string? Unsupported { get; set; }

    /// <summary>The test decode has been done for this file.</summary>
    public bool DecodeChecked { get; set; }

    /// <summary>File name of the cue's thumbnail in <see cref="Thumbs.Folder"/> (null = none yet / audio only).</summary>
    public string? Thumb { get; set; }

    /// <summary>The picture is VMX (vMix recordings, fourcc VMX1): decoded by libvmx instead of libVLC (null = not checked yet).</summary>
    public bool? Vmx { get; set; }

    /// <summary>All audio tracks run at 48 kHz (null = not read yet) - needed for the FFmpeg sound of multi-track files.</summary>
    public bool? AudioRates48 { get; set; }

    /// <summary>
    /// FFmpeg reads the sound (libVLC's own sound off): several audio tracks (libVLC plays only one), and also ONE track with more than
    /// 2 channels - libVLC reorders those depending on the container (WAV 8 ch: 1 2 7 8 5 6 3 4, MOV 8 ch: file order), so the fixed
    /// re-mapping swapped 3+4 with 7+8 on a MOV (Peter 2026-11-26, checked in vMix). FFmpeg always gives the file's order.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesFfmpegAudio => AudioTracks is { Count: > 0 } && (AudioTracks.Count > 1 || AudioTracks.Sum() > 2) && AudioRates48 == true && FfmpegAudio.Available;
}

/// <summary>
/// Audio choices per cue (Peter: dropdown 12, 34, 56, 78, 1-8, mute - only what the file offers; stereo: 12, 1, 2, mute).
/// One track with several channels: pairs, and every channel alone (mono on both sides). The output is always stereo; an old saved
/// "all" falls back to the first option. Several tracks (libVLC plays one at a time): "tN" = track N, labelled with its channel numbers counted across tracks.
/// </summary>
internal static class AudioChoice
{
    public const string Default = "12", Mute = "mute";

    public static List<(string value, string label)> Options(Cue c) => Options(c.AudioTracks, c.UsesFfmpegAudio);

    public static string Normalize(string? value, Cue c) => Normalize(value, c.AudioTracks, c.UsesFfmpegAudio);

    /// <param name="multi">several tracks read together by FFmpeg: they count as one source with all their channels</param>
    public static List<(string value, string label)> Options(List<int>? tracks, bool multi = false)
    {
        var o = new List<(string, string)>();
        if (tracks == null) { o.Add(("12", "1+2")); o.Add(("1", "1")); o.Add(("2", "2")); }
        else if (tracks.Count == 1 || (multi && tracks.Count > 1))
        {
            int n = Math.Min(tracks.Sum(), Deck.MaxChannels);
            if (n <= 1) o.Add(("1", "1"));
            else if (n == 2) { o.Add(("12", "1+2")); o.Add(("1", "1")); o.Add(("2", "2")); }
            else
            {
                // pairs, then every channel alone (Peter: "1-8" = each single track 1..8, mono on both output channels)
                for (int p = 0; p + 1 < n; p += 2) o.Add(($"{p + 1}{p + 2}", $"{p + 1}+{p + 2}"));
                for (int k = 1; k <= n; k++) o.Add(($"{k}", $"{k}"));
            }
        }
        else if (tracks.Count > 1)
        {
            int ch = 1;
            for (int t = 0; t < Math.Min(tracks.Count, 8); t++)
            {
                int c = Math.Max(1, tracks[t]);
                o.Add(($"t{t + 1}", c >= 2 ? $"{ch}+{ch + 1}" : $"{ch}"));
                ch += c;
            }
        }
        o.Add((Mute, "Mute"));
        return o;
    }

    /// <summary>A choice the file offers; an unknown or impossible one falls back to the first option.</summary>
    public static string Normalize(string? value, List<int>? tracks, bool multi = false)
    {
        var opts = Options(tracks, multi);
        return opts.Any(x => x.value == value) ? value! : opts[0].value;
    }

    /// <summary>Audio track index for "tN" choices, else -1.</summary>
    public static int TrackIndex(string sel) => sel.Length >= 2 && sel[0] == 't' && int.TryParse(sel[1..], out var t) ? t - 1 : -1;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), (int src, int dst)[]> RouteCache = new();

    /// <summary>
    /// libVLC delivers 6 and 8 channels in its own (5.1/7.1) order, not the file's. Measured 2026-11-26 with test WAVs (channel k = level k):
    /// 6 ch arrive as file 1 2 5 6 3 4, 8 ch as file 1 2 7 8 5 6 3 4 (with and without a channel mask). This gives, for a file channel,
    /// the position in libVLC's buffer. 3, 4, 5 and 7 channels lose channels inside libVLC (same test) - left as they come.
    /// </summary>
    public static int VlcIndex(int fileChannel, int channels) => channels switch
    {
        6 => new[] { 0, 1, 4, 5, 2, 3 }[fileChannel],
        8 => new[] { 0, 1, 6, 7, 4, 5, 2, 3 }[fileChannel],
        _ => fileChannel,
    };

    /// <summary>Which channel of libVLC's buffer goes to which output channel (file channel numbers translated with VlcIndex).
    /// <paramref name="remap"/> false = the source is already in file order (FFmpeg sound).</summary>
    public static (int src, int dst)[] Routes(string sel, int srcChannels, bool remap = true) => remap
        ? RouteCache.GetOrAdd((sel, srcChannels), k => FileRoutes(k.Item1, k.Item2).Select(r => (r.src < k.Item2 ? VlcIndex(r.src, k.Item2) : r.src, r.dst)).ToArray())
        : FileRouteCache.GetOrAdd((sel, srcChannels), k => FileRoutes(k.Item1, k.Item2));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), (int src, int dst)[]> FileRouteCache = new();

    /// <summary>Which FILE channel goes to which output channel.</summary>
    private static (int src, int dst)[] FileRoutes(string s, int n)
    {
        if (s == Mute) return [];
        if (n <= 1) return [(0, 0), (0, 1)];   // mono file: on both sides
        if (s.Length == 1 && s[0] is >= '1' and <= '8')
        {
            int c = s[0] - '1';   // one channel alone, mono on both output channels
            return c < n ? [(c, 0), (c, 1)] : [(0, 0), (0, 1)];
        }
        if (s.Length == 2 && char.IsDigit(s[0]) && char.IsDigit(s[1]))
        {
            int a = s[0] - '1';
            return a + 1 < n ? [(a, 0), (a + 1, 1)] : [(0, 0), (1, 1)];
        }
        return [(0, 0), (1, 1)];   // "tN": the chosen track as it comes (stereo)
    }
}

/// <summary>A playlist file (*.cuelist, JSON). Each cue keeps its absolute path and the path relative to the playlist,
/// so a show folder can be copied to another PC or drive with its playlist.</summary>
internal static class Playlist
{
    private sealed class FileModel
    {
        public string Format { get; set; } = "CuePlayer playlist";
        public int Version { get; set; } = 1;
        public int FadeMs { get; set; } = CuePlayer.Fade.DefaultMs;
        public List<Entry> Cues { get; set; } = [];
    }

    private sealed class Entry
    {
        public string? Id { get; set; }          // since 0.1.0.44 (jump targets refer to it); older playlists: new ids at load
        public string? JumpTo { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string? RelativePath { get; set; }
        public long DurationMs { get; set; }
        public string EndAction { get; set; } = CuePlayer.EndAction.Hold;
        public string Audio { get; set; } = AudioChoice.Default;
        public List<int>? AudioTracks { get; set; }
        public bool? HasVideo { get; set; }
        public bool? AudioRates48 { get; set; }
        public int StillSeconds { get; set; } = Still.DefaultSeconds;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string file, IEnumerable<Cue> cues, int fadeMs)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!;
        var model = new FileModel
        {
            FadeMs = CuePlayer.Fade.Normalize(fadeMs),
            Cues = cues.Select(c => new Entry
            {
                Id = c.Id,
                JumpTo = c.JumpTo,
                Name = c.Name,
                Path = c.Path,
                RelativePath = System.IO.Path.GetPathRoot(c.Path)?.Equals(System.IO.Path.GetPathRoot(dir), StringComparison.OrdinalIgnoreCase) == true
                    ? System.IO.Path.GetRelativePath(dir, c.Path) : null,
                DurationMs = c.DurationMs,
                EndAction = c.EndAction,
                Audio = c.Audio,
                AudioTracks = c.AudioTracks,
                HasVideo = c.HasVideo,
                AudioRates48 = c.AudioRates48,
                StillSeconds = c.StillSeconds,
            }).ToList(),
        };
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, Json));
        File.Move(tmp, file, true);
    }

    /// <summary>Reads a playlist; a file that is not at its absolute path is looked for relative to the playlist.</summary>
    public static (List<Cue> cues, int missing, int fadeMs) Load(string file)
    {
        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file)) ?? throw new InvalidDataException("Not a CuePlayer playlist");
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!;
        var cues = new List<Cue>();
        var ids = new HashSet<string>();
        int missing = 0;
        foreach (var e in model.Cues)
        {
            string path = e.Path;
            if (!File.Exists(path) && !string.IsNullOrEmpty(e.RelativePath))
            {
                var rel = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, e.RelativePath));
                if (File.Exists(rel)) path = rel;
            }
            bool found = File.Exists(path);
            if (!found) missing++;
            var cue = new Cue
            {
                Path = path,
                Name = string.IsNullOrWhiteSpace(e.Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : e.Name,
                DurationMs = e.DurationMs,
                EndAction = CuePlayer.EndAction.Normalize(e.EndAction),
                Missing = !found,
                AudioTracks = e.AudioTracks,
                HasVideo = e.HasVideo,
                AudioRates48 = e.AudioRates48,
                StillSeconds = Still.Normalize(e.StillSeconds),
                JumpTo = e.JumpTo,
            };
            // the saved id (jump targets point at it), unless missing or used twice - then the new one from the constructor
            if (!string.IsNullOrWhiteSpace(e.Id) && ids.Add(e.Id)) cue.Id = e.Id; else ids.Add(cue.Id);
            cue.Audio = AudioChoice.Normalize(e.Audio, cue);
            cues.Add(cue);
        }
        return (cues, missing, CuePlayer.Fade.Normalize(model.FadeMs));
    }
}

internal sealed class ShowSettings
{
    public string Format { get; set; } = "1080p50";
    public const string DefaultSourceName = "fast video player";
    public string SourceName { get; set; } = DefaultSourceName;
    public bool Ndi { get; set; } = true;
    public bool Omt { get; set; } = true;
    public bool Sdi { get; set; }
    public string SdiPort { get; set; } = "";   // display name of the DeckLink device (empty = the first one)
    public string MonitorDevice { get; set; } = "";   // Windows audio device id for listening (empty = off)
    public bool Hdmi { get; set; }                     // full-screen output on a second monitor
    public string HdmiScreen { get; set; } = "";       // Windows display name (\\.\DISPLAY2); empty = the first one that is not the control screen
    public double GainDb { get; set; }

    // Remote control over HTTP (Peter 2026-11-27): off by default - the player is a closed system unless this is switched on
    public string RemoteMode { get; set; } = RemoteHttp.Off;   // off | local (127.0.0.1 only) | network
    public int RemotePort { get; set; } = RemoteHttp.DefaultPort;
    public string RemoteAllow { get; set; } = "";              // network mode: allowed client IPs, comma separated (empty = all)

    /// <summary>A copy (settings are replaced as a whole on Apply).</summary>
    public ShowSettings Clone() => (ShowSettings)MemberwiseClone();
}

internal static class Fade
{
    public const int DefaultMs = 300, MinMs = 100, MaxMs = 1000;   // Peter: dropdown in 100 ms steps, saved with the playlist
    public static int Normalize(int ms) => ms <= 0 ? DefaultMs : Math.Clamp((ms + 50) / 100 * 100, MinMs, MaxMs);
}

internal sealed class Show
{
    public ShowSettings Settings { get; set; } = new();
    public List<Cue> Cues { get; set; } = [];

    /// <summary>The playlist file the list was opened from / saved to (null = not saved yet).</summary>
    public string? PlaylistPath { get; set; }

    /// <summary>The list differs from the playlist file.</summary>
    public bool Modified { get; set; }

    /// <summary>Fade time for "Fade next" in this playlist (saved in the .cuelist).</summary>
    public int FadeMs { get; set; } = CuePlayer.Fade.DefaultMs;
}

/// <summary>The show (cue list + settings) in C:\ProgramData\CuePlayer\show.json, saved on every change.</summary>
internal sealed class ShowStore
{
    public static string Folder { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CuePlayer");
    private readonly string _file;
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Show Show { get; }

    public ShowStore(string? file = null)
    {
        Directory.CreateDirectory(Folder);
        _file = file ?? System.IO.Path.Combine(Folder, "show.json");
        Show = Load();
    }

    private Show Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var show = JsonSerializer.Deserialize<Show>(File.ReadAllText(_file)) ?? new Show();
                // the old default NDI/OMT name becomes the new one (Peter 2026-11-25); a name chosen by hand stays
                if (show.Settings.SourceName == "CuePlayer") show.Settings.SourceName = ShowSettings.DefaultSourceName;
                return show;
            }
        }
        catch (Exception e)
        {
            Log.Write("show.json unreadable, starting empty: " + e.Message);
            try { File.Copy(_file, _file + ".broken", true); } catch { }
        }
        return new Show();
    }

    /// <summary>Runs a change under the lock; the file is written shortly after in the background (see <see cref="Flush"/>).</summary>
    public T Change<T>(Func<Show, T> change)
    {
        T r;
        lock (_sync) { r = change(Show); Interlocked.Increment(ref _version); }
        RequestSave();
        return r;
    }

    private long _version;
    /// <summary>+1 on every change: the window sends the cue list to the page only when this moved.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Change(Action<Show> change) => Change(s => { change(s); return 0; });

    public T Read<T>(Func<Show, T> read) { lock (_sync) return read(Show); }

    // ---- saving (review 2026-11-27): the file was written inside the lock on every change; the command thread reads that lock every
    // 40 ms, so a slow disk / virus scan held up GO. Now: changes mark the show dirty, a timer writes it 200 ms later on a pool thread
    // (several changes = one write), outside the lock. A failed write (file locked) is logged and tried again 2 s later.
    private readonly object _saveSync = new(), _writeSync = new();
    private bool _dirty;
    private System.Threading.Timer? _saveTimer;
    private const int SaveDelayMs = 200, RetryDelayMs = 2000;

    private void RequestSave()
    {
        lock (_saveSync)
        {
            _dirty = true;
            _saveTimer ??= new System.Threading.Timer(_ => Flush());
            _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>Writes the show now if it changed (timer, and at exit). Never throws.</summary>
    public void Flush()
    {
        lock (_writeSync)
        {
            lock (_saveSync) { if (!_dirty) return; _dirty = false; }
            string json;
            lock (_sync) json = JsonSerializer.Serialize(Show, Json);   // in memory only, the disk is written outside the lock
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _file, true);
            }
            catch (Exception e)
            {
                Log.Write("show.json not saved, trying again: " + e.Message);
                lock (_saveSync)
                {
                    _dirty = true;
                    _saveTimer?.Change(RetryDelayMs, Timeout.Infinite);
                }
            }
        }
    }
}

internal static class Log
{
    private static readonly object Sync = new();
    private static readonly string File = System.IO.Path.Combine(ShowStore.Folder, "cueplayer.log");
    public static bool ToConsole { get; set; }
    private static readonly System.Diagnostics.Stopwatch Since = System.Diagnostics.Stopwatch.StartNew();
    private static long _lastStep;
    private const long MaxBytes = 5 * 1024 * 1024;
    private static int _linesSinceCheck;

    /// <summary>Start-up timing: "startup +N ms (+delta): what" - where the start of the program spends its time.</summary>
    public static void Step(string what)
    {
        long now = Since.ElapsedMilliseconds, d = now - Interlocked.Exchange(ref _lastStep, now);
        Write($"startup +{now} ms (+{d}): {what}");
    }

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
        lock (Sync)
        {
            // size limit: checked about every 500 lines; over 5 MB the log becomes cueplayer.log.old (one generation kept)
            if (++_linesSinceCheck >= 500 || _linesSinceCheck == 1)
            {
                _linesSinceCheck = 1;
                try { if (new FileInfo(File).Length > MaxBytes) System.IO.File.Move(File, File + ".old", true); } catch { }
            }
            try { System.IO.File.AppendAllText(File, text + Environment.NewLine); } catch { }
            if (ToConsole) Console.WriteLine(text);
        }
    }
}
