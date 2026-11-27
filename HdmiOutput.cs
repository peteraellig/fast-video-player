using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace CuePlayer;

/// <summary>
/// HDMI / monitor output (Peter 2026-11-27): the output picture full screen on a second monitor. Not a WebView (its own frame timing
/// gives judder): a borderless window with a Direct3D 11 flip-model swap chain on the GPU that drives that monitor. The output thread
/// hands over each UYVY picture; a render thread shows the next one at every vertical blank (latency waitable, 1 frame), a pixel shader
/// turns UYVY (BT.709 limited) into RGB. The monitor is switched to the output's frame rate while in use (50 Hz for 50p/25p/1080i50),
/// and back when it is closed.
/// </summary>
internal sealed unsafe class HdmiOutput : IDisposable
{
    public sealed record ScreenInfo(string Id, string Name, System.Drawing.Rectangle Bounds);

    /// <summary>All monitors except the one given (the control window's).</summary>
    public static List<ScreenInfo> Screens(string? exceptDevice)
    {
        var list = new List<ScreenInfo>();
        foreach (var s in Screen.AllScreens)
        {
            if (string.Equals(s.DeviceName, exceptDevice, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new ScreenInfo(s.DeviceName, $"{MonitorName(s.DeviceName)} ({s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? ", main" : "")})", s.Bounds));
        }
        return list;
    }

    private readonly OutputFormat _fmt;
    private readonly int _bytes;
    private readonly string _device;
    private readonly System.Drawing.Rectangle _bounds;
    private readonly Thread _uiThread, _renderThread;
    private readonly ManualResetEventSlim _windowReady = new();
    private Form? _form;
    private IntPtr _hwnd;
    private volatile bool _stop;
    private bool _modeChanged;

    // picture queue: the output thread fills, the render thread shows one per vertical blank (guarded by _sync)
    private readonly object _sync = new();
    private readonly Queue<IntPtr> _queue = new();
    private readonly Stack<IntPtr> _free = new();
    private IntPtr _showing = IntPtr.Zero;
    private const int Buffers = 5, MaxQueued = 2;

    public string Name { get; }
    public string Status { get; private set; } = "starting";
    public int Presented;
    /// <summary>Test helper: set true, the render thread reads back the centre pixel of what it drew (BGRA) into <see cref="CenterPixel"/>.</summary>
    public volatile bool GrabCenter;
    public volatile string CenterPixel = "";
    public int RefreshHz { get; private set; }
    public int Repeated, Dropped;

    public HdmiOutput(string device, OutputFormat fmt)
    {
        var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException("monitor not found");
        _device = screen.DeviceName;
        _bounds = screen.Bounds;
        _fmt = fmt;
        _bytes = fmt.Width * fmt.Height * 2;
        Name = MonitorName(_device);
        for (int i = 0; i < Buffers; i++) _free.Push(Marshal.AllocHGlobal(_bytes));

        // the monitor runs at the output's frame rate if it can (60 Hz with 50p output judders)
        _modeChanged = SetRefresh(_device, TargetHz(fmt), out var hz);
        RefreshHz = hz;

        _uiThread = new Thread(WindowThread) { IsBackground = true, Name = "hdmi window" };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        if (!_windowReady.Wait(5000)) throw new InvalidOperationException("window did not open");
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "hdmi render", Priority = ThreadPriority.AboveNormal };
        _renderThread.Start();
    }

    /// <summary>50p, 25p and 1080i50 (25 woven frames) want 50 Hz; 59.94/60 want 60 Hz.</summary>
    private static int TargetHz(OutputFormat f)
    {
        double rate = f.RateN / (double)f.RateD;
        double field = f.Interlaced || rate < 40 ? rate * 2 : rate;
        return (int)Math.Round(field);
    }

    // ================= window =================

