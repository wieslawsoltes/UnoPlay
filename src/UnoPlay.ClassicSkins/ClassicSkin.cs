using System.IO.Compression;

namespace UnoPlay.ClassicSkins;

/// <summary>A classic Winamp skin. Loading never extracts files to disk. A missing sheet inherits the bundled skin.</summary>
public sealed class ClassicSkin
{
    private const int MaxArchiveBytes = 32 * 1024 * 1024;
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    private const int MaxSettingsBytes = 256 * 1024;
    private const int MaxDecodedPixels = 8 * 1024 * 1024;
    private static readonly HashSet<string> Sheets = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "cbuttons", "titlebar", "text", "nums_ex", "numbers", "monoster", "playpaus",
        "shufrep", "volume", "balance", "posbar", "eqmain", "eq_ex", "pledit", "mb", "avs", "video",
        "gen", "genex", "gen_ex", "font", "spec"
    };
    private readonly Dictionary<string, SkinBitmap> bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> cursors = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SkinPoint[][]> regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool builtIn;
    private static readonly Lazy<ClassicSkin> BuiltIn = new(() =>
    {
        using var stream = typeof(ClassicSkin).Assembly.GetManifestResourceStream("UnoPlay.Midnight.wsz")
            ?? throw new InvalidOperationException("Built-in classic skin resource is missing.");
        return ReadArchive(stream, true);
    });

    private ClassicSkin(bool builtIn) => this.builtIn = builtIn;
    public static ClassicSkin Default => BuiltIn.Value;
    public PlaylistStyle Playlist { get; private set; } = PlaylistStyle.Default;
    public IReadOnlyList<uint> VisualizationColors { get; private set; } = Array.AsReadOnly(SkinSettings.DefaultColors);
    public IEnumerable<string> BitmapNames => bitmaps.Keys;
    public bool HasCursors => cursors.Count > 0;
    public IEnumerable<string> CursorNames => cursors.Keys;

    public SkinBitmap? GetBitmap(string sheet)
    {
        var key = Path.GetFileNameWithoutExtension(sheet);
        return bitmaps.TryGetValue(key, out var bmp) ? bmp : builtIn ? null : Default.GetBitmap(key);
    }

    /// <summary>Resolves at sprite granularity so shorter, older revisions of a sheet inherit only the missing sprites.</summary>
    public SkinBitmap? GetBitmap(string sheet, int x, int y, int width, int height)
    {
        var bmp = GetBitmap(sheet);
        if (Fits(bmp, x, y, width, height)) return bmp;
        var fallback = builtIn ? null : Default.GetBitmap(sheet);
        return Fits(fallback, x, y, width, height) ? fallback : null;
    }

    private static bool Fits(SkinBitmap? bitmap, int x, int y, int w, int h) =>
        bitmap != null && x >= 0 && y >= 0 && w > 0 && h > 0 && x <= bitmap.Width - w && y <= bitmap.Height - h;

    public ReadOnlyMemory<byte> GetCursor(string name) =>
        cursors.TryGetValue(Path.GetFileNameWithoutExtension(name), out var bytes) ? bytes : ReadOnlyMemory<byte>.Empty;

    public IReadOnlyList<SkinPoint[]> GetRegion(string name) =>
        regions.TryGetValue(name, out var polygons) ? polygons : Array.Empty<SkinPoint[]>();

    public static ClassicSkin Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Position == 0)
        {
            if (stream.Length > MaxArchiveBytes) throw new InvalidDataException("Skin archive is too large.");
            return ReadArchive(stream, false);
        }

        using var copy = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (copy.Length + read > MaxArchiveBytes) throw new InvalidDataException("Skin archive is too large.");
            copy.Write(buffer, 0, read);
        }
        copy.Position = 0;
        return ReadArchive(copy, false);
    }

    private static ClassicSkin ReadArchive(Stream stream, bool builtIn)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > 2048) throw new InvalidDataException("Skin archive contains too many entries.");

        var skin = new ClassicSkin(builtIn);
        int pixelsLeft = MaxDecodedPixels;
        long bytesLeft = MaxArchiveBytes;
        bool recognized = false;
        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith('/') || path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
                path.Split('/').Any(p => p is ".." or ".")) continue;
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool bitmap = ext == ".bmp" && Sheets.Contains(name);
            bool cursor = ext == ".cur" && name.Length is > 0 and <= 32;
            bool settings = ext == ".txt" && name is "pledit" or "region" or "viscolor";
            if (!bitmap && !cursor && !settings) continue;
            int limit = bitmap ? MaxEntryBytes : MaxSettingsBytes;
            if (entry.Length > limit || entry.Length > bytesLeft) throw new InvalidDataException("Skin assets exceed the size limit.");
            bytesLeft -= entry.Length;
            byte[] raw = new byte[(int)entry.Length];
            using (var source = entry.Open())
            {
                source.ReadExactly(raw);
                if (source.ReadByte() != -1) throw new InvalidDataException("Skin entry length mismatch.");
            }

            if (bitmap)
            {
                var value = SkinBitmap.Decode(raw, pixelsLeft);
                pixelsLeft -= value.Width * value.Height;
                skin.bitmaps[name] = value;
            }
            else if (cursor) skin.cursors[name] = raw;
            else
            {
                string text = SkinSettings.DecodeText(raw);
                switch (name)
                {
                    case "pledit": skin.Playlist = SkinSettings.ReadPlaylist(text); break;
                    case "region": skin.regions = SkinSettings.ReadRegions(text); break;
                    case "viscolor": skin.VisualizationColors = Array.AsReadOnly(SkinSettings.ReadColors(text)); break;
                }
            }
            recognized = true;
        }
        if (!recognized) throw new InvalidDataException("This archive does not contain a classic Winamp skin.");
        if (skin.bitmaps.TryGetValue("volume", out var volume)) skin.bitmaps.TryAdd("balance", volume);
        if (skin.bitmaps.TryGetValue("nums_ex", out var extDigits)) skin.bitmaps["numbers"] = extDigits;
        else if (skin.bitmaps.TryGetValue("numbers", out var digits) && digits.Width >= 90 && digits.Height >= 13)
            skin.bitmaps["nums_ex"] = ExtendDigits(digits);
        return skin;
    }

    private static SkinBitmap ExtendDigits(SkinBitmap numbers)
    {
        const int width = 108;
        byte[] output = new byte[width * 13 * 4];
        var source = numbers.Bgra32.Span;
        for (int y = 0; y < 13; y++)
        {
            var row = output.AsSpan(y * width * 4, width * 4);
            var input = source.Slice(y * numbers.Width * 4, numbers.Width * 4);
            input[..360].CopyTo(row);
            if (numbers.Width >= 99) input.Slice(360, 36).CopyTo(row[360..]);
            else for (int x = 90; x < 99; x++) input[..4].CopyTo(row[(x * 4)..]);
            row.Slice(360, 36).CopyTo(row[396..]);
            if (y == 6) input.Slice(20 * 4, 5 * 4).CopyTo(row[(101 * 4)..]);
        }
        return new(width, 13, output);
    }
}
