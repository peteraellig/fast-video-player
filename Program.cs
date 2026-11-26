using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace CuePlayer;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(ShowStore.Folder);

        // console modes for testing: --probe ADDRESS [seconds] [omt|ndi], --test FILE [seconds] [format]
        if (args.Length > 0 && (args[0] == "--probe" || args[0] == "--test" || args[0] == "--duration" || args[0] == "--sdi-in" || args[0] == "--vmx"))
        {
            AttachConsole(-1);
            Log.ToConsole = true;
            Console.WriteLine();
            if (args[0] == "--duration")
            {
                Core.Initialize();
                using var e = new PlayerEngine(new ShowStore(Path.Combine(Path.GetTempPath(), "cueplayer_test_show.json")));
                for (int i = 1; i < args.Length; i++)
                {
                    Console.WriteLine($"{args[i]}: {e.ProbeDurationAsync(args[i]).GetAwaiter().GetResult()} ms; {e.ProbeTracksAsync(args[i]).GetAwaiter().GetResult()}");
                    Console.WriteLine($"  decode check: {e.CheckDecodableAsync(args[i]).GetAwaiter().GetResult() ?? "picture OK"}");
                }
                Thread.Sleep(3000);
                Console.WriteLine("still alive");
                return 0;
            }
            if (args[0] == "--vmx") return VmxProbe.Run(args);
            if (args[0] == "--sdi-in")
            {
                // DeckLink calls back on its own threads: run it in the MTA (Main is STA and has no message loop here)
                int rc = 1;
                var t = new Thread(() => rc = SdiProbe.Run(args));
                t.SetApartmentState(ApartmentState.MTA);
                t.Start(); t.Join();
                return rc;
            }
            return args[0] == "--probe" ? Probe.Run(args) : TestPlay(args);
        }

        // only one CuePlayer per PC: two would fight over the NDI name (NDI refuses a second sender with the same name; seen 2026-11-25)
        using var single = new Mutex(true, @"Local\CuePlayer.SingleInstance", out bool first);
        if (!first)
        {
            var other = System.Diagnostics.Process.GetProcessesByName("CuePlayer")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other != null)
            {
                ShowWindow(other.MainWindowHandle, 9);   // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
            }
            return 0;
        }

        Log.Write($"CuePlayer {typeof(Program).Assembly.GetName().Version} start");
        Core.Initialize();
        ApplicationConfiguration.Initialize();
        var store = new ShowStore();
        using var engine = new PlayerEngine(store);
        Application.Run(new MainForm(engine, store));
        Log.Write("exit");
        return 0;
    }

    /// <summary>Plays one file without a window (own show file in the temp folder), prints the output state every second.</summary>
    private static int TestPlay(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1])) { Console.WriteLine("usage: CuePlayer --test FILE [seconds] [format] [endaction] [FILE2] [fadeSeconds]"); return 1; }
        int seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 10;
        Core.Initialize();
        var store = new ShowStore(Path.Combine(Path.GetTempPath(), "cueplayer_test_show.json"));
        store.Change(sh =>
        {
            sh.Cues.Clear();
            sh.Settings.SourceName = "CuePlayer Test";
            if (args.Length > 3) sh.Settings.Format = OutputFormat.Find(args[3]).Id;
            // CUEPLAYER_TEST_SDI=1 (or a port name): also play out on the DeckLink / UltraStudio port
            var sdi = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_SDI");
            sh.Settings.Sdi = !string.IsNullOrEmpty(sdi);
            sh.Settings.SdiPort = sdi is null or "1" ? "" : sdi;
            // CUEPLAYER_TEST_MONITOR=1: listen on the Windows default playback device
            sh.Settings.MonitorDevice = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_MONITOR") == "1" ? AudioMonitor.Devices().FirstOrDefault().id ?? "" : "";
            sh.Cues.Add(new Cue { Path = args[1], Name = Path.GetFileName(args[1]), EndAction = args.Length > 4 ? EndAction.Normalize(args[4]) : EndAction.Hold });
            // optional second file: tests "next" / "fade" transitions without a window
            if (args.Length > 5) sh.Cues.Add(new Cue { Path = args[5], Name = Path.GetFileName(args[5]), EndAction = EndAction.Hold });
            if (args.Length > 6 && double.TryParse(args[6], System.Globalization.CultureInfo.InvariantCulture, out var fade)) sh.FadeMs = Fade.Normalize((int)(fade * 1000));
        });
        using var engine = new PlayerEngine(store);
        // read tracks like the window does when files are added; CUEPLAYER_TEST_AUDIO=choice sets the first cue's audio choice
        foreach (var c in store.Read(sh => sh.Cues.ToList()))
        {
            var (ms, tracks, video, rates48) = engine.ProbeInfoAsync(c.Path).GetAwaiter().GetResult();
            var audio = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_AUDIO");
            store.Change(sh =>
            {
                var x = sh.Cues.First(q => q.Id == c.Id);
                x.DurationMs = ms; x.AudioTracks = tracks; x.HasVideo = video; x.AudioRates48 = rates48;
                x.Vmx = video && VmxVideo.IsVmx(x.Path);
                x.Audio = AudioChoice.Normalize(x == sh.Cues[0] && audio != null ? audio : x.Audio, x);
            });
            var cc = store.Read(sh => sh.Cues.First(q => q.Id == c.Id));
            Console.WriteLine($"{cc.Name}: tracks [{string.Join(",", tracks)}] video {video}, ffmpeg audio {cc.UsesFfmpegAudio} ({FfmpegAudio.Status}), audio {cc.Audio}, options {string.Join(" ", AudioChoice.Options(cc).Select(o => o.value))}");
        }
        Thread.Sleep(1500);
        // CUEPLAYER_TEST_FLIP=N[:ms]: stopped, choose cue 1 / cue 2 alternately N times (every ms, default 900) and check that the picture on the
        // outputs is the chosen cue's first picture (reference = the signature seen the first time each cue was cued)
        var flip = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_FLIP")?.Split(':');
        if (flip != null && int.TryParse(flip[0], out var flips))
        {
            int ms = flip.Length > 1 && int.TryParse(flip[1], out var fm) ? fm : 900;
            var ids = store.Read(sh => sh.Cues.Select(q => q.Id).ToList());
            var refSig = new Dictionary<string, long>();
            int bad = 0;
            for (int i = 0; i < flips; i++)
            {
                var id = ids[i % 2];
                engine.Select(id);
                var swFlip = System.Diagnostics.Stopwatch.StartNew();
                string? cued = null; long readyMs = -1;
                while (swFlip.ElapsedMilliseconds < ms)
                {
                    Thread.Sleep(10);
                    var j0 = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(engine.Snapshot())).RootElement;
                    cued = j0.GetProperty("cuedId").ValueKind == System.Text.Json.JsonValueKind.String ? j0.GetProperty("cuedId").GetString() : null;
                    if (cued == id && readyMs < 0) readyMs = swFlip.ElapsedMilliseconds;
                }
                long sig = engine.CuedSignature();
                if (cued != null && i < 2 && cued == id) refSig[id] = sig;
                // the picture must belong to the cue marked CUED; a cue still loading keeps the previous one (that is fine)
                bool match = cued != null && refSig.TryGetValue(cued, out var want) && want == sig;
                if (!match) bad++;
                Console.WriteLine($"{i + 1,3} chose {(i % 2) + 1}: cued={(cued == ids[0] ? "1" : cued == ids[1] ? "2" : "-")} sig={sig:X6} " +
                    $"{(!match ? "WRONG PICTURE" : cued == id ? $"ok, ready after {readyMs} ms" : "still loading (previous picture stays)")}");
            }
            Console.WriteLine($"flip test: {bad} wrong of {flips}");
            return bad == 0 ? 0 : 2;
        }
        // CUEPLAYER_TEST_NOGO=1: stay stopped (the preview must show the first frame of the NEXT cue, the output must stay black)
        if (Environment.GetEnvironmentVariable("CUEPLAYER_TEST_NOGO") != "1") engine.Go();
        else engine.Select(store.Read(sh => sh.Cues[0].Id));   // like a click on the first cue
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var j = System.Text.Json.JsonSerializer.Serialize(engine.Snapshot());
            var d = System.Text.Json.JsonDocument.Parse(j).RootElement;
            if (i == 2) SavePreview(engine, Path.Combine(Path.GetTempPath(), "cueplayer_test_preview.png"));
            // CUEPLAYER_TEST_SETEND=N:action changes the end action of the first cue after N seconds (like the dropdown while it plays)
            var setEnd = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_SETEND")?.Split(':');
            if (setEnd is { Length: 2 } && int.TryParse(setEnd[0], out var se) && i + 1 == se)
            {
                store.Change(sh => sh.Cues[0].EndAction = EndAction.Normalize(setEnd[1]));
                Console.WriteLine($"end action of the first cue -> {setEnd[1]}");
            }
            // CUEPLAYER_TEST_SETAUDIO=N:choice changes the first cue's audio choice after N seconds (like the dropdown while it plays)
            var setAudio = Environment.GetEnvironmentVariable("CUEPLAYER_TEST_SETAUDIO")?.Split(':');
            if (setAudio is { Length: 2 } && int.TryParse(setAudio[0], out var sa) && i + 1 == sa)
            {
                store.Change(sh => sh.Cues[0].Audio = AudioChoice.Normalize(setAudio[1], sh.Cues[0]));
                Console.WriteLine($"audio of the first cue -> {store.Read(sh => sh.Cues[0].Audio)}");
            }
            // CUEPLAYER_TEST_SELECT2=N clicks the second cue after N seconds
            if (int.TryParse(Environment.GetEnvironmentVariable("CUEPLAYER_TEST_SELECT2"), out var s2) && i + 1 == s2) engine.Select(store.Read(sh => sh.Cues[1].Id));
            // CUEPLAYER_TEST_SELECT1=N clicks the first cue after N seconds
            if (int.TryParse(Environment.GetEnvironmentVariable("CUEPLAYER_TEST_SELECT1"), out var s1) && i + 1 == s1) engine.Select(store.Read(sh => sh.Cues[0].Id));
            // CUEPLAYER_TEST_STOP=N presses Stop after N seconds
            if (int.TryParse(Environment.GetEnvironmentVariable("CUEPLAYER_TEST_STOP"), out var st) && i + 1 == st) engine.Stop();
            // CUEPLAYER_TEST_LAST10=N presses "Last 10 s" after N seconds
            if (int.TryParse(Environment.GetEnvironmentVariable("CUEPLAYER_TEST_LAST10"), out var l10) && i + 1 == l10) engine.LastTen();
            var cuedNow = d.GetProperty("cuedId").ValueKind == System.Text.Json.JsonValueKind.String ? d.GetProperty("cuedId").GetString() : null;
            var cueIds = store.Read(sh => sh.Cues.Select(q => q.Id).ToList());
            if (Environment.GetEnvironmentVariable("CUEPLAYER_TEST_METER") == "1")
                Console.WriteLine("     ffmpeg sound: " + engine.AudioStats() + " | meter dBFS per file channel: " + string.Join(" ", d.GetProperty("meter").EnumerateArray().Select((v, k) => $"{k + 1}:{(v.GetDouble() > 0 ? 20 * Math.Log10(v.GetDouble()) : -99):0}")));
            Console.WriteLine($"{i + 1,3}s cued={(cuedNow == null ? "-" : (cueIds.IndexOf(cuedNow) + 1).ToString())} cuedSig={engine.CuedSignature():X6} state={d.GetProperty("state")} pos={d.GetProperty("positionMs")} len={d.GetProperty("lengthMs")} sent={d.GetProperty("fps")} decoded={d.GetProperty("decodedFps")} skipped={d.GetProperty("skipped")} omt={d.GetProperty("omt")} ndi={d.GetProperty("ndi")} sdi={d.GetProperty("sdi")} monitor={d.GetProperty("monitor").GetProperty("status")}");
        }
        return 0;
    }

    private static void SavePreview(PlayerEngine engine, string file)
    {
        var (rgba, _, w, h) = engine.Preview();
        var bgra = (byte[])rgba.Clone();
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        bmp.UnlockBits(d);
        bmp.Save(file);
        Console.WriteLine("preview saved: " + file);
    }
}
