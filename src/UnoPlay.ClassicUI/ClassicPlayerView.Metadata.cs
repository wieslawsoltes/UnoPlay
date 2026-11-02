using UnoPlay.Core;
using UnoPlay.Metadata;
using Windows.Storage;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private static readonly HttpClient TagClient = new();
    private static readonly HashSet<string> ProbeExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".mp4", ".m4v", ".m4a", ".mov", ".aac", ".flac", ".wav" };
    private readonly Dictionary<Track, MediaMetadata> _tags = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _probeCancellation;
    private MediaMetadata? CurrentTags => _loaded is not null ? _tags.GetValueOrDefault(_loaded) : null;
    private string TitleOf(Track t) => _tags.GetValueOrDefault(t)?.DisplayTitle ?? t.Title;
    private void CancelProbe() { _probeCancellation?.Cancel(); _probeCancellation = null; }

    private async Task Probe(Track track)
    {
        CancelProbe();
        if (_tags.ContainsKey(track)) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _probeCancellation = cancel;
        try
        {
            MediaMetadata tags;
            if (track.Source is StorageFile file)
            {
                using var stream = await file.OpenStreamForReadAsync();
                tags = await MetadataSource.FromStreamAsync(stream, cancel.Token);
            }
            else if (Uri.TryCreate(track.Location, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile && !OperatingSystem.IsBrowser())
                {
                    using var stream = File.OpenRead(uri.LocalPath);
                    tags = await MetadataSource.FromStreamAsync(stream, cancel.Token);
                }
                else if (uri.Scheme == "blob" || uri.Scheme is "http" or "https" && ProbeExtensions.Contains(Path.GetExtension(uri.AbsolutePath)))
                    tags = await MetadataSource.FromUriAsync(TagClient, uri, cancel.Token);
                else return;
            }
            else return;
            if (cancel.IsCancellationRequested || _disposed) return;
            _tags[track] = tags;
            if (ReferenceEquals(_loaded, track)) { UpdateMediaSession(); Draw(PanelId.Main); Draw(PanelId.Playlist); }
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or NotSupportedException or UnauthorizedAccessException)
        { /* Playback proceeds for streams which do not grant metadata or byte-range access. */ }
        finally { if (ReferenceEquals(_probeCancellation, cancel)) _probeCancellation = null; }
    }

    private static string TechnicalInfo(MediaMetadata? tags)
    {
        if (tags is null) return "";
        var fields = new List<string>(4);
        if (tags.Codec is not null) fields.Add(tags.Codec);
        if (tags.KilobitsPerSecond > 0) fields.Add($"{tags.KilobitsPerSecond} kbps");
        if (tags.SampleRate > 0) fields.Add($"{tags.SampleRate:N0} Hz");
        if (tags.Channels > 0) fields.Add(tags.Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{tags.Channels} channels" });
        return fields.Count == 0 ? "" : "\n\n" + string.Join(" · ", fields);
    }
}
