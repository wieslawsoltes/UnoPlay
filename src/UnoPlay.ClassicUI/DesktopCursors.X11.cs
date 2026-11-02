#if !UNOPLAY_BROWSER
using System.Runtime.InteropServices;
using SkiaSharp;
using UnoPlay.ClassicSkins;

namespace UnoPlay.ClassicUI;

internal sealed class X11SkinCursors(nint display, nuint root) : IDesktopSkinCursors
{
    private readonly nint _connection = XGetXCBConnection(display);
    private uint _target;
    private nuint _shown;
    private const string X = "libX11.so.6", C = "libXcursor.so.1";

    public static X11SkinCursors? Open(nint root)
    {
        nint connection = XOpenDisplay(0);
        if (connection == 0) return null;
        try { return new(connection, (nuint)root); }
        catch { XCloseDisplay(connection); throw; }
    }

    public nint Load(ReadOnlyMemory<byte> data)
    {
        var cursor = SkinCursor.Decode(data);
        nint images = XcursorImagesCreate(cursor.Steps.Count);
        if (images == 0) return 0;
        try
        {
            nint slots = Marshal.ReadIntPtr(images, IntPtr.Size);
            // Xcursor's ownership count is incremented only after each successful allocation.
            long pixels = 0;
            foreach (var step in cursor.Steps)
            {
                var frame = cursor.Frames[step.Frame];
                pixels += frame.Width * frame.Height;
                if (pixels > 4 * 1024 * 1024) return 0;
                using var encoded = SKData.CreateCopy(frame.Icon.Span);
                using var codec = SKCodec.Create(encoded);
                if (codec == null || codec.Info.Width != frame.Width || codec.Info.Height != frame.Height) return 0;
                nint image = XcursorImageCreate(frame.Width, frame.Height);
                if (image == 0) return 0;
                int index = Marshal.ReadInt32(images);
                Marshal.WriteIntPtr(slots, IntPtr.Size * index, image);
                Marshal.WriteInt32(images, index + 1);
                Marshal.WriteInt32(image, 16, frame.HotX);
                Marshal.WriteInt32(image, 20, frame.HotY);
                Marshal.WriteInt32(image, 24, step.DelayMilliseconds);
                nint buffer = Marshal.ReadIntPtr(image, IntPtr.Size == 8 ? 32 : 28);
                var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                if (codec.GetPixels(info, buffer) != SKCodecResult.Success) return 0;
            }
            var handle = XcursorImagesLoadCursor(display, images);
            XFlush(display);
            return (nint)handle;
        }
        finally { XcursorImagesDestroy(images); }
    }

    public void Show(nint cursor)
    {
        if (cursor == 0) { Hide(); return; }
        // Uno's rendering/input surface is a child of the public root window. Query the
        // pointer only inside our own root, never adopt another application's XID.
        if (_target == 0)
        {
            // Checked XCB operations avoid Xlib's process-wide BadWindow handler if the
            // renderer or native host disappears while the pointer leaves the window.
            var reply = xcb_query_pointer_reply(_connection, xcb_query_pointer(_connection, (uint)root), out var error);
            if (error != 0) free(error);
            if (reply == 0) return;
            uint child = (uint)Marshal.ReadInt32(reply, 12);
            free(reply);
            _target = child == 0 ? (uint)root : child;
        }
        if (_shown == (nuint)cursor) return;
        if (SetWindowCursor((uint)cursor)) _shown = (nuint)cursor;
        else _target = 0;
    }

    public void Hide()
    {
        if (_target == 0) return;
        SetWindowCursor(0);
        _shown = _target = 0;
    }

    private bool SetWindowCursor(uint cursor)
    {
        const uint cursorAttribute = 1u << 14;
        var cookie = xcb_change_window_attributes_checked(_connection, _target, cursorAttribute, ref cursor);
        nint error = xcb_request_check(_connection, cookie);
        if (error == 0) return true;
        free(error);
        return false;
    }

    public void Release(nint cursor) { if (cursor != 0) XFreeCursor(display, (nuint)cursor); }
    public void Dispose() => XCloseDisplay(display);
    [DllImport(X)] private static extern nint XOpenDisplay(nint name);
    [DllImport(X)] private static extern int XFlush(nint display);
    [DllImport(X)] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11-xcb.so.1")] private static extern nint XGetXCBConnection(nint display);
    [DllImport("libxcb.so.1")] private static extern uint xcb_query_pointer(nint connection, uint window);
    [DllImport("libxcb.so.1")] private static extern nint xcb_query_pointer_reply(nint connection, uint cookie, out nint error);
    [DllImport("libxcb.so.1")] private static extern uint xcb_change_window_attributes_checked(nint connection, uint window, uint mask, ref uint value);
    [DllImport("libxcb.so.1")] private static extern nint xcb_request_check(nint connection, uint cookie);
    [DllImport("libc")] private static extern void free(nint allocation);
    [DllImport(X)] private static extern int XFreeCursor(nint display, nuint cursor);
    [DllImport(C)] private static extern nint XcursorImagesCreate(int size);
    [DllImport(C)] private static extern nint XcursorImageCreate(int width, int height);
    [DllImport(C)] private static extern void XcursorImagesDestroy(nint images);
    [DllImport(C)] private static extern nuint XcursorImagesLoadCursor(nint display, nint images);
}
#endif
