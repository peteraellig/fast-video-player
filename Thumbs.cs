using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CuePlayer;

/// <summary>
/// Small first pictures for the cue list (Peter 2026-11-27): 160x90 JPEG in C:\ProgramData\CuePlayer\thumbs, named after the file,
/// its time and size (a changed file gets a new one). The page loads them from https://thumbs.cueplayer.app/ (WebView2 folder mapping,
/// no network). Made when a file is probed: videos and pictures from the decode check's first picture, VMX from libvmx.
/// </summary>
internal static class Thumbs
{
    public const int Width = 160, Height = 90;
    public static readonly string Folder = Path.Combine(ShowStore.Folder, "thumbs");

    public static string NameFor(string path)
    {
        var fi = new FileInfo(path);
        string key = $"{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16] + ".jpg";
    }

    public static bool Exists(string? name) => !string.IsNullOrEmpty(name) && File.Exists(Path.Combine(Folder, name));

    /// <summary>A UYVY picture of exactly Width x Height (BT.709 limited range) as JPEG.</summary>
    public static unsafe bool SaveUyvy(IntPtr uyvy, string name)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            var d = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, bmp.PixelFormat);
            byte* s = (byte*)uyvy;
            for (int y = 0; y < Height; y++)
            {
                byte* row = s + y * Width * 2;
                byte* dst = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < Width; x++)
                {
                    int u = row[(x & ~1) * 2] - 128, v = row[(x & ~1) * 2 + 2] - 128, yy = row[x * 2 + 1] - 16;
                    int r = (int)(1.164 * yy + 1.793 * v), g = (int)(1.164 * yy - 0.213 * u - 0.533 * v), b = (int)(1.164 * yy + 2.112 * u);
                    dst[x * 3] = (byte)Math.Clamp(b, 0, 255); dst[x * 3 + 1] = (byte)Math.Clamp(g, 0, 255); dst[x * 3 + 2] = (byte)Math.Clamp(r, 0, 255);
                }
            }
            bmp.UnlockBits(d);
            var file = Path.Combine(Folder, name);
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            bmp.Save(file + ".tmp", jpeg, p);
            File.Move(file + ".tmp", file, true);
            return true;
        }
        catch (Exception e) { Log.Write("thumbnail failed: " + e.Message); return false; }
    }

    /// <summary>A UYVY picture of any size scaled to fit Width x Height (nearest pixel, black bars), then saved.</summary>
    public static unsafe bool SaveUyvyScaled(IntPtr src, int w, int h, string name)
    {
        var dst = Marshal.AllocHGlobal(Width * Height * 2);
        try
        {
            uint* d32 = (uint*)dst;
            for (int i = 0; i < Width * Height / 2; i++) d32[i] = 0x10801080;
            double scale = Math.Min(Width / (double)w, Height / (double)h);
            int fw = ((int)(w * scale)) & ~1, fh = (int)(h * scale);
            int x0 = ((Width - fw) / 2) & ~1, y0 = (Height - fh) / 2;
            for (int y = 0; y < fh; y++)
            {
                int sy = Math.Min(h - 1, (int)(y / scale));
                uint* srow = (uint*)((byte*)src + sy * w * 2);
                uint* drow = (uint*)((byte*)dst + (y0 + y) * Width * 2) + x0 / 2;
                for (int x = 0; x < fw / 2; x++) drow[x] = srow[Math.Min(w / 2 - 1, (int)(x * 2 / scale) / 2)];
            }
            return SaveUyvy(dst, name);
        }
        finally { Marshal.FreeHGlobal(dst); }
    }

    /// <summary>Removes thumbnails no cue uses any more (called at start with the names in use).</summary>
    public static void Clean(IEnumerable<string?> inUse)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var keep = new HashSet<string>(inUse.Where(n => !string.IsNullOrEmpty(n))!, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(Folder, "*.jpg"))
                if (!keep.Contains(Path.GetFileName(f)) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f);
        }
        catch { }
    }
}
