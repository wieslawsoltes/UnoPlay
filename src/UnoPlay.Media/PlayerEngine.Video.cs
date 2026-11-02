#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;
#endif

namespace UnoPlay.Media;

public sealed partial class PlayerEngine
{
    /// <summary>Intrinsic display dimensions when reported by the live transport, or zero if unavailable.</summary>
#if UNOPLAY_BROWSER
    public int VideoWidth => (int)BrowserNative.State(_id, "video");
    public int VideoHeight => (int)BrowserNative.State(_id, "videoHeight");
    public bool IsVideoFullscreen => BrowserNative.State(_id, "fullscreen") != 0;
    public Task ToggleVideoFullscreenAsync() => BrowserNative.ToggleFullscreen(_id);
#else
    // The Skia media plugins currently do not implement PlaybackSession.NaturalVideoWidth/Height.
    // The metadata library can independently recover the dimensions from a file's track header.
    public int VideoWidth => 0;
    public int VideoHeight => 0;
    public bool IsVideoFullscreen => _element?.IsFullWindow == true;
    public Task ToggleVideoFullscreenAsync()
    {
        if (!HasVideo || _element is null) throw new InvalidOperationException("The current source has no video.");
        bool enter = !_element.IsFullWindow;
        _element.AreTransportControlsEnabled = enter;
        _element.IsFullWindow = enter;
        RaiseStateChanged();
        return Task.CompletedTask;
    }
#endif
}
#if UNOPLAY_BROWSER
internal static partial class BrowserNative
{
    [JSImport(Api + "toggleFullscreen")] internal static partial Task ToggleFullscreen(string id);
}
#endif
