#if !UNOPLAY_BROWSER
using System.Runtime.InteropServices;

namespace UnoPlay.ClassicUI;

internal sealed class Win32SkinCursors : IDesktopSkinCursors
{
    private readonly nint _window;
    private readonly WindowProc _proc;
    private readonly nuint _subclass;
    private nint _current;
    private bool _installed;
    private static int _nextSubclass;

    public Win32SkinCursors(nint window)
    {
        _window = window;
        _proc = OnMessage;
        _subclass = (nuint)Interlocked.Increment(ref _nextSubclass);
    }

    public nint Load(ReadOnlyMemory<byte> bytes)
    {
        // LoadImage uses the OS cursor parser (including monochrome XOR and animated cursors).
        // LR_SHARED is intentionally absent so each returned handle has clear ownership.
        if (bytes.Length < 6) return 0;
        bool animated = bytes.Span.StartsWith("RIFF"u8);
        if (!animated && !bytes.Span.StartsWith(new byte[] {0, 0, 2, 0})) return 0;
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + (animated ? ".ani" : ".cur"));
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes.Span);
            return LoadImageW(0, path, 2, 0, 0, 0x10); // IMAGE_CURSOR, LR_LOADFROMFILE
        }
        finally { File.Delete(path); }
    }

    public void Show(nint cursor)
    {
        if (cursor == 0) { Hide(); return; }
        if (!_installed) _installed = SetWindowSubclass(_window, _proc, _subclass, 0);
        _current = cursor;
        SetCursor(cursor);
    }

    private nint OnMessage(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        if (message == 0x0020 && ((nuint)lparam & 0xffff) == 1 && _current != 0) // WM_SETCURSOR, HTCLIENT
        {
            SetCursor(_current);
            return 1;
        }
        if (message == 0x0082) { _installed = false; _current = 0; RemoveWindowSubclass(hwnd, _proc, id); }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }

    public void Hide()
    {
        if (_current != 0 && GetCursor() == _current) SetCursor(LoadCursorW(0, 32512)); // IDC_ARROW
        _current = 0;
        if (_installed) { RemoveWindowSubclass(_window, _proc, _subclass); _installed = false; }
    }
    public void Release(nint cursor) { if (cursor != 0) DestroyCursor(cursor); }
    public void Dispose() => Hide();

    private delegate nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, WindowProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, WindowProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint LoadImageW(nint instance, string path, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint GetCursor();
    [DllImport("user32.dll")] private static extern bool DestroyCursor(nint cursor);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint LoadCursorW(nint instance, nint systemResource);
}
#endif
