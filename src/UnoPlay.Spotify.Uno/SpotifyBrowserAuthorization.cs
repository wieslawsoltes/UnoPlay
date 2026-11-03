#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace UnoPlay.Spotify.Uno;

internal static partial class SpotifyBrowserAuthorization
{
    [JSImport("globalThis.UnoPlaySpotifyAuth.redirectUri")]
    internal static partial string RedirectUri();
    [JSImport("globalThis.UnoPlaySpotifyAuth.callback")]
    internal static partial string Callback();
    [JSImport("globalThis.UnoPlaySpotifyAuth.clearCallback")]
    internal static partial void ClearCallback();
    [JSImport("globalThis.UnoPlaySpotifyAuth.read")]
    internal static partial string Read(string key);
    [JSImport("globalThis.UnoPlaySpotifyAuth.write")]
    internal static partial void Write(string key, string value);
    [JSImport("globalThis.UnoPlaySpotifyAuth.authorize")]
    internal static partial void Authorize(string url);
}
#endif
