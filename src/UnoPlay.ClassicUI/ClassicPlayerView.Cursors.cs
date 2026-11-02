#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;
#else
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;
#endif
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
#if UNOPLAY_BROWSER
    private static int _nextCursorOwner;
    private readonly int _cursorOwner = Interlocked.Increment(ref _nextCursorOwner);
#else
    private IDesktopSkinCursors? _nativeCursors;
    private bool _cursorBackendUnavailable;
#endif
    private readonly Dictionary<string, nint> _cursorHandles = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeCursors()
    {
        Unloaded += (_, _) => ResetCursors();
        for (var i = 0; i < _panels.Length; i++)
        {
            var id = (PanelId)i;
            var panel = _panels[i];
            void Update(object sender, PointerRoutedEventArgs args)
            {
                if (!_skin.HasCursors || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
                var point = args.GetCurrentPoint(panel).Position;
                if (!panel.AcceptsPoint(point)) { HideSkinCursor(); return; }
                string normal = id switch
                {
                    PanelId.Equalizer => "eqnormal",
                    PanelId.Playlist => _shaded[2] ? "pwsnorm" : "pnormal",
                    _ => _shaded[0] ? "wsnormal" : "normal"
                };
                string role = CursorRole(id, (int)(point.X / panel.Zoom), (int)(point.Y / panel.Zoom)) ?? normal;
                nint handle = CursorHandle(role);
                if (handle == 0 && role != normal) handle = CursorHandle(normal);
                if (handle == 0 && normal != "normal") handle = CursorHandle("normal");
                if (handle == 0) HideSkinCursor();
                else
#if UNOPLAY_BROWSER
                    BrowserCursors.Show(_cursorOwner, (int)handle);
#else
                    _nativeCursors?.Show(handle);
#endif
            }
            panel.AddHandler(PointerEnteredEvent, new PointerEventHandler(Update), true);
            panel.AddHandler(PointerMovedEvent, new PointerEventHandler(Update), true);
            panel.PointerExited += (_, _) => { if (_cursorHandles.Count > 0) HideSkinCursor(); };
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

    private nint CursorHandle(string name)
    {
        if (_cursorHandles.TryGetValue(name, out nint handle)) return handle;
        var bytes = _skin.GetCursor(name);
        if (bytes.IsEmpty) return _cursorHandles[name] = 0;
#if UNOPLAY_BROWSER
        return _cursorHandles[name] = BrowserCursors.Register(bytes.ToArray());
#else
        if (_cursorBackendUnavailable) return 0;
        try
        {
            if (_nativeCursors == null)
            {
                var window = Uno.UI.ApplicationHelper.Windows.FirstOrDefault(w => w.Content?.XamlRoot == XamlRoot);
                if (window is null) return 0;
                _nativeCursors = window.GetNativeWindow() switch
                {
                    X11NativeWindow x when OperatingSystem.IsLinux() => X11SkinCursors.Open(x.WindowId),
                    Win32NativeWindow w when OperatingSystem.IsWindows() => new Win32SkinCursors(w.Hwnd),
                    _ => null
                };
                if (_nativeCursors == null) { _cursorBackendUnavailable = true; return 0; }
            }
            return _cursorHandles[name] = _nativeCursors.Load(bytes);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (error is DllNotFoundException or EntryPointNotFoundException) _cursorBackendUnavailable = true;
            return _cursorHandles[name] = 0;
        }
#endif
    }

    private void HideSkinCursor()
    {
#if UNOPLAY_BROWSER
        BrowserCursors.Hide(_cursorOwner);
#else
        _nativeCursors?.Hide();
#endif
    }

    private void ResetCursors()
    {
        if (_cursorHandles.Count > 0)
        {
            HideSkinCursor();
            foreach (nint cursor in _cursorHandles.Values)
            {
                if (cursor == 0) continue;
#if UNOPLAY_BROWSER
                BrowserCursors.Release((int)cursor);
#else
                _nativeCursors?.Release(cursor);
#endif
            }
            _cursorHandles.Clear();
        }
#if !UNOPLAY_BROWSER
        _nativeCursors?.Dispose();
        _nativeCursors = null;
        _cursorBackendUnavailable = false;
#endif
    }
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
