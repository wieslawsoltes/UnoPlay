using System.Buffers.Binary;
using System.Numerics;

namespace UnoPlay.ClassicSkins;

/// <summary>A decoded, top-down bitmap with opaque, tightly packed BGRA pixels.</summary>
public sealed class SkinBitmap
{
    internal SkinBitmap(int width, int height, byte[] pixels) =>
        (Width, Height, Bgra32) = (width, height, pixels);

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Bgra32 { get; }

    /// <summary>Decodes classic Windows and OS/2 device-independent bitmaps.</summary>
    public static SkinBitmap Decode(ReadOnlySpan<byte> bmp) => Decode(bmp, 4 * 1024 * 1024);

    internal static SkinBitmap Decode(ReadOnlySpan<byte> bmp, int pixelBudget)
    {
        Require(bmp.Length >= 26 && bmp[0] == 'B' && bmp[1] == 'M');
        int offset = I32(bmp, 10), dib = I32(bmp, 14);
        Require(dib is 12 or 40 or 52 or 56 or 64 or 108 or 124 && offset >= 14 + dib && offset <= bmp.Length);

        bool core = dib == 12;
        int width = core ? U16(bmp, 18) : I32(bmp, 18);
        int signedHeight = core ? U16(bmp, 20) : I32(bmp, 22);
        Require(width is > 0 and <= 8192 && signedHeight is >= -8192 and <= 8192 && signedHeight != 0);
        int height = Math.Abs(signedHeight);
        Require((long)width * height <= Math.Min(pixelBudget, 4 * 1024 * 1024));
        Require(U16(bmp, core ? 22 : 26) == 1);
        int bits = U16(bmp, core ? 24 : 28);
        int mode = core ? 0 : I32(bmp, 30);
        Require(mode switch
        {
            0 => bits is 1 or 4 or 8 or 16 or 24 or 32,
            1 => bits == 8 && signedHeight > 0,
            2 => bits == 4 && signedHeight > 0,
            3 or 6 => bits is 16 or 32 && dib != 64,
            _ => false
        });

        uint[] palette = [];
        if (bits <= 8)
        {
            int count = core ? 0 : I32(bmp, 46);
            if (count == 0) count = 1 << bits;
            int stride = core ? 3 : 4;
            int start = 14 + dib;
            Require(count > 0 && count <= 1 << bits && start + count * stride <= offset);
            palette = new uint[count];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * stride;
                palette[i] = 0xff000000u | (uint)(bmp[p] | bmp[p + 1] << 8 | bmp[p + 2] << 16);
            }
        }

        var red = new ComponentMask(0x7c00);
        var green = new ComponentMask(0x03e0);
        var blue = new ComponentMask(0x001f);
        if (mode is 3 or 6)
        {
            Require(offset >= 66 && (mode != 6 || offset >= 70));
            uint r = U32(bmp, 54), g = U32(bmp, 58), b = U32(bmp, 62);
            Require((r & g | g & b | r & b) == 0 && (bits == 32 || (r | g | b) <= 0xffff));
            red = new(r); green = new(g); blue = new(b);
        }

        var pixels = new byte[width * height * 4];
        if (mode is 1 or 2)
        {
            DecodeRle(bmp[offset..], pixels, width, height, bits, palette);
        }
        else
        {
            int stride = ((width * bits + 31) / 32) * 4;
            Require((long)stride * height <= bmp.Length - offset);
            for (int y = 0; y < height; y++)
            {
                var source = bmp.Slice(offset + (signedHeight < 0 ? y : height - 1 - y) * stride, stride);
                var dest = pixels.AsSpan(y * width * 4, width * 4);
                for (int x = 0; x < width; x++)
                {
                    uint color;
                    if (bits <= 8)
                    {
                        int index = bits switch
                        {
                            8 => source[x],
                            4 => source[x >> 1] >> (4 * (1 - (x & 1))) & 15,
                            _ => source[x >> 3] >> (7 - (x & 7)) & 1
                        };
                        color = Lookup(palette, index);
                    }
                    else if (bits == 24 || bits == 32 && mode == 0)
                    {
                        int p = x * (bits / 8);
                        color = 0xff000000u | (uint)(source[p] | source[p + 1] << 8 | source[p + 2] << 16);
                    }
                    else
                    {
                        uint value = bits == 16 ? U16(source, x * 2) : U32(source, x * 4);
                        color = 0xff000000u | red.Get(value) << 16 | green.Get(value) << 8 | blue.Get(value);
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(x * 4), color);
                }
            }
        }
        return new(width, height, pixels);
    }

    private readonly struct ComponentMask
    {
        private readonly int shift;
        private readonly uint maximum;
        internal ComponentMask(uint value)
        {
            shift = BitOperations.TrailingZeroCount(value);
            maximum = value >> shift;
            Require(value != 0 && (maximum & (maximum + 1)) == 0);
        }

        internal uint Get(uint value) => (uint)((((value >> shift) & maximum) * 255ul + maximum / 2) / maximum);
    }

    private static void DecodeRle(ReadOnlySpan<byte> data, byte[] output, int w, int h, int bits, uint[] palette)
    {
        for (int i = 0; i < output.Length; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i), palette[0]);

        int p = 0, x = 0, y = h - 1;
        while (p + 2 <= data.Length)
        {
            int count = data[p++], code = data[p++];
            if (count == 0 && code == 1) return;
            if (count == 0 && code == 0) { x = 0; y--; continue; }
            Require(y >= 0);
            if (count == 0 && code == 2)
            {
                Require(p + 2 <= data.Length);
                x += data[p++];
                y -= data[p++];
                Require(x <= w && y >= 0);
                continue;
            }
            bool literal = count == 0;
            if (literal) count = code;
            int bytes = bits == 8 ? count : (count + 1) / 2;
            Require(count <= w - x && (!literal || p + ((bytes + 1) & ~1) <= data.Length));
            for (int i = 0; i < count; i++, x++)
            {
                int value = literal ? data[p + (bits == 8 ? i : i / 2)] : code;
                int index = bits == 8 ? value : value >> (4 * (1 - (i & 1))) & 15;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan((y * w + x) * 4), Lookup(palette, index));
            }
            if (literal) p += (bytes + 1) & ~1;
        }
        Require(y < 0 || y == 0 && x == w);
    }

    private static uint Lookup(uint[] palette, int index) =>
        index < palette.Length ? palette[index] : throw new InvalidDataException("Invalid bitmap palette index.");

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static int I32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("Invalid or unsupported classic skin bitmap.");
    }
}
