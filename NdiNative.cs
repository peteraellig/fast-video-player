using System.Runtime.InteropServices;

namespace CuePlayer;

/// <summary>
/// NDI send side through the installed NDI runtime (NDI Tools or NDI Runtime from ndi.video; the DLL is not shipped, like LiveIn).
/// Loaded at run time: without NDI installed, the NDI output is simply unavailable. Struct layouts follow Processing.NDI.Lib.h.
/// NDI® is a registered trademark of Vizrt NDI AB.
/// </summary>
internal static unsafe class NdiNative
{
    public const uint FourCcUyvy = 0x59565955;
    public const int FormatInterleaved = 0, FormatProgressive = 1;
    public const long TimecodeSynthesize = long.MaxValue;
    public const int FrameVideo = 1, FrameAudio = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate { public IntPtr Name; public IntPtr Groups; public byte ClockVideo; public byte ClockAudio; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Source { public IntPtr Name; public IntPtr Url; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvSettings { public Source Source; public int ColorFormat; public int Bandwidth; public byte AllowFields; public IntPtr Name; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrame
    {
        public int Width, Height;
        public uint FourCc;
        public int RateN, RateD;
        public float Aspect;
        public int FrameFormat;
        public long Timecode;
        public IntPtr Data;
        public int Stride;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrame
    {
        public int SampleRate, Channels, Samples;
        public long Timecode;
        public IntPtr Data;          // planar float32
        public int ChannelStride;    // bytes from one channel to the next
        public IntPtr Metadata;
        public long Timestamp;
    }

    private static delegate* unmanaged[Cdecl]<byte> _initialize;
    private static delegate* unmanaged[Cdecl]<SendCreate*, IntPtr> _sendCreate;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _sendDestroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void> _sendVideo;
    private static delegate* unmanaged[Cdecl]<IntPtr, AudioFrame*, void> _sendAudio;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint, int> _sendConnections;
    private static delegate* unmanaged[Cdecl]<RecvSettings*, IntPtr> _recvCreate;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _recvDestroy;
    private static delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, AudioFrame*, IntPtr, uint, int> _capture;
    private static delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void> _freeVideo;
    private static delegate* unmanaged[Cdecl]<IntPtr, AudioFrame*, void> _freeAudio;

    public static bool Available { get; private set; }
    public static string Status { get; private set; } = "not loaded";
    private static bool _tried;

    public static bool Load()
    {
        if (_tried) return Available;
        _tried = true;
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6") ?? "", "Processing.NDI.Lib.x64.dll"),
            Path.Combine(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5") ?? "", "Processing.NDI.Lib.x64.dll"),
            @"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll",
            @"C:\Program Files\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll",
        };
        string? path = candidates.FirstOrDefault(p => p.Length > 30 && File.Exists(p));
        if (path == null) { Status = "NDI runtime not installed (NDI Tools from ndi.video)"; return false; }
        try
        {
            var lib = NativeLibrary.Load(path);
            _initialize = (delegate* unmanaged[Cdecl]<byte>)NativeLibrary.GetExport(lib, "NDIlib_initialize");
            _sendCreate = (delegate* unmanaged[Cdecl]<SendCreate*, IntPtr>)NativeLibrary.GetExport(lib, "NDIlib_send_create");
            _sendDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "NDIlib_send_destroy");
            _sendVideo = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void>)NativeLibrary.GetExport(lib, "NDIlib_send_send_video_v2");
            _sendAudio = (delegate* unmanaged[Cdecl]<IntPtr, AudioFrame*, void>)NativeLibrary.GetExport(lib, "NDIlib_send_send_audio_v2");
            _sendConnections = (delegate* unmanaged[Cdecl]<IntPtr, uint, int>)NativeLibrary.GetExport(lib, "NDIlib_send_get_no_connections");
            _recvCreate = (delegate* unmanaged[Cdecl]<RecvSettings*, IntPtr>)NativeLibrary.GetExport(lib, "NDIlib_recv_create_v3");
            _recvDestroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(lib, "NDIlib_recv_destroy");
            _capture = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, AudioFrame*, IntPtr, uint, int>)NativeLibrary.GetExport(lib, "NDIlib_recv_capture_v2");
            _freeVideo = (delegate* unmanaged[Cdecl]<IntPtr, VideoFrame*, void>)NativeLibrary.GetExport(lib, "NDIlib_recv_free_video_v2");
            _freeAudio = (delegate* unmanaged[Cdecl]<IntPtr, AudioFrame*, void>)NativeLibrary.GetExport(lib, "NDIlib_recv_free_audio_v2");
            if (_initialize() == 0) { Status = "NDI initialization failed (CPU not supported?)"; return false; }
            Available = true;
            Status = "NDI runtime: " + path;
            return true;
        }
        catch (Exception e)
        {
            Status = "NDI runtime could not be loaded: " + e.Message;
            return false;
        }
    }

    public static IntPtr SendCreateNamed(string name)
    {
        IntPtr n = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            // no clocking by NDI: the output loop paces video and audio itself
            var s = new SendCreate { Name = n, Groups = IntPtr.Zero, ClockVideo = 0, ClockAudio = 0 };
            return _sendCreate(&s);
        }
        finally { Marshal.FreeCoTaskMem(n); }
    }

