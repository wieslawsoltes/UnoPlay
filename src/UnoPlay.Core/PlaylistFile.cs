using System.Globalization;
using System.Text;

namespace UnoPlay.Core;

public static class PlaylistFile
{
    public static IReadOnlyList<Track> Read(string text, string extension, Uri? baseUri = null)
    {
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        return extension.TrimStart('.').Equals("pls", StringComparison.OrdinalIgnoreCase)
            ? ReadPls(reader, baseUri) : ReadM3u(reader, baseUri);
    }

    private static List<Track> ReadM3u(StringReader reader, Uri? baseUri)
    {
        List<Track> result = [];
        string? title = null;
        TimeSpan? duration = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Open the HLS manifest as a stream URL.");
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = line.IndexOf(',');
                title = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                duration = Seconds(comma >= 0 ? line[8..comma] : line[8..]);
            }
            else if (line.Length != 0 && line[0] != '#')
            {
                result.Add(Create(line, title, duration, baseUri));
                title = null;
                duration = null;
            }
        }
        return result;
    }

    private static List<Track> ReadPls(StringReader reader, Uri? baseUri)
    {
        var entries = new SortedDictionary<int, Dictionary<string,string>>();
        while (reader.ReadLine() is { } line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            int digit = key.AsSpan().IndexOfAnyInRange('0', '9');
            if (digit < 1 || !int.TryParse(key.AsSpan(digit), out int n) || n < 1) continue;
            var field = key[..digit].ToLowerInvariant();
            if (field is not ("file" or "length" or "title")) continue;
            if (!entries.TryGetValue(n, out var entry)) entries[n] = entry = [];
            entry[field] = line[(eq + 1)..].Trim();
        }
        return entries.Values.Where(e => !string.IsNullOrWhiteSpace(e.GetValueOrDefault("file")))
            .Select(e => Create(e["file"], e.GetValueOrDefault("title"), Seconds(e.GetValueOrDefault("length")), baseUri)).ToList();
    }

    private static Track Create(string location, string? title, TimeSpan? duration, Uri? baseUri)
    {
        if (baseUri is not null && Uri.TryCreate(baseUri, location, out var resolved)) location = resolved.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(title))
        {
            var path = Uri.TryCreate(location, UriKind.Absolute, out var uri) ? uri.LocalPath : location;
            title = Uri.UnescapeDataString(path.Replace('\\', '/').Split('/')[^1]);
            if (title.Length == 0) title = location;
        }
        return new Track(location, title, duration);
    }

    private static TimeSpan? Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;

    public static string WriteM3u(IEnumerable<Track> tracks)
    {
        var result = new StringBuilder("#EXTM3U\n");
        foreach (var track in tracks)
        {
            if (track.Location.IndexOfAny(['\r', '\n']) >= 0) throw new FormatException("Playlist paths must fit on one line.");
            var seconds = track.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "-1";
            result.Append("#EXTINF:").Append(seconds).Append(',').Append(track.Title.ReplaceLineEndings(" "))
                .Append('\n').Append(track.Location).Append('\n');
        }
        return result.ToString();
    }
}
