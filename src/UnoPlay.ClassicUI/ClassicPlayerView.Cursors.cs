#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
#endif

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
#if UNOPLAY_BROWSER
    private static int _nextCursorOwner;
    private readonly int _cursorOwner = Interlocked.Increment(ref _nextCursorOwner);
    private readonly Dictionary<string, int> _cursorHandles = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeCursors()
    {
        for (var i = 0; i < _panels.Length; i++)
        {
            var id = (PanelId)i;
            var panel = _panels[i];
            void Update(object sender, PointerRoutedEventArgs args)
            {
                if (!_skin.HasCursors || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
                var point = args.GetCurrentPoint(panel).Position;
                if (!panel.AcceptsPoint(point)) { BrowserCursors.Hide(_cursorOwner); return; }
                string normal = id switch
                {
                    PanelId.Equalizer => "eqnormal",
                    PanelId.Playlist => _shaded[2] ? "pwsnorm" : "pnormal",
                    _ => _shaded[0] ? "wsnormal" : "normal"
                };
                string role = CursorRole(id, (int)point.X, (int)point.Y) ?? normal;
                int handle = CursorHandle(role);
                if (handle == 0 && role != normal) handle = CursorHandle(normal);
                if (handle == 0 && normal != "normal") handle = CursorHandle("normal");
                if (handle == 0) BrowserCursors.Hide(_cursorOwner);
                else BrowserCursors.Show(_cursorOwner, handle);
            }
            panel.AddHandler(PointerEnteredEvent, new PointerEventHandler(Update), true);
            panel.AddHandler(PointerMovedEvent, new PointerEventHandler(Update), true);
            panel.PointerExited += (_, _) => { if (_cursorHandles.Count > 0) BrowserCursors.Hide(_cursorOwner); };
        }
    }

    private string? CursorRole(PanelId id, int x, int y)
    {
        if (id == PanelId.Playlist)
        {
            int w = _playlistWidth;
            if (y < 14 && x >= w - 14) return "pclose";
            if (y < 14 && x >= w - 25) return "pwinbut";
            if (_shaded[2]) return x >= w - 3 ? "pwssize" : null;
            if (y < 20) return "ptbar";
            if (y >= _playlistHeight - 20 && x >= w - 20) return "psize";
            if (x >= w - 20 && y < _playlistHeight - 38) return "pvscroll";
            return null;
        }
        if (id == PanelId.Equalizer)
        {
            if (y < 14 && x >= 263) return "eqclose";
            if (_shaded[1] && x is >= 60 and < 207 && y is >= 3 and < 12) return "eqslid";
            if (y < 14) return "eqtitle";
            return y is >= 38 and < 101 && (x is >= 21 and < 35 || x is >= 78 and < 254) ? "eqslid" : null;
        }
        if (y < 14)
        {
            if (x >= 264) return _shaded[0] ? "wsclose" : "close";
            if (x >= 254) return _shaded[0] ? "wswinbut" : "winbut";
            if (x >= 244) return _shaded[0] ? "wsmin" : "min";
            if (x < 16) return "mainmenu";
            if (_shaded[0] && x is >= 226 and < 243) return "wsposbar";
            return "titlebar";
        }
        if (x is >= 108 and < 266 && y is >= 22 and < 37) return "songname";
        if (x is >= 107 and < 216 && y is >= 57 and < 70) return "volbal";
        if (x is >= 16 and < 264 && y is >= 72 and < 82) return "posbar";
        return null;
    }

    private int CursorHandle(string name)
    {
        if (_cursorHandles.TryGetValue(name, out int handle)) return handle;
        var bytes = _skin.GetCursor(name);
        return _cursorHandles[name] = bytes.IsEmpty ? 0 : BrowserCursors.Register(bytes.ToArray());
    }

    private void ResetCursors()
    {
        if (_cursorHandles.Count == 0) return;
        BrowserCursors.Hide(_cursorOwner);
        foreach (int id in _cursorHandles.Values) if (id != 0) BrowserCursors.Release(id);
        _cursorHandles.Clear();
    }
#else
    private void InitializeCursors() { }
    private void ResetCursors() { }
#endif
}
#if UNOPLAY_BROWSER
internal static partial class BrowserCursors
{
    private const string Api = "globalThis.UnoPlaySkinCursors.";
    [JSImport(Api + "register")] internal static partial int Register(byte[] data);
    [JSImport(Api + "show")] internal static partial void Show(int instance, int handle);
    [JSImport(Api + "hide")] internal static partial void Hide(int instance);
    [JSImport(Api + "release")] internal static partial void Release(int handle);
}
#endif
