using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CuePlayer;

/// <summary>
/// Local listening (Peter 2026-11-26): the same stereo sound that goes to NDI/OMT/SDI, played on a Windows audio device.
/// The output thread writes each frame's samples into a small buffer; WASAPI plays it on its own clock. The two clocks differ a little,
/// so the buffer is trimmed back when it grows beyond 250 ms (a short skip now and then, fine for monitoring; nothing on air is affected).
/// </summary>
internal sealed class AudioMonitor : IDisposable
{
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _buffer;
    private float[] _inter = new float[2 * 4096];
    private byte[] _bytes = new byte[2 * 4096 * 4];

    public string DeviceName { get; }

    /// <summary>Active playback devices: (id, name), the Windows default first.</summary>
    public static List<(string id, string name)> Devices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add((d.ID, d.FriendlyName)); } catch { }
                d.Dispose();
            }
            list.Sort((a, b) => (b.Item1 == def).CompareTo(a.Item1 == def));
        }
        catch (Exception e) { Log.Write("audio devices: " + e.Message); }
        return list;
    }

    public AudioMonitor(string deviceId)
    {
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDevice(deviceId);
        DeviceName = dev.FriendlyName;
        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true,   // nothing buffered: silence, the device keeps running
        };
        _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 40);
        _out.Init(_buffer);
        _out.Play();
    }

    /// <summary>Output thread: n samples per channel, planar (channel c at c*n); the first two channels are played.</summary>
    public void Write(float[] planar, int channels, int n)
    {
        if (_buffer.BufferedDuration.TotalMilliseconds > 250) _buffer.ClearBuffer();
        if (_inter.Length < n * 2) { _inter = new float[n * 2]; _bytes = new byte[n * 8]; }
        int c2 = Math.Min(channels, 2);
        for (int i = 0; i < n; i++)
        {
            float l = planar[i];
            float r = c2 > 1 ? planar[n + i] : l;
            _inter[2 * i] = l; _inter[2 * i + 1] = r;
        }
        Buffer.BlockCopy(_inter, 0, _bytes, 0, n * 8);
        _buffer.AddSamples(_bytes, 0, n * 8);
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
