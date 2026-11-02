#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace UnoPlay.ClassicUI;

internal static partial class BrowserSkinStore
{
    [JSImport("globalThis.UnoPlayStorage.load")] internal static partial Task<JSObject> Load();
    [JSImport("globalThis.UnoPlayStorage.bytes")] internal static partial byte[] Bytes(JSObject data);
    [JSImport("globalThis.UnoPlayStorage.save")] internal static partial Task Save(byte[] bytes);
    [JSImport("globalThis.UnoPlayStorage.clear")] internal static partial Task Clear();
}
#endif
