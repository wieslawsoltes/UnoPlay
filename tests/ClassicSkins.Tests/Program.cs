using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UnoPlay.ClassicSkins;
using UnoPlay.Core;

static void Check(bool assertion, string message)
{
    if (!assertion) throw new Exception(message);
}

static byte[] Bitmap(int bits, int mode, int width, int height, byte[] payload, uint[] palette)
{
    using var buffer = new MemoryStream();
    using var writer = new BinaryWriter(buffer);
    var offset = 54 + palette.Length * 4;
    writer.Write((ushort)0x4d42); writer.Write(offset + payload.Length); writer.Write(0); writer.Write(offset);
    writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)bits);
    writer.Write(mode); writer.Write(payload.Length); writer.Write(0); writer.Write(0);
    writer.Write(bits <= 8 ? palette.Length : 0); writer.Write(0);
    foreach (uint entry in palette) writer.Write(entry);
    writer.Write(payload);
    return buffer.ToArray();
}

static void Pixels(byte[] fixture, params uint[] expected)
{
    var image = SkinBitmap.Decode(fixture);
    var data = image.Bgra32.Span;
    Check(data.Length == 4 * expected.Length, "Pixel count");
    for (int p = 0; p < expected.Length; p++)
        Check(BinaryPrimitives.ReadUInt32LittleEndian(data[(p * 4)..]) == (0xff000000 | expected[p]), $"Pixel {p}");
}

static void Invalid(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Accepted invalid input");
}

static ClassicSkin Load(params (string Path, byte[] Data)[] entries)
{
    using var memory = new MemoryStream();
    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        foreach (var (path, data) in entries)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(data);
        }
    memory.Position = 0;
    return ClassicSkin.Load(memory);
}

const uint red = 0xff0000, blue = 0x0000ff;
uint[] palette = [red, blue];
var oneBit = Bitmap(1, 0, 2, 2, [0x80, 0, 0, 0, 0x40, 0, 0, 0], palette);
Pixels(oneBit, red, blue, blue, red);
Pixels(Bitmap(4, 0, 2, -1, [0x01, 0, 0, 0], palette), red, blue);
Pixels(Bitmap(8, 0, 2, 1, [1, 0, 0, 0], palette), blue, red);
Pixels(Bitmap(16, 0, 1, 1, [0x00, 0x7c, 0, 0], []), red);
Pixels(Bitmap(16, 3, 1, 1, [0xe0, 7, 0, 0], [0xf800, 0x07e0, 0x1f]), 0x00ff00);
Pixels(Bitmap(24, 0, 1, 1, [0xff, 0, 0, 0], []), blue);
Pixels(Bitmap(32, 0, 1, -1, [0, 0, 0xff, 0], []), red);
Pixels(Bitmap(32, 3, 1, 1, [0xff, 3, 0, 0], [0x3ff, 0xffc00, 0x3ff00000]), red);
Pixels(Bitmap(8, 1, 5, 1, [0, 3, 1, 0, 1, 0, 2, 1, 0, 1], palette), blue, red, blue, blue, blue);
Pixels(Bitmap(4, 2, 5, 1, [0, 3, 0x10, 0x10, 2, 0x01, 0, 1], palette), blue, red, blue, red, blue);
Pixels(Bitmap(8, 1, 3, 2, [0, 2, 1, 1, 2, 1, 0, 1], palette), red, blue, blue, red, red, red);
Invalid(() => SkinBitmap.Decode(Bitmap(8, 1, 1, 1, [5, 0, 0, 1], palette)));
Invalid(() => SkinBitmap.Decode(Bitmap(8, 0, 1, 1, [2, 0, 0, 0], palette)));
Invalid(() => SkinBitmap.Decode(Bitmap(32, 0, 65536, 65536, [], [])));
Invalid(() => Load(("../main.bmp", oneBit), ("readme.txt", [])));

