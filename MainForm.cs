using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CuePlayer;

/// <summary>
/// The window: a WebView2 showing ui\index.html from the program folder (virtual host, no network port).
/// The page sends commands with chrome.webview.postMessage, the window answers with state messages (10/s).
/// The preview pictures go through a WebView2 shared buffer (two RGBA halves), one small "frame" message per picture,
/// so the preview runs at the media's own rate without any encoding.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly string[] MediaExtensions =
        [".mp4", ".mov", ".m4v", ".mkv", ".mxf", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".flv", ".mp3", ".wav", ".aac", ".m4a", ".flac",
         .. Still.Extensions];
    public const string AppTitle = "fast video player";
    private const string PlaylistFilter = "CuePlayer playlist (*.cuelist)|*.cuelist|All files|*.*";
    private const int PreviewBytes = PlayerEngine.PreviewWidth * PlayerEngine.PreviewHeight * 4;

    private readonly PlayerEngine _engine;
    private readonly ShowStore _store;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(11, 37, 71) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private CoreWebView2Environment? _env;
    private CoreWebView2SharedBuffer? _shared;
    private IntPtr _sharedPtr;              // written from the preview thread, so read once on the UI thread
    private volatile int _frameHalf;
    private int _framePending;
    private bool _ready;

    public MainForm(PlayerEngine engine, ShowStore store)
    {
        _engine = engine;
        _store = store;
        Text = AppTitle;
        BackColor = Color.FromArgb(11, 37, 71);
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        // the .ico next to the exe has all sizes (ExtractAssociatedIcon only gives 32 px, blurry in the title bar at higher scaling)
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "fast_video_player.ico")); }
        catch { try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } }
        Controls.Add(_web);
        _timer.Tick += (_, _) => PushState();
        Load += async (_, _) => { FitToScreen(); ProbeMissing(); await InitWebAsync(); };
        FormClosing += OnClosing;
        _engine.PreviewReady += OnPreview;
    }

    /// <summary>
    /// Window size so the right column (output, settings, keys) is not cut off: 1050 page pixels high (measured on Peter's screenshot,
    /// the keys card ends at about 1045), scaled by the screen DPI and limited to the working area of the screen.
    /// </summary>
    private void FitToScreen()
    {
        float s = DeviceDpi / 96f;
        var wa = Screen.FromControl(this).WorkingArea;
        Size Clamp(Size z) => new(Math.Min(z.Width, wa.Width), Math.Min(z.Height, wa.Height));
        // 1400 wide since the level meters sit beside the preview (right column 560 instead of 460)
        // 1300: below that the header (status | text logo | fps, clock) and the list buttons get too tight (checked at 1100)
        MinimumSize = Clamp(SizeFromClientSize(new Size((int)(1300 * s), (int)(1050 * s))));
        // 1680: Peter widened the window to this (2026-11-27), the list has room for the thumbnails
        Size = Clamp(SizeFromClientSize(new Size((int)(1680 * s), (int)(1050 * s))));
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
    }

    private async Task InitWebAsync()
    {
        _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ShowStore.Folder, "webview"));
        await _web.EnsureCoreWebView2Async(_env);
        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping("cueplayer.app", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
        // thumbnails of the cue list (made when files are probed), read-only images
        Directory.CreateDirectory(Thumbs.Folder);
        core.SetVirtualHostNameToFolderMapping("thumbs.cueplayer.app", Thumbs.Folder, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        // the page must never leave the program's own content
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://cueplayer.app/")) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.Navigate("https://cueplayer.app/index.html");
        _timer.Start();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (IsPlaying() &&
            MessageBox.Show(this, "A cue is playing. Quit the player and stop the output?", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!AskSaveChanges()) { e.Cancel = true; return; }
        _timer.Stop();
        _engine.PreviewReady -= OnPreview;
        _sharedPtr = IntPtr.Zero;
    }

    private bool IsPlaying() => JsonSerializer.SerializeToElement(_engine.Snapshot()).GetProperty("state").GetString() is "playing" or "paused";

    private void Post(object o)
    {
        if (_web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o, JsonOpts));
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void PushState()
    {
        if (_ready) Post(_engine.Snapshot());
        if (_ready) AskUnsupported();
    }

    // ================= preview =================

    private void SetupPreviewBuffer()
    {
        if (_env == null || _web.CoreWebView2 == null) return;
        if (_shared == null)
        {
            _shared = _env.CreateSharedBuffer((ulong)PreviewBytes * 2);
            _sharedPtr = _shared.Buffer;
        }
        _web.CoreWebView2.PostSharedBufferToScript(_shared, CoreWebView2SharedBufferAccess.ReadOnly,
            JsonSerializer.Serialize(new { type = "previewbuf", width = PlayerEngine.PreviewWidth, height = PlayerEngine.PreviewHeight }));
        // show the current picture at once
        var (rgba, _, _, _) = _engine.Preview();
        OnPreview(rgba, 0, 0);
    }

    /// <summary>Preview thread: copy into the shared buffer half, then tell the page (at most one message waiting at a time).</summary>
    private void OnPreview(byte[] rgba, int half, int serial)
    {
        var ptr = _sharedPtr;
        if (ptr == IntPtr.Zero) return;
        Marshal.Copy(rgba, 0, ptr + half * PreviewBytes, PreviewBytes);
        _frameHalf = half;
        if (Interlocked.Exchange(ref _framePending, 1) == 1) return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _framePending, 0);
                if (_ready && _sharedPtr != IntPtr.Zero) _web.CoreWebView2?.PostWebMessageAsJson($"{{\"type\":\"frame\",\"half\":{_frameHalf}}}");
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _framePending, 0); }   // window not ready or closing
    }

    // ================= messages from the page =================

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            string cmd = m.GetProperty("cmd").GetString() ?? "";
            string Str(string name) => m.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            switch (cmd)
            {
                case "ready": _ready = true; SetupPreviewBuffer(); PushState(); break;
                case "go": _engine.Go(); break;
                case "stop": _engine.Stop(); break;
                case "pause": _engine.TogglePause(); break;
                case "next": _engine.Next(); break;
                case "prev": _engine.Prev(); break;
                case "play": _engine.Play(Str("id")); break;
                case "select": _engine.Select(Str("id")); break;
                case "seek": _engine.Seek(m.GetProperty("ms").GetInt64()); break;
                case "last10": _engine.LastTen(); break;
                case "addFiles": AddWithDialog(); break;
                case "drop":
                    // files dragged from Explorer arrive as CoreWebView2File objects with their real path
                    var paths = new List<string>();
                    if (e.AdditionalObjects != null)
                        foreach (var o in e.AdditionalObjects)
                            if (o is CoreWebView2File f) paths.Add(f.Path);
                    if (paths.Count == 1 && paths[0].EndsWith(".cuelist", StringComparison.OrdinalIgnoreCase)) OpenPlaylist(paths[0]);
                    else AddPaths(paths, Str("before"));
                    break;
                case "remove": ChangeCues(s => s.Cues.RemoveAll(c => c.Id == Str("id"))); break;
                case "endAction":
                    ChangeCues(s => { var c = s.Cues.FirstOrDefault(c => c.Id == Str("id")); if (c != null) c.EndAction = EndAction.Normalize(Str("action")); });
                    break;
                case "still":
                    // display time of a still picture, applies at once (also while it is on air)
                    ChangeCues(s =>
                    {
                        var c = s.Cues.FirstOrDefault(c => c.Id == Str("id"));
                        if (c == null) return;
                        c.StillSeconds = Still.Normalize(m.GetProperty("seconds").GetInt32());
                        c.DurationMs = c.StillSeconds * 1000L;
                    });
                    break;
                case "audio":
                    // applies at once, also while the cue plays (the engine follows it within 40 ms)
                    ChangeCues(s => { var c = s.Cues.FirstOrDefault(c => c.Id == Str("id")); if (c != null) c.Audio = AudioChoice.Normalize(Str("value"), c); });
                    break;
                case "order":
                    var ids = m.GetProperty("ids").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    ChangeCues(s => s.Cues = s.Cues.OrderBy(c => { int i = ids.IndexOf(c.Id); return i < 0 ? int.MaxValue : i; }).ToList());
                    break;
                case "move":
                    // one place up (-1) or down (+1): the ▲ ▼ buttons and Alt+arrow keys
                    int dir = m.GetProperty("dir").GetInt32();
                    ChangeCues(s =>
                    {
                        int i = s.Cues.FindIndex(c => c.Id == Str("id")), j = i + Math.Sign(dir);
                        if (i < 0 || j < 0 || j >= s.Cues.Count) return;
                        (s.Cues[i], s.Cues[j]) = (s.Cues[j], s.Cues[i]);
                    });
                    break;
                case "settings": ApplySettings(m); break;
                case "monitor":
                    _store.Change(s => s.Settings.MonitorDevice = m.GetProperty("device").GetString() ?? "");
                    _engine.ApplyMonitor();
                    break;
                case "fade": ChangeCues(s => s.FadeMs = Fade.Normalize(m.GetProperty("ms").GetInt32())); break;
                case "newList": NewPlaylist(); break;
                case "openList": OpenPlaylistDialog(); break;
                case "saveList": SavePlaylist(false); break;
                case "saveListAs": SavePlaylist(true); break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("message failed: " + ex);
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>A change to the cue list itself: the playlist now differs from its file.</summary>
    private void ChangeCues(Action<Show> change) => _store.Change(s => { change(s); s.Modified = true; });

    // ================= playlists =================

    /// <summary>Asks to save a changed playlist. False = the user cancelled.</summary>
    private bool AskSaveChanges()
    {
        var (modified, count, name) = _store.Read(s => (s.Modified, s.Cues.Count, PlaylistName(s.PlaylistPath)));
        if (!modified || count == 0) return true;
        var r = MessageBox.Show(this, $"Save the changes to \"{name}\"?", AppTitle, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        return r == DialogResult.No || SavePlaylist(false);
    }

    public static string PlaylistName(string? path) => string.IsNullOrEmpty(path) ? "Untitled" : Path.GetFileNameWithoutExtension(path);

    private void NewPlaylist()
    {
        if (!AskSaveChanges()) return;
        _store.Change(s => { s.Cues.Clear(); s.PlaylistPath = null; s.Modified = false; s.FadeMs = Fade.DefaultMs; });
    }

    private void OpenPlaylistDialog()
    {
        if (!AskSaveChanges()) return;
        using var dlg = new OpenFileDialog { Title = "Open playlist", Filter = PlaylistFilter, InitialDirectory = PlaylistFolder() };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenPlaylist(dlg.FileName, asked: true);
    }

    private void OpenPlaylist(string file, bool asked = false)
    {
        if (!asked && !AskSaveChanges()) return;
        var (cues, missing, fadeMs) = Playlist.Load(file);
        _store.Change(s => { s.Cues = cues; s.PlaylistPath = file; s.Modified = false; s.FadeMs = fadeMs; });
        Log.Write($"opened playlist {file} ({cues.Count} cues, {missing} missing)");
        ProbeMissing();
        if (missing > 0)
            MessageBox.Show(this, $"{missing} of {cues.Count} files were not found. They stay in the list and are marked.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private bool SavePlaylist(bool askName)
    {
        var (cues, path, fadeMs) = _store.Read(s => (s.Cues.ToList(), s.PlaylistPath, s.FadeMs));
        if (askName || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save playlist",
                Filter = PlaylistFilter,
                DefaultExt = "cuelist",
                InitialDirectory = PlaylistFolder(),
                FileName = string.IsNullOrEmpty(path) ? "Playlist" : Path.GetFileName(path),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            path = dlg.FileName;
        }
        Playlist.Save(path!, cues, fadeMs);
        _store.Change(s => { s.PlaylistPath = path; s.Modified = false; });
        Log.Write($"saved playlist {path} ({cues.Count} cues)");
        return true;
    }

    private string PlaylistFolder()
    {
        var p = _store.Read(s => s.PlaylistPath);
        var dir = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
        return dir != null && Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // ================= settings / media =================

    private void ApplySettings(JsonElement m)
    {
        var old = _store.Read(s => new ShowSettings { Format = s.Settings.Format, SourceName = s.Settings.SourceName, Ndi = s.Settings.Ndi, Omt = s.Settings.Omt, Sdi = s.Settings.Sdi, SdiPort = s.Settings.SdiPort, MonitorDevice = s.Settings.MonitorDevice, GainDb = s.Settings.GainDb });
        var n = new ShowSettings
        {
            Format = OutputFormat.Find(m.GetProperty("format").GetString()).Id,
            SourceName = (m.GetProperty("sourceName").GetString() ?? "").Trim() is { Length: > 0 } nm ? nm : ShowSettings.DefaultSourceName,
            Ndi = m.GetProperty("ndi").GetBoolean(),
            Omt = m.GetProperty("omt").GetBoolean(),
            Sdi = m.TryGetProperty("sdi", out var sd) && sd.GetBoolean(),
            SdiPort = m.TryGetProperty("sdiPort", out var sp) ? sp.GetString() ?? "" : old.SdiPort,
            MonitorDevice = old.MonitorDevice,   // set on its own ("monitor" message), no Apply needed
            GainDb = Math.Clamp(m.GetProperty("gainDb").GetDouble(), -60, 12),
        };
        _store.Change(s => s.Settings = n);
        _engine.SetGain(n.GainDb);
        bool restart = n.Format != old.Format || n.SourceName != old.SourceName || n.Ndi != old.Ndi || n.Omt != old.Omt || n.Sdi != old.Sdi || n.SdiPort != old.SdiPort;
        if (restart)
        {
            if (IsPlaying() && MessageBox.Show(this, "Changing the output stops the cue that is playing. Continue?", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                _store.Change(s => { s.Settings = old; s.Settings.GainDb = n.GainDb; });
                return;
            }
            _engine.ApplySettings();
        }
    }

    private void AddWithDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add media",
            Multiselect = true,
            Filter = "Media|" + string.Join(";", MediaExtensions.Select(x => "*" + x)) + "|All files|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames, "");
    }

    private void AddPaths(IEnumerable<string> paths, string beforeId)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p).Where(IsMedia).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
            else if (File.Exists(p) && IsMedia(p)) files.Add(p);
        }
        if (files.Count == 0) return;
        var cues = files.Select(f => new Cue { Path = f, Name = Path.GetFileNameWithoutExtension(f) }).ToList();
        ChangeCues(s =>
        {
            int at = s.Cues.FindIndex(c => c.Id == beforeId);
            s.Cues.InsertRange(at < 0 ? s.Cues.Count : at, cues);
        });
        ProbeCues(cues);
    }

    /// <summary>Reads length, audio tracks and video of the given cues in the background (for the list and the audio choices).</summary>
    private void ProbeCues(IEnumerable<Cue> cues)
    {
        foreach (var c in cues.ToList())
        {
            var cue = c;
            if (!File.Exists(cue.Path)) continue;
            _ = Task.Run(async () =>
            {
                try
                {
                    var (ms, tracks, video, rates48) = await _engine.ProbeInfoAsync(cue.Path);
                    _store.Change(s =>
                    {
                        var x = s.Cues.FirstOrDefault(q => q.Id == cue.Id);
                        if (x == null) return;
                        x.DurationMs = x.IsStill ? x.StillSeconds * 1000L : ms;   // libVLC reports 10 s (its default) for pictures
                        x.AudioTracks = tracks;
                        x.HasVideo = video;
                        x.AudioRates48 = rates48;
                        x.Audio = AudioChoice.Normalize(x.Audio, x);
                    });
                    // VMX (vMix recordings): libVLC cannot decode it, libvmx does - no test decode needed
                    bool vmx = video && VmxVideo.IsVmx(cue.Path);
                    _store.Change(s => { var x = s.Cues.FirstOrDefault(q => q.Id == cue.Id); if (x != null) x.Vmx = vmx; });
                    string thumb = Thumbs.NameFor(cue.Path);
                    if (vmx)
                    {
                        Log.Write($"VMX picture (libvmx): {cue.Path}");
                        bool t = VmxVideo.Thumb(cue.Path, thumb);
                        _store.Change(s => { var x = s.Cues.FirstOrDefault(q => q.Id == cue.Id); if (x != null) { x.Unsupported = null; x.DecodeChecked = true; x.Thumb = t ? thumb : null; } });
                    }
                    // can libVLC decode the picture? - a short test decode on the small check player; its first picture is the thumbnail
                    else if (video)
                    {
                        var reason = await _engine.CheckDecodableAsync(cue.Path, thumb);
                        if (reason != null) Log.Write($"not supported: {cue.Path}: {reason}");
                        bool t = reason == null && Thumbs.Exists(thumb);
                        _store.Change(s => { var x = s.Cues.FirstOrDefault(q => q.Id == cue.Id); if (x != null) { x.Unsupported = reason; x.DecodeChecked = true; x.Thumb = t ? thumb : null; } });
                    }
                    else _store.Change(s => { var x = s.Cues.FirstOrDefault(q => q.Id == cue.Id); if (x != null) { x.Unsupported = null; x.DecodeChecked = true; } });
                }
                catch (Exception ex) { Log.Write("probe failed: " + cue.Path + ": " + ex.Message); }
            });
        }
    }

    /// <summary>Cues from before the audio choices / the decode check (or from an old playlist): read them once.</summary>
    private void ProbeMissing()
    {
        // also cues without a thumbnail yet (or whose thumbnail file is gone / belongs to an older version of the file)
        ProbeCues(_store.Read(s => s.Cues.Where(c => c.AudioTracks == null || c.HasVideo == null || !c.DecodeChecked || c.AudioRates48 == null
            || (c.HasVideo == true && c.Vmx == null)
            || (c.HasVideo == true && c.Unsupported == null && File.Exists(c.Path) && (!Thumbs.Exists(c.Thumb) || c.Thumb != Thumbs.NameFor(c.Path)))).ToList()));
        Thumbs.Clean(_store.Read(s => s.Cues.Select(c => c.Thumb).ToList()));
    }

    // ================= files that cannot be played =================

    private readonly HashSet<string> _askedUnsupported = [];
    private bool _asking;

    /// <summary>
    /// A file whose picture libVLC cannot decode (found by the check when it was added): ask once whether to remove it (Peter).
    /// Kept, it plays its sound only with a black picture.
    /// </summary>
    private void AskUnsupported()
    {
        if (_asking) return;
        var bad = _store.Read(s => s.Cues.Where(c => c.Unsupported != null && !_askedUnsupported.Contains(c.Id)).Select(c => (c.Id, c.Name, c.Unsupported)).ToList());
        if (bad.Count == 0) return;
        _asking = true;
        try
        {
            foreach (var (id, name, reason) in bad)
            {
                _askedUnsupported.Add(id);
                var r = MessageBox.Show(this,
                    $"\"{name}\": this file format is not supported.\n\n{reason}\n\nRemove it from the list?\n(If you keep it, it plays its sound only, with a black picture.)",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) ChangeCues(s => s.Cues.RemoveAll(c => c.Id == id));
            }
        }
        finally { _asking = false; }
    }

    private static bool IsMedia(string p) => MediaExtensions.Contains(Path.GetExtension(p).ToLowerInvariant());
}
