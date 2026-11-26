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
    public static readonly string[] All = [Stop, Hold, Next, Fade, Loop, LoopFade];
    public static string Normalize(string? a) => All.Contains(a) ? a! : Hold;
}

internal sealed class Cue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long DurationMs { get; set; }
    public string EndAction { get; set; } = CuePlayer.EndAction.Hold;

    /// <summary>File not found when the playlist was opened (shown in the list).</summary>
    public bool Missing { get; set; }

    /// <summary>Audio choice: "12", "34", "56", "78", "1", "2", "all", "t1".."t8" (track of a multi-track file) or "mute".</summary>
    public string Audio { get; set; } = AudioChoice.Default;

    /// <summary>Channels of each audio track of the file (read once with libVLC; null = not read yet, empty = no audio).</summary>
    public List<int>? AudioTracks { get; set; }

    /// <summary>The file has a video track (null = not read yet). Audio-only files go on air without waiting for a picture.</summary>
    public bool? HasVideo { get; set; }

    /// <summary>Why the picture cannot be decoded (e.g. VMX1 from vMix), found by a short test decode when added; null = fine / not checked.</summary>
    public string? Unsupported { get; set; }

    /// <summary>The test decode has been done for this file.</summary>
    public bool DecodeChecked { get; set; }

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
/// One track with several channels: pairs, single channels for stereo, "all" = every channel to the output (NDI/OMT then carry that
/// many channels). Several tracks (libVLC plays one at a time): "tN" = track N, labelled with its channel numbers counted across tracks.
/// </summary>
internal static class AudioChoice
{
    public const string Default = "12", All = "all", Mute = "mute";

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
    private static (int src, int dst)[] FileRoutes(string sel, int srcChannels) => ((Func<(string, int), (int src, int dst)[]>)(k =>
    {
        var (s, n) = k;
        if (s == Mute) return [];
        if (s == All) return Enumerable.Range(0, n).Select(i => (i, i)).ToArray();
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
    }))((sel, srcChannels));
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
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string? RelativePath { get; set; }
        public long DurationMs { get; set; }
        public string EndAction { get; set; } = CuePlayer.EndAction.Hold;
        public string Audio { get; set; } = AudioChoice.Default;
        public List<int>? AudioTracks { get; set; }
        public bool? HasVideo { get; set; }
        public bool? AudioRates48 { get; set; }
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
            };
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
    public double GainDb { get; set; }
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

    /// <summary>Runs a change under the lock and saves.</summary>
    public T Change<T>(Func<Show, T> change)
    {
        lock (_sync)
        {
            var r = change(Show);
            Save();
            return r;
        }
    }

    public void Change(Action<Show> change) => Change(s => { change(s); return 0; });

    public T Read<T>(Func<Show, T> read) { lock (_sync) return read(Show); }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Show, Json));
        File.Move(tmp, _file, true);
    }
}

internal static class Log
{
    private static readonly object Sync = new();
    private static readonly string File = System.IO.Path.Combine(ShowStore.Folder, "cueplayer.log");
    public static bool ToConsole { get; set; }

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
        lock (Sync)
        {
            try { System.IO.File.AppendAllText(File, text + Environment.NewLine); } catch { }
            if (ToConsole) Console.WriteLine(text);
        }
    }
}
