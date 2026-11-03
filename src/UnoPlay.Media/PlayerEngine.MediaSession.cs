#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;
#endif

namespace UnoPlay.Media;

public enum MediaTransportCommand { Play, Pause, Stop, Previous, Next }

public sealed partial class PlayerEngine
{
    /// <summary>Transport requests from system controls such as a headset or the browser's media overlay.</summary>
    public event EventHandler<MediaTransportCommand>? TransportRequested;

    /// <summary>Updates now-playing metadata. Null clears the media session metadata.</summary>
    public void SetNowPlaying(string? title, string? artist = null, string? album = null)
    {
#if UNOPLAY_BROWSER
        BrowserNative.Metadata(_id, title ?? "", artist ?? "", album ?? "");
#endif
    }

    /// <summary>Yields the browser media controls to another playback provider.</summary>
    public void ReleaseMediaSession()
    {
#if UNOPLAY_BROWSER
        BrowserNative.MediaSession(_id, false);
#endif
    }

    /// <summary>Reinstalls local transport handlers after another playback provider has used them.</summary>
    public void ReclaimMediaSession()
    {
#if UNOPLAY_BROWSER
        BrowserNative.MediaSession(_id, true);
#endif
    }

    private async Task DispatchTransportAsync(MediaTransportCommand command)
    {
        try
        {
            if (TransportRequested is { } handler) handler(this, command);
            else if (command == MediaTransportCommand.Play) await PlayAsync();
            else if (command == MediaTransportCommand.Pause) Pause();
            else if (command == MediaTransportCommand.Stop) Stop();
        }
        catch (Exception error) { if (!_disposed) RaiseFailed(error.Message); }
    }
}

#if UNOPLAY_BROWSER
internal static partial class BrowserNative
{
    [JSImport(Api + "metadata")] internal static partial void Metadata(string id, string title, string artist, string album);
    [JSImport(Api + "mediaSession")] internal static partial void MediaSession(string id, bool active);
}
#endif
