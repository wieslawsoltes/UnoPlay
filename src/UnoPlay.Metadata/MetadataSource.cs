using System.Net;
using System.Net.Http.Headers;

namespace UnoPlay.Metadata;

public static class MetadataSource
{
    public static async Task<MediaMetadata> FromStreamAsync(Stream stream, CancellationToken token = default)
    {
        byte[] prefix = await ReadWindow(stream, token);
        byte[] tail = [];
        if (stream.CanSeek && stream.Length > MetadataReader.ProbeSize && IsMp4(prefix))
        {
            stream.Position = Math.Max(MetadataReader.ProbeSize, stream.Length - MetadataReader.ProbeSize);
            tail = await ReadWindow(stream, token);
        }
        return MetadataReader.Read(prefix, tail);
    }

    public static async Task<MediaMetadata> FromUriAsync(HttpClient client, Uri source, CancellationToken token = default)
    {
        using var head = await Send(client, source, new RangeHeaderValue(0, MetadataReader.ProbeSize - 1), token);
        head.EnsureSuccessStatusCode();
        using var stream = await head.Content.ReadAsStreamAsync(token);
        byte[] prefix = await ReadWindow(stream, token);
        if (!IsMp4(prefix) || head.StatusCode != HttpStatusCode.PartialContent ||
            head.Content.Headers.ContentRange?.Length is not long length || length <= prefix.Length)
            return MetadataReader.Read(prefix);
        using var end = await Send(client, source, new RangeHeaderValue(Math.Max(prefix.Length, length - MetadataReader.ProbeSize), length - 1), token);
        if (end.StatusCode != HttpStatusCode.PartialContent) return MetadataReader.Read(prefix);
        using var tailStream = await end.Content.ReadAsStreamAsync(token);
        byte[] tail = await ReadWindow(tailStream, token);
        return MetadataReader.Read(prefix, tail);
    }

    private static bool IsMp4(byte[] data) => data.Length >= 12 && data.AsSpan(4,4).SequenceEqual("ftyp"u8);
    private static async Task<byte[]> ReadWindow(Stream stream, CancellationToken token)
    {
        int requested = stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position, 0L, (long)MetadataReader.ProbeSize) : MetadataReader.ProbeSize;
        var bytes = new byte[requested];
        int count = await stream.ReadAtLeastAsync(bytes, requested, throwOnEndOfStream: false, cancellationToken: token);
        if (count != requested) Array.Resize(ref bytes, count);
        return bytes;
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, Uri source, RangeHeaderValue range, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = range;
        request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
}