    private sealed class OutputForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW: not in the taskbar */ | 0x00000008 /* WS_EX_TOPMOST */;
                return cp;
            }
        }
    }

    private void WindowThread()
    {
        _form = new OutputForm
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = _bounds,
            BackColor = System.Drawing.Color.Black,
            ShowInTaskbar = false,
            TopMost = true,
            Text = "fast video player - HDMI",
        };
        _form.MouseEnter += (_, _) => Cursor.Hide();
        _form.MouseLeave += (_, _) => Cursor.Show();
        _form.Shown += (_, _) => { _hwnd = _form.Handle; _windowReady.Set(); };
        Application.Run(_form);
    }

    // ================= output thread =================

    /// <summary>Output thread: one picture (UYVY at the output size). Copied; the oldest waiting one is dropped when the queue is full.</summary>
    public void Send(IntPtr pic)
    {
        IntPtr buf;
        lock (_sync)
        {
            if (_free.Count > 0) buf = _free.Pop();
            else if (_queue.Count > 0) { buf = _queue.Dequeue(); Dropped++; }
            else return;
        }
        Buffer.MemoryCopy((void*)pic, (void*)buf, _bytes, _bytes);
        lock (_sync)
        {
            _queue.Enqueue(buf);
            while (_queue.Count > MaxQueued) { _free.Push(_queue.Dequeue()); Dropped++; }
        }
    }

    // ================= render thread =================

    private const string Shader = @"
