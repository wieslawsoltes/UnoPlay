using System.Buffers.Binary;

namespace UnoPlay.ClassicSkins;

public sealed record CursorStep(int Frame, int DelayMilliseconds);

/// <summary>An ICO-compatible frame and its hotspot. ICO encoding lets the host's native image codec decode DIB/PNG payloads.</summary>
public sealed record CursorFrame(int Width, int Height, int HotX, int HotY, ReadOnlyMemory<byte> Icon);

/// <summary>Static Windows cursors and RIFF ACON animation timelines from classic skins.</summary>
public sealed class SkinCursor
{
    public IReadOnlyList<CursorFrame> Frames { get; }
    public IReadOnlyList<CursorStep> Steps { get; }

    private SkinCursor(CursorFrame[] frames, CursorStep[] steps) => (Frames, Steps) = (frames, steps);

    public static SkinCursor Decode(ReadOnlyMemory<byte> data, int preferredSize = 32)
    {
        if (data.Length is < 6 or > 262144) throw new InvalidDataException("Invalid skin cursor length.");
        if (!data.Span.StartsWith("RIFF"u8)) return new([ReadFrame(data.Span, preferredSize)], [new(0, 0)]);
        if (data.Length < 12 || !data.Span.Slice(8, 4).SequenceEqual("ACON"u8)) throw new InvalidDataException("Invalid ANI header.");
        var frames = new List<CursorFrame>();
        var order = Array.Empty<int>();
        var rates = Array.Empty<int>();
        int count = 0, defaultRate = 6;
        int end = U32(data.Span, 4);
        // Several classic cursor editors recorded the total file size instead of the RIFF payload size.
        if (end == data.Length) end -= 8;
        if (end < 4 || end > data.Length - 8) throw new InvalidDataException("Invalid RIFF size.");
        ReadChunks(data.Span.Slice(12, end - 4), false);
        if (frames.Count == 0) throw new InvalidDataException("Missing ANI cursor frames.");
        if (count == 0) count = order.Length != 0 ? order.Length : frames.Count;
        if (count > 512 || order.Length != 0 && order.Length != count || rates.Length != 0 && rates.Length != count)
            throw new InvalidDataException("Invalid ANI sequence.");
        var steps = new CursorStep[count];
        for (int i = 0; i < count; i++)
        {
            int index = order.Length != 0 ? order[i] : i % frames.Count;
            if (index >= frames.Count) throw new InvalidDataException("Invalid ANI frame index.");
            int rate = rates.Length != 0 && rates[i] > 0 ? rates[i] : Math.Max(defaultRate, 1);
            steps[i] = new(index, (int)Math.Min(int.MaxValue, (rate * 1000L + 30) / 60));
        }
        return new(frames.ToArray(), steps);

        void ReadChunks(ReadOnlySpan<byte> chunks, bool frameList)
        {
            while (chunks.Length >= 8)
            {
                var id = chunks[..4];
                int n = U32(chunks, 4);
                if (n > chunks.Length - 8) throw new InvalidDataException("Truncated ANI chunk.");
                var body = chunks.Slice(8, n);
                if (frameList)
                {
                    if (id.SequenceEqual("icon"u8))
                    {
                        if (frames.Count >= 256) throw new InvalidDataException("Too many ANI frames.");
                        frames.Add(ReadFrame(body, preferredSize));
                    }
                }
                else if (id.SequenceEqual("anih"u8) && n >= 36) { count = U32(body, 8); defaultRate = U32(body, 28); }
                else if (id.SequenceEqual("LIST"u8) && body.StartsWith("fram"u8)) ReadChunks(body[4..], true);
                else if (id.SequenceEqual("seq "u8) || id.SequenceEqual("rate"u8))
                {
                    if (n % 4 != 0 || n / 4 > 512) throw new InvalidDataException("Invalid ANI sequence length.");
                    var values = new int[n / 4];
                    for (int i = 0; i < values.Length; i++) values[i] = U32(body, 4 * i);
                    if (id.SequenceEqual("seq "u8)) order = values; else rates = values;
                }
                chunks = chunks[Math.Min(chunks.Length, 8 + n + (n & 1))..];
            }
        }
    }

    private static CursorFrame ReadFrame(ReadOnlySpan<byte> data, int preferredSize)
    {
        if (data.Length < 22 || U16(data, 0) != 0 || U16(data, 2) is not (1 or 2))
            throw new InvalidDataException("Invalid CUR header.");
        int entries = U16(data, 4);
        if (entries < 1 || entries > (data.Length - 6) / 16) throw new InvalidDataException("Invalid CUR directory.");
        int best = 0, distance = int.MaxValue;
        for (int i = 0; i < entries; i++)
        {
            int size = data[6 + i * 16];
            int d = Math.Abs((size == 0 ? 256 : size) - Math.Clamp(preferredSize, 1, 256));
            if (d < distance) { best = 6 + i * 16; distance = d; }
        }
        var entry = data.Slice(best, 16);
        int w = entry[0] == 0 ? 256 : entry[0], h = entry[1] == 0 ? 256 : entry[1];
        int len = U32(entry, 8), offset = U32(entry, 12);
        if (len <= 0 || offset < 6 + entries * 16 || offset > data.Length - len)
            throw new InvalidDataException("Invalid CUR image range.");
        bool cur = U16(data, 2) == 2;
        int hotX = cur ? Math.Min(w - 1, (int)U16(entry, 4)) : 0;
        int hotY = cur ? Math.Min(h - 1, (int)U16(entry, 6)) : 0;
        byte[] icon = new byte[22 + len];
        icon[2] = 1; icon[4] = 1;
        entry.CopyTo(icon.AsSpan(6));
        var payload = data.Slice(offset, len);
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(10), 1);
        ushort bits = payload.Length >= 16 && payload[0] >= 40 && payload[1] == 0 ? U16(payload, 14) : (ushort)32;
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(12), bits);
        BinaryPrimitives.WriteInt32LittleEndian(icon.AsSpan(18), 22);
        payload.CopyTo(icon.AsSpan(22));
        return new(w, h, hotX, hotY, icon);
    }

    private static int U32(ReadOnlySpan<byte> value, int index)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(value[index..]);
        return n >= 0 ? n : throw new InvalidDataException("Unsupported cursor field size.");
    }
    private static ushort U16(ReadOnlySpan<byte> value, int index) => BinaryPrimitives.ReadUInt16LittleEndian(value[index..]);
}
