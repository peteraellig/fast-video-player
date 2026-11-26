using System.Runtime.InteropServices;

namespace CuePlayer;

/// <summary>
/// libvmx (OMT SDK, MIT, shipped next to libomt.dll): the VMX codec that vMix records with (fourcc VMX1).
/// libVLC and FFmpeg cannot decode it; FFmpeg still demuxes the packets from the AVI.
/// </summary>
internal static unsafe class VmxNative
{
    private const string Dll = "libvmx.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct VmxSize { public int Width, Height; }

    public const int ColorSpaceBt709 = 709;
    public const int ProfileDefault = 0;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr VMX_Create(VmxSize size, int profile, int colorSpace);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void VMX_Destroy(IntPtr instance);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int VMX_LoadFrom(IntPtr instance, byte* src, int srcLen);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int VMX_DecodeUYVY(IntPtr instance, byte* dst, int stride);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int VMX_SetThreads(IntPtr instance, int threads);
}
