using System.Net.Http;
using System.Text;

namespace UnoPlay.Core;

/// <summary>Resolves remote M3U and PLS entries relative to the final response URL after redirects.</summary>
public static class RemotePlaylist
{
    private const int MaxBytes = 4 * 1024 * 1024;

    public static async Task<IReadOnlyList<Track>?> TryReadAsync(HttpClient client, Uri location, CancellationToken token = default)
    {
        var extension = Path.GetExtension(location.AbsolutePath).ToLowerInvariant();
        if (extension is not (".m3u" or ".m3u8" or ".pls")) return null;
        using var response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Playlist exceeds 4 MiB.");
        using var source = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidDataException("Playlist exceeds 4 MiB.");
            output.Write(buffer, 0, count);
        }
        var text = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            if (line.TrimStart().StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase)) return null;
        var tracks = PlaylistFile.Read(text, extension, response.RequestMessage?.RequestUri ?? location);
        // Remote playlists must never grant access to local files or browser object URLs.
        return tracks.Where(t => Uri.TryCreate(t.Location, UriKind.Absolute, out var target)
            && target.Scheme is "http" or "https").ToArray();
    }
}