var skin = Load(("Folder\\MAIN.BMP", oneBit), ("folder/PLEDIT.TXT", "[Text]\nNormal=#123456 ;sample\nFONT=Verdana"u8.ToArray()),
    ("VIScolor.TXT", "0,4,255 // color\n"u8.ToArray()), ("REGION.TXT", "[Normal]\nNumPoints=4\nPointList=0,0,1,0,1,1,0,1\n"u8.ToArray()),
    ("eqnormal.cur", [0, 2]));
Check(skin.GetBitmap("main.bmp")?.Width == 2, "Nested case-insensitive bitmap");
Check(skin.GetBitmap("MAIN", 0, 0, 275, 116)?.Width == 275, "Fallback for incomplete sheet");
Check(skin.GetBitmap("eqmain")?.Height == 315, "Bundled fallback");
Check(skin.Playlist.Normal == 0xff123456 && skin.Playlist.Font == "Verdana", "Playlist colors and font");
Check(skin.VisualizationColors.Count == 24 && skin.VisualizationColors[0] == 0xff0004ff, "Partial palette");
Check(skin.GetRegion("normal")[0][2] == new SkinPoint(1, 1), "Polygon regions");
Check(skin.GetCursor("EQNORMAL").Length == 2, "Cursor asset");
var mask = new RegionMask([
    [new(0, 0), new(2, 0), new(2, 2), new(0, 2)],
    [new(1, 1), new(3, 1), new(3, 3), new(1, 3)]
], 4, 4);
Check(mask.Count(-1, -1, 6, 6) == 7 && mask.Contains(1, 1) && !mask.Contains(3, 2), "Region union");
var rgba = Enumerable.Repeat((byte)255, 64).ToArray();
mask.Apply(rgba, 0, 0, 4, 4);
Check(rgba.Count(v => v != 0) == 28, "Premultiplied-alpha pixel mask");
Console.WriteLine("Passed classic bitmap, archive, metadata and fallback checks.");

var flat = EqualizerPreset.FromDecibels("Entry1", new double[10], 0);
var eqf = EqualizerFile.Write([flat]);
Check(eqf.Length == 299 && eqf.AsSpan(288).IndexOfAnyExcept((byte)31) < 0, "Classic flat EQ");
Check(EqualizerFile.Read(eqf).Single().Preamp == 0, "Neutral preamp");
eqf[^1] = 63; eqf[^2] = 0;
var stepped = EqualizerFile.Read(eqf).Single();
Check(stepped.Preamp == -12 && stepped.GetBand(9) == 12, "Inverted six-bit faders");
Invalid(() => EqualizerFile.Read(eqf.AsSpan(0, 298)));
eqf[^1] = 64;
Invalid(() => EqualizerFile.Read(eqf));
Check(ClassicEqualizerPresets.All.Count == 18, "Factory preset count");
Console.WriteLine("Passed EQF and Q1 compatibility checks.");

foreach (string file in args)
{
    if (Path.GetExtension(file).ToLowerInvariant() is ".eqf" or ".q1" or ".q2")
    {
        byte[] bytes = File.ReadAllBytes(file);
        var importedBank = EqualizerFile.Read(bytes);
        var encodedBank = EqualizerFile.Write(importedBank);
        Check(bytes.Length == encodedBank.Length, "Round trip length");
        for (int i = 0; i < importedBank.Count; i++)
        {
            int pos = 31 + 268 * i;
            int end = bytes.AsSpan(pos, 257).IndexOf((byte)0) + 1;
            Check(bytes.AsSpan(pos, end).SequenceEqual(encodedBank.AsSpan(pos, end)) &&
                bytes.AsSpan(pos + 257, 11).SequenceEqual(encodedBank.AsSpan(pos + 257, 11)), "External EQ bank round trip");
        }
        Console.WriteLine($"{Path.GetFileName(file)}: {importedBank.Count} presets, names and faders match.");
        continue;
    }
    using var stream = File.OpenRead(file);
    var imported = ClassicSkin.Load(stream);
    Console.WriteLine($"{Path.GetFileName(file)}: {imported.BitmapNames.Count()} sheets, " +
        $"{imported.GetRegion("Normal").Count} main-window polygons, {imported.CursorNames.Count()} cursors");
}
