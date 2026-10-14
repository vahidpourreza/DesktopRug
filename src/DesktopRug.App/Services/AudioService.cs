using System.Runtime.InteropServices;
using DesktopRug.App.Interop;

namespace DesktopRug.App.Services;

internal sealed class AudioService : IDisposable
{
    private readonly byte[] _rustle = Generate(.16, .032), _settle = Generate(.24, .045);
    private GCHandle _pin;
    private long _lastSound;
    internal bool Enabled { get; set; }
    internal void Rustle() => Play(_rustle, 600);
    internal void Settle() => Play(_settle, 250);
    private void Play(byte[] data, int minimumGap)
    {
        if (!Enabled || Environment.TickCount64 - _lastSound < minimumGap) return;
        Native.PlaySound(null, 0, 0); if (_pin.IsAllocated) _pin.Free();
        _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        Native.PlaySound(data, 0, 0x0004 | 0x0001 | 0x0002); // MEMORY | ASYNC | NODEFAULT
        _lastSound = Environment.TickCount64;
    }
    private static byte[] Generate(double duration, double volume)
    {
        const int rate = 22050; int count = (int)(duration * rate);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(count * 2);
        var random = new Random(17); double smooth = 0;
        for (int i = 0; i < count; i++)
        {
            smooth = smooth * .82 + (random.NextDouble() * 2 - 1) * .18;
            double envelope = Math.Pow(Math.Sin(Math.PI * i / count), 2);
            writer.Write((short)(smooth * envelope * volume * short.MaxValue));
        }
        return stream.ToArray();
    }
    public void Dispose() { Native.PlaySound(null, 0, 0); if (_pin.IsAllocated) _pin.Free(); }
}