    public static void SendDestroy(IntPtr s) { if (s != IntPtr.Zero) _sendDestroy(s); }
    public static void SendVideo(IntPtr s, VideoFrame* f) => _sendVideo(s, f);
    public static void SendAudio(IntPtr s, AudioFrame* f) => _sendAudio(s, f);
    public static int Connections(IntPtr s) => _sendConnections(s, 0);

    // receive side, for --probe only
    public static IntPtr ReceiveCreate(string sourceName)
    {
        IntPtr name = Marshal.StringToCoTaskMemUTF8(sourceName);
        try
        {
            var settings = new RecvSettings { Source = new Source { Name = name }, ColorFormat = 1, Bandwidth = 100, AllowFields = 0 };
            return _recvCreate(&settings);
        }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    public static void ReceiveDestroy(IntPtr r) { if (r != IntPtr.Zero) _recvDestroy(r); }
    public static int Capture(IntPtr r, VideoFrame* v, AudioFrame* a, uint timeoutMs) => _capture(r, v, a, IntPtr.Zero, timeoutMs);
    public static void FreeVideo(IntPtr r, VideoFrame* v) => _freeVideo(r, v);
    public static void FreeAudio(IntPtr r, AudioFrame* a) => _freeAudio(r, a);
}

/// <summary>One NDI source sending UYVY video and planar float audio.</summary>
internal sealed unsafe class NdiSender : IDisposable
{
    private readonly IntPtr _handle;

    public NdiSender(string name)
    {
        _handle = NdiNative.SendCreateNamed(name);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("NDIlib_send_create failed");
    }

    public int Connections => NdiNative.Connections(_handle);

    public void SendVideo(IntPtr pixels, OutputFormat f)
    {
        var frame = new NdiNative.VideoFrame
        {
            Width = f.Width,
            Height = f.Height,
            FourCc = NdiNative.FourCcUyvy,
            RateN = f.RateN,
            RateD = f.RateD,
            Aspect = (float)f.Width / f.Height,
            FrameFormat = f.Interlaced ? NdiNative.FormatInterleaved : NdiNative.FormatProgressive,
            Timecode = NdiNative.TimecodeSynthesize,
            Data = pixels,
            Stride = f.Width * 2,
        };
        NdiNative.SendVideo(_handle, &frame);
    }

    public void SendAudio(float[] planar, int channels, int samplesPerChannel, int sampleRate)
    {
        fixed (float* p = planar)
        {
            var frame = new NdiNative.AudioFrame
            {
                SampleRate = sampleRate,
                Channels = channels,
                Samples = samplesPerChannel,
                Timecode = NdiNative.TimecodeSynthesize,
                Data = (IntPtr)p,
                ChannelStride = samplesPerChannel * 4,
            };
            NdiNative.SendAudio(_handle, &frame);
        }
    }

    public void Dispose() => NdiNative.SendDestroy(_handle);
}
