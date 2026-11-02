using System.Globalization;
using System.Text;

namespace UnoPlay.ClassicSkins;

public readonly record struct SkinPoint(int X, int Y);
public sealed record PlaylistStyle(uint Normal, uint Current, uint NormalBG, uint SelectedBG, string Font,
    uint MiniBrowserFG, uint MiniBrowserBG)
{
    public static PlaylistStyle Default { get; } =
        new(0xff00ff00, 0xffffffff, 0xff000000, 0xff000080, "Arial", 0xff00ff00, 0xff000000);
}

internal static class SkinSettings
{
    internal static readonly uint[] DefaultColors = [0xff000000, 0xff181828, 0xffff0000, 0xffef3100,
        0xffde6200, 0xffce9400, 0xffbdc500, 0xffadf700, 0xff9def00, 0xff84df00, 0xff73d600, 0xff5ac500,
        0xff4abd00, 0xff31ad00, 0xff21a500, 0xff189400, 0xff088c00, 0xff007b00, 0xffffffff, 0xffd6d6de,
        0xffb5b5bd, 0xff848c94, 0xff636b73, 0xffb5b5bd];

    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    internal static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? values = null;
        string? previous = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } input)
        {
            var line = input.Split(';', 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out values)) sections[name] = values = new(StringComparer.OrdinalIgnoreCase);
                previous = null;
            }
            else if (values != null)
            {
                int equals = line.IndexOf('=');
                if (equals > 0)
                {
                    previous = line[..equals].Trim();
                    values[previous] = line[(equals + 1)..].Trim();
                }
                else if (previous != null && previous.Equals("PointList", StringComparison.OrdinalIgnoreCase))
                    values[previous] += " " + line;
            }
        }
        return sections;
    }

    internal static PlaylistStyle ReadPlaylist(string text)
    {
        var defaults = PlaylistStyle.Default;
        if (!ParseIni(text).TryGetValue("Text", out var values)) return defaults;
        uint Color(string name, uint fallback)
        {
            string? s = values.GetValueOrDefault(name)?.TrimStart('#');
            return s is { Length: >= 6 } && uint.TryParse(s.AsSpan(0, 6), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint rgb) ? rgb | 0xff000000u : fallback;
        }

        string? font = values.GetValueOrDefault("Font")?.Trim('"', '\'');
        return new(Color("Normal", defaults.Normal), Color("Current", defaults.Current),
            Color("NormalBG", defaults.NormalBG), Color("SelectedBG", defaults.SelectedBG),
            string.IsNullOrWhiteSpace(font) ? defaults.Font : font,
            Color("MbFG", defaults.MiniBrowserFG), Color("MbBG", defaults.MiniBrowserBG));
    }

    internal static uint[] ReadColors(string text)
    {
        uint[] colors = (uint[])DefaultColors.Clone();
        using var reader = new StringReader(text);
        int i = 0;
        while (i < colors.Length && reader.ReadLine() is { } line)
        {
            string[] fields = line.Split(['/', ';'], 2)[0].Split(',');
            if (fields.Length < 3 || !byte.TryParse(fields[0], out byte r) ||
                !byte.TryParse(fields[1], out byte g) || !byte.TryParse(fields[2], out byte b)) continue;
            colors[i++] = 0xff000000u | (uint)(r << 16 | g << 8 | b);
        }
        return colors;
    }

    internal static Dictionary<string, SkinPoint[][]> ReadRegions(string text)
    {
        var regions = new Dictionary<string, SkinPoint[][]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in ParseIni(text))
        {
            if (!values.TryGetValue("NumPoints", out var sizes) || !values.TryGetValue("PointList", out var coords)) continue;
            var counts = ParseInts(sizes);
            var points = ParseInts(coords);
            if (counts.Length == 0 || counts.Any(n => n is < 3 or > 8192) || counts.Sum(n => (long)n) * 2 != points.Length) continue;
            var polygons = new SkinPoint[counts.Length][];
            int p = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                var polygon = polygons[i] = new SkinPoint[counts[i]];
                for (int j = 0; j < polygon.Length; j++) polygon[j] = new(points[p++], points[p++]);
            }
            regions[name] = polygons;
        }
        return regions;
    }

    private static int[] ParseInts(string text)
    {
        var fields = text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int[] numbers = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            if (!int.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]) ||
                numbers[i] is < -32768 or > 32767) return [];
        return numbers;
    }
}
