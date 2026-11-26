using System.Runtime.InteropServices;
using System.Text;

namespace CuePlayer;

// Minimal P/Invoke layer for libomt.dll (OMT SDK, MIT). Struct layout follows libomt.h (OMTMediaFrame).
// Taken from PresenterOmt, plus UYVY and the receive side (used by --probe only).
internal static class OmtNative
{
    public const int FrameTypeVideo = 2;
    public const int FrameTypeAudio = 4;
    public const int CodecFpa1 = 0x31415046;   // planar float audio
    public const int CodecUyvy = 0x59565955;
    public const int FlagInterlaced = 1;
    public const int ColorSpaceBt709 = 709;
    public const int QualityDefault = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MediaFrame
    {
        public int Type;
        public long Timestamp;
        public int Codec;
        public int Width;
        public int Height;
        public int Stride;
        public int Flags;
        public int FrameRateN;
        public int FrameRateD;
        public float AspectRatio;
        public int ColorSpace;
        public int SampleRate;
        public int Channels;
        public int SamplesPerChannel;
        public IntPtr Data;
        public int DataLength;
        public IntPtr CompressedData;
        public int CompressedLength;
        public IntPtr FrameMetadata;
        public int FrameMetadataLength;
    }

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern long omt_send_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int quality);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern void omt_send_destroy(long instance);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern int omt_send(long instance, ref MediaFrame frame);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern int omt_send_connections(long instance);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern int omt_send_getaddress(long instance, byte[] address, int maxLength);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr omt_receive_create([MarshalAs(UnmanagedType.LPUTF8Str)] string address, int frameTypes, int format, int flags);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern void omt_receive_destroy(IntPtr instance);

    [DllImport("libomt", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr omt_receive(IntPtr instance, int frameTypes, int timeoutMilliseconds);
}

internal sealed class OmtSender : IDisposable
{
    private readonly long _handle;
    public string Address { get; }

    public OmtSender(string name)
    {
        _handle = OmtNative.omt_send_create(name, OmtNative.QualityDefault);
        if (_handle == 0) throw new InvalidOperationException("omt_send_create failed");
        var buf = new byte[512];
        int n = OmtNative.omt_send_getaddress(_handle, buf, buf.Length);
        Address = n > 1 ? Encoding.UTF8.GetString(buf, 0, n - 1) : name;
    }

    public int Connections => OmtNative.omt_send_connections(_handle);

    /// <summary>Sends one UYVY picture. <paramref name="timestamp"/> is in 100 ns units; the caller paces the sending.</summary>
    public int SendVideo(IntPtr pixels, OutputFormat f, long timestamp)
    {
        var frame = new OmtNative.MediaFrame
        {
            Type = OmtNative.FrameTypeVideo,
            Timestamp = timestamp,
            Codec = OmtNative.CodecUyvy,
            Width = f.Width,
            Height = f.Height,
            Stride = f.Width * 2,
            Flags = f.Interlaced ? OmtNative.FlagInterlaced : 0,
            FrameRateN = f.RateN,
            FrameRateD = f.RateD,
            AspectRatio = (float)f.Width / f.Height,
            ColorSpace = OmtNative.ColorSpaceBt709,
            Data = pixels,
            DataLength = f.Width * 2 * f.Height,
        };
        return OmtNative.omt_send(_handle, ref frame);
    }

    /// <summary>Sends planar float audio (channels * samples floats, one plane after the other).</summary>
    public unsafe int SendAudio(float[] planar, int channels, int samplesPerChannel, int sampleRate, long timestamp)
    {
        fixed (float* p = planar)
        {
            var frame = new OmtNative.MediaFrame
            {
                Type = OmtNative.FrameTypeAudio,
                Timestamp = timestamp,
                Codec = OmtNative.CodecFpa1,
                SampleRate = sampleRate,
                Channels = channels,
                SamplesPerChannel = samplesPerChannel,
                Data = (IntPtr)p,
                DataLength = samplesPerChannel * channels * 4,
            };
            return OmtNative.omt_send(_handle, ref frame);
        }
    }

    public void Dispose() => OmtNative.omt_send_destroy(_handle);
}