Texture2D<float4> tex : register(t0);
cbuffer C : register(b0) { float2 size; float2 pad; };
struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
V VS(uint id : SV_VertexID)
{
    V o;
    float2 t = float2((id << 1) & 2, id & 2);
    o.uv = t;
    o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 PS(V i) : SV_TARGET
{
    int px = min((int)(i.uv.x * size.x), (int)size.x - 1);
    int py = min((int)(i.uv.y * size.y), (int)size.y - 1);
    float4 t = tex.Load(int3(px >> 1, py, 0));      // R = U, G = Y0, B = V, A = Y1
    float y = (px & 1) ? t.a : t.g;
    float Y = (y * 255.0 - 16.0) / 219.0;
    float Cb = (t.r * 255.0 - 128.0) / 224.0;
    float Cr = (t.b * 255.0 - 128.0) / 224.0;
    float3 rgb = float3(Y + 1.5748 * Cr, Y - 0.1873 * Cb - 0.4681 * Cr, Y + 1.8556 * Cb);   // BT.709
    return float4(saturate(rgb), 1);
}";

    private void RenderLoop()
    {
        IDXGIFactory2? factory = null;
        IDXGIAdapter1? adapter = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? ctx = null;
        IDXGISwapChain1? swap = null;
        ID3D11RenderTargetView? rtv = null;
        ID3D11Texture2D? tex = null;
        ID3D11ShaderResourceView? srv = null;
        ID3D11VertexShader? vs = null;
        ID3D11PixelShader? ps = null;
        ID3D11Buffer? cb = null;
        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            adapter = AdapterFor(factory, _device);   // the GPU that drives this monitor (laptop: the NVIDIA for the HDMI socket)
            D3D11.D3D11CreateDevice(adapter, adapter != null ? DriverType.Unknown : DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out device, out ctx).CheckError();

            int w = _bounds.Width, h = _bounds.Height;
            var desc = new SwapChainDescription1
            {
                Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            swap = factory.CreateSwapChainForHwnd(device!, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            IntPtr waitable;
            using (var swap2 = swap.QueryInterface<IDXGISwapChain2>())
            {
                swap2.MaximumFrameLatency = 1;
                waitable = swap2.FrameLatencyWaitableObject;
            }
            using (var back = swap.GetBuffer<ID3D11Texture2D>(0)) rtv = device!.CreateRenderTargetView(back);

            // the UYVY picture as a texture of width/2 RGBA texels (U Y0 V Y1)
            tex = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)(_fmt.Width / 2), Height = (uint)_fmt.Height, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            srv = device.CreateShaderResourceView(tex);
            vs = device.CreateVertexShader(Compile(Shader, "VS", "vs_5_0"));
            ps = device.CreatePixelShader(Compile(Shader, "PS", "ps_5_0"));
            float* consts = stackalloc float[4] { _fmt.Width, _fmt.Height, 0, 0 };
            cb = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer), new SubresourceData((IntPtr)consts));

            // the picture keeps its aspect ratio on the monitor (black bars when the monitor is not 16:9)
            float scale = Math.Min(w / (float)_fmt.Width, h / (float)_fmt.Height);
            float vw = _fmt.Width * scale, vh = _fmt.Height * scale;
            var viewport = new Viewport((w - vw) / 2, (h - vh) / 2, vw, vh);

            Status = RefreshHz > 0 ? $"on, {RefreshHz} Hz" : "on";
            Log.Write($"HDMI {Name} {w}x{h} at {RefreshHz} Hz (target {TargetHz(_fmt)}), GPU {adapter?.Description1.Description ?? "default"}, {LastConnection}");
            bool haveTexture = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double waitSum = 0, presentSum = 0, waitMax = 0, presentMax = 0; int n = 0; long lastLog = 0;
            while (!_stop)
            {
                double t0 = sw.Elapsed.TotalMilliseconds;
                uint wr = WaitForSingleObjectEx(waitable, 1000, true);   // next vertical blank
                double tw = sw.Elapsed.TotalMilliseconds - t0;
                waitSum += tw; waitMax = Math.Max(waitMax, tw);
                if (wr != 0) Log.Write($"HDMI wait returned {wr:X} after {tw:0} ms");
                IntPtr next = IntPtr.Zero;
                lock (_sync)
                {
                    if (_queue.Count > 0)
                    {
                        next = _queue.Dequeue();
                        if (_showing != IntPtr.Zero) _free.Push(_showing);
                        _showing = next;
                    }
                    else Repeated++;
                }
                if (next != IntPtr.Zero)
                {
                    ctx!.UpdateSubresource(tex, 0, null, next, (uint)(_fmt.Width * 2), 0);
                    haveTexture = true;
                }
                ctx!.OMSetRenderTargets(rtv);
                ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
                if (haveTexture)
                {
                    ctx.RSSetViewport(viewport);
                    ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    ctx.VSSetShader(vs);
                    ctx.PSSetShader(ps);
                    ctx.PSSetShaderResource(0, srv);
                    ctx.PSSetConstantBuffer(0, cb);
                    ctx.Draw(3, 0);
                }
                if (GrabCenter)
                {
                    GrabCenter = false;
                    using var back = swap.GetBuffer<ID3D11Texture2D>(0);
                    var bd = back.Description;
                    using var staging = device!.CreateTexture2D(new Texture2DDescription
                    {
                        Width = bd.Width, Height = bd.Height, MipLevels = 1, ArraySize = 1, Format = bd.Format, SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                    });
                    ctx.CopyResource(staging, back);
                    var map = ctx.Map(staging, 0, MapMode.Read);
                    byte* px = (byte*)map.DataPointer + (bd.Height / 2) * map.RowPitch + (bd.Width / 2) * 4;
                    CenterPixel = $"{bd.Width}x{bd.Height} centre B{px[0]} G{px[1]} R{px[2]}";
                    ctx.Unmap(staging, 0);
                }
                double tp0 = sw.Elapsed.TotalMilliseconds;
                var r = swap.Present(1, PresentFlags.None);
                double tp = sw.Elapsed.TotalMilliseconds - tp0;
                presentSum += tp; presentMax = Math.Max(presentMax, tp); n++;
                Presented++;
                if (sw.ElapsedMilliseconds - lastLog > 2000 && sw.ElapsedMilliseconds < 12000)
                {
                    // the first seconds in the log: one wait per refresh (16.5 ms at 60 Hz) = smooth; hundreds of ms = something holds the window
                    Log.Write($"HDMI {n} frames: wait avg {waitSum / n:0.0} max {waitMax:0.0} ms, present avg {presentSum / n:0.0} max {presentMax:0.0} ms");
                    lastLog = sw.ElapsedMilliseconds; waitSum = presentSum = waitMax = presentMax = 0; n = 0;
                }
                if (r.Failure) { Status = "display lost"; Log.Write("HDMI present failed: " + r); break; }
            }
        }
        catch (Exception e)
        {
            Status = "failed: " + e.Message;
            Log.Write("HDMI output failed: " + e);
        }
        finally
        {
            cb?.Dispose(); ps?.Dispose(); vs?.Dispose(); srv?.Dispose(); tex?.Dispose(); rtv?.Dispose();
            swap?.Dispose(); ctx?.Dispose(); device?.Dispose(); adapter?.Dispose(); factory?.Dispose();
        }
    }

    private static IDXGIAdapter1? AdapterFor(IDXGIFactory2 factory, string device)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                bool match = string.Equals(output.Description.DeviceName, device, StringComparison.OrdinalIgnoreCase);
                output.Dispose();
                if (match) return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }

    // ================= shader compiler (d3dcompiler_47.dll is part of Windows) =================

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, nint len, string? name, IntPtr defines, IntPtr include, string entry, string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    private static byte[] Compile(string src, string entry, string target)
    {
        var bytes = Encoding.ASCII.GetBytes(src);
        int hr = D3DCompile(bytes, bytes.Length, "hdmi", IntPtr.Zero, IntPtr.Zero, entry, target, 0, 0, out var code, out var errors);
        try
        {
            if (hr < 0) throw new InvalidOperationException("shader: " + (errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(errors)) : hr.ToString("X")));
            var r = new byte[(int)BlobSize(code)];
            Marshal.Copy(BlobPointer(code), r, 0, r.Length);
            return r;
        }
        finally
        {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }

    // ID3DBlob: IUnknown (3 methods), then GetBufferPointer, GetBufferSize
    private static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(*(void***)blob + 3)))(blob);
    private static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)(*(*(void***)blob + 4)))(blob);

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObjectEx(IntPtr handle, uint ms, bool alertable);


    // ================= monitor name and refresh rate =================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);

    private const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;
    private const uint CDS_FULLSCREEN = 4;   // temporary: Windows restores the mode when the program ends

    private static string MonitorName(string device)
    {
        string id = device.Replace(@"\\.\", "");
        string? friendly = FriendlyName(device);
        if (friendly == null)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(device, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString) && !dd.DeviceString.StartsWith("Generic", StringComparison.OrdinalIgnoreCase))
                friendly = dd.DeviceString;
        }
        return friendly != null ? $"{friendly} - {id}" : id;
    }

    // ---- the monitor's own name (EDID), the way Windows' display settings show it: QueryDisplayConfig + DisplayConfigGetDeviceInfo ----

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling;
        public uint refreshNum, refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)] private struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME { public DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PATH_INFO[] p, ref uint modes, [Out] MODE_INFO[] m, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME r);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME r);

    public static string LastConnection = "";

    private static string? FriendlyName(string device)
    {
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return null;
            var paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;
            for (int i = 0; i < np; i++)
            {
                var src = new SOURCE_DEVICE_NAME { header = { type = 1, size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(), adapterId = paths[i].sourceInfo.adapterId, id = paths[i].sourceInfo.id } };
                if (DisplayConfigGetDeviceInfo(ref src) != 0 || !string.Equals(src.viewGdiDeviceName, device, StringComparison.OrdinalIgnoreCase)) continue;
                var tgt = new TARGET_DEVICE_NAME { header = { type = 2, size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>(), adapterId = paths[i].targetInfo.adapterId, id = paths[i].targetInfo.id } };
                if (DisplayConfigGetDeviceInfo(ref tgt) == 0 && !string.IsNullOrWhiteSpace(tgt.monitorFriendlyDeviceName))
                {
                    LastConnection = $"output technology {tgt.outputTechnology}, path refresh {paths[i].targetInfo.refreshNum}/{paths[i].targetInfo.refreshDen}";
                    return tgt.monitorFriendlyDeviceName;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Switches the monitor to <paramref name="hz"/> at its current size, if it offers that. True = changed (restore later).</summary>
    private static bool SetRefresh(string device, int hz, out int now)
    {
        var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        now = EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref cur) ? cur.dmDisplayFrequency : 0;
        if (now == hz || now == 0) return false;
        bool offered = false;
        var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(device, i, ref m); i++)
            if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmDisplayFrequency == hz) { offered = true; break; }
        if (!offered) { Log.Write($"HDMI {device}: {hz} Hz not offered at {cur.dmPelsWidth}x{cur.dmPelsHeight}, stays at {now} Hz"); return false; }
        var want = cur;
        want.dmFields = DM_DISPLAYFREQUENCY | DM_PELSWIDTH | DM_PELSHEIGHT;
        want.dmDisplayFrequency = hz;
        int r = ChangeDisplaySettingsEx(device, ref want, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        if (r != 0) { Log.Write($"HDMI {device}: switching to {hz} Hz failed ({r})"); return false; }
        now = hz;
        return true;
    }

    public void Dispose()
    {
        _stop = true;
        _renderThread?.Join(2000);
        try { _form?.BeginInvoke(() => _form.Close()); } catch { }
        _uiThread?.Join(2000);
        if (_modeChanged) ChangeDisplaySettingsEx(_device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);   // back to the monitor's own mode
        lock (_sync)
        {
            foreach (var b in _queue) Marshal.FreeHGlobal(b);
            foreach (var b in _free) Marshal.FreeHGlobal(b);
            if (_showing != IntPtr.Zero) Marshal.FreeHGlobal(_showing);
            _queue.Clear(); _free.Clear(); _showing = IntPtr.Zero;
        }
    }
}
