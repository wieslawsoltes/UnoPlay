using System.Text;

namespace UnoPlay.Core;

/// <summary>Preserves the original six-bit fader positions, including the two center detents.</summary>
public sealed class EqualizerPreset
{
    private readonly byte[] positions;
    public string Name { get; }
    public double Preamp => ToDecibels(positions[10]);

    public double GetBand(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, 10u);
        return ToDecibels(positions[index]);
    }

    internal EqualizerPreset(string name, ReadOnlySpan<byte> positions)
    {
        if (name.Contains('\0') || positions.Length != 11 || positions.ContainsAnyExceptInRange((byte)0, (byte)63))
            throw new InvalidDataException("Invalid classic EQ preset.");
        Name = name;
        this.positions = positions.ToArray();
    }

    public static EqualizerPreset FromDecibels(string name, ReadOnlySpan<double> bands, double preamp)
    {
        if (bands.Length != 10) throw new ArgumentException("Expected ten equalizer bands.", nameof(bands));
        Span<byte> values = stackalloc byte[11];
        for (int i = 0; i < 10; i++) values[i] = ToPosition(bands[i]);
        values[10] = ToPosition(preamp);
        return new(name, values);
    }

    internal void CopyPositionsTo(Span<byte> buffer) => positions.CopyTo(buffer);

    private static double ToDecibels(byte gain) => gain is 31 or 32 ? 0 : (31.5 - gain) * (12 / 31.5);
    private static byte ToPosition(double db)
    {
        if (!double.IsFinite(db) || db is < -12 or > 12) throw new ArgumentOutOfRangeException(nameof(db));
        return db == 0 ? (byte)31 : (byte)Math.Round(31.5 - db * (31.5 / 12));
    }
}

/// <summary>Reads and writes Winamp EQF, Q1 and Q2 libraries. Single-file presets are named Entry1 by Winamp.</summary>
public static class EqualizerFile
{
    private static ReadOnlySpan<byte> Signature => "Winamp EQ library file v1.1\u001a!--"u8;
    private const int NameSize = 257, RecordSize = NameSize + 11, MaxPresets = 4096;
    public const int MaxFileBytes = 31 + RecordSize * MaxPresets;

    public static IReadOnlyList<EqualizerPreset> Read(ReadOnlySpan<byte> file, Encoding? nameEncoding = null)
    {
        if (file.Length > MaxFileBytes || !file.StartsWith(Signature) || (file.Length - Signature.Length) % RecordSize != 0)
            throw new InvalidDataException("Invalid Winamp equalizer preset library.");
        file = file[Signature.Length..];
        var presets = new List<EqualizerPreset>(file.Length / RecordSize);
        var encoding = nameEncoding ?? Encoding.Latin1;
        while (!file.IsEmpty)
        {
            var field = file[..NameSize];
            int end = field.IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException("Missing EQ preset name terminator.");
            presets.Add(new EqualizerPreset(encoding.GetString(field[..end]), file.Slice(NameSize, 11)));
            file = file[RecordSize..];
        }
        return presets.AsReadOnly();
    }

    public static byte[] Write(IReadOnlyList<EqualizerPreset> presets, Encoding? nameEncoding = null)
    {
        if (presets.Count > MaxPresets) throw new ArgumentOutOfRangeException(nameof(presets));
        var encoding = nameEncoding ?? Encoding.Latin1;
        var bytes = new byte[Signature.Length + presets.Count * RecordSize];
        Signature.CopyTo(bytes);
        int offset = Signature.Length;
        foreach (var preset in presets)
        {
            if (encoding.GetByteCount(preset.Name) >= NameSize) throw new InvalidDataException("Preset name is too long.");
            encoding.GetBytes(preset.Name, bytes.AsSpan(offset, NameSize - 1));
            preset.CopyPositionsTo(bytes.AsSpan(offset + NameSize, 11));
            offset += RecordSize;
        }
        return bytes;
    }
}
