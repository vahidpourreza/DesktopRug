using System.Buffers.Binary;
using DesktopRug.App.Interop;

namespace DesktopRug.App.Services;

internal sealed class AudioService : IDisposable
{
    // Pinned-object-heap buffers remain valid for asynchronous PlaySound without allocating a
    // new buffer, stream, pin handle, or synthesis work on each gesture.
    private readonly byte[][] _rustles =
    [
        Generate(.145, .029, .79, 17), Generate(.17, .032, .83, 43),
        Generate(.155, .030, .86, 79), Generate(.185, .028, .81, 113),
        Generate(.16, .031, .84, 157), Generate(.175, .029, .80, 211)
    ];
    private readonly byte[][] _settles =
    [
        Generate(.24, .042, .86, 307), Generate(.265, .039, .89, 401),
        Generate(.225, .041, .87, 503)
    ];
    private readonly Random _variation = new();
    private int _lastRustle = -1, _lastSettle = -1;
    private long _lastSound;
    private bool _enabled, _disposed;
    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed) return;
            if (_enabled && !value) Native.PlaySound(null, 0, 0);
            _enabled = value;
        }
    }
    internal void Rustle() => Play(_rustles, ref _lastRustle, 600);
    internal void Settle() => Play(_settles, ref _lastSettle, 250);
    private void Play(byte[][] variations, ref int lastVariation, int minimumGap)
    {
        if (_disposed || !_enabled || Environment.TickCount64 - _lastSound < minimumGap) return;
        int selected = lastVariation < 0 ? _variation.Next(variations.Length) : _variation.Next(variations.Length - 1);
        if (lastVariation >= 0 && selected >= lastVariation) selected++;
        byte[] data = variations[selected];
        Native.PlaySound(null, 0, 0);
        Native.PlaySound(data, 0, 0x0004 | 0x0001 | 0x0002); // MEMORY | ASYNC | NODEFAULT
        lastVariation = selected;
        _lastSound = Environment.TickCount64;
    }
    private static byte[] Generate(double duration, double volume, double softness, int seed)
    {
        const int rate = 22050; int count = (int)(duration * rate);
        byte[] data = GC.AllocateUninitializedArray<byte>(44 + count * 2, pinned: true);
        "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 36 + count * 2);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), count * 2);
        var random = new Random(seed); double smooth = 0;
        for (int i = 0; i < count; i++)
        {
            smooth = smooth * softness + (random.NextDouble() * 2 - 1) * (1 - softness);
            double envelope = Math.Pow(Math.Sin(Math.PI * i / count), 2);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(44 + i * 2), (short)(smooth * envelope * volume * short.MaxValue));
        }
        return data;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _enabled = false;
        Native.PlaySound(null, 0, 0);
    }
}
