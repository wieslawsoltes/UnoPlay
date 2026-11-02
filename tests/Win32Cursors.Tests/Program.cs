using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UnoPlay.ClassicUI;

if (!OperatingSystem.IsWindows()) return;
nint window = CreateWindowExW(0, "STATIC", "UnoPlay cursor test", 0x80000000, 0, 0, 48, 48, 0, 0, 0, 0);
if (window == 0) throw new InvalidOperationException("Could not create test HWND.");
var still = Cursor(0x61b238);
var next = Cursor(0xc75b26);
try
{
    foreach (var encoded in new[] { still, Animation(still, next) })
    {
        using var host = new Win32SkinCursors(window);
        nint loaded = host.Load(encoded);
        if (loaded == 0) throw new InvalidDataException("Windows refused the cursor.");
        try
        {
            host.Show(loaded);
            if (GetCursor() != loaded || SendMessageW(window, 0x0020, window, 1) != 1)
                throw new Exception("WM_SETCURSOR handling failed.");
            if (!GetIconInfo(loaded, out var info)) throw new Exception("GetIconInfo failed.");
            try { if (info.IsIcon != 0 || info.X != 2 || info.Y != 3) throw new Exception("Skin hotspot lost."); }
            finally { if (info.Mask != 0) DeleteObject(info.Mask); if (info.Color != 0) DeleteObject(info.Color); }
            host.Hide();
            if (GetCursor() == loaded) throw new Exception("Skin cursor still selected after exit.");
        }
        finally { host.Release(loaded); }
    }
}
finally { DestroyWindow(window); }
Console.WriteLine("Win32 loaded native CUR and animated ANI, preserved hotspots, handled WM_SETCURSOR, and restored the system cursor.");

static byte[] Cursor(uint rgb)
{
    const int width = 16, xorBytes = width * width * 4, maskBytes = width * 4;
    using var output = new MemoryStream(); using var w = new BinaryWriter(output);
    w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);
    w.Write((byte)width); w.Write((byte)width); w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)3);
    w.Write(40 + xorBytes + maskBytes); w.Write(22);
    w.Write(40); w.Write(width); w.Write(width * 2); w.Write((ushort)1); w.Write((ushort)32);
    w.Write(0); w.Write(xorBytes + maskBytes); w.Write(new byte[16]);
    for (int i = 0; i < width * width; i++) w.Write(rgb | 0xff000000u);
    w.Write(new byte[maskBytes]);
    return output.ToArray();
}
static byte[] Chunk(ReadOnlySpan<byte> name, byte[] bytes)
{
    byte[] encoded = new byte[8 + bytes.Length + (bytes.Length & 1)];
    name.CopyTo(encoded); BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(4), bytes.Length);
    bytes.CopyTo(encoded, 8); return encoded;
}
static byte[] Animation(byte[] a, byte[] b)
{
    byte[] header = new byte[36];
    int[] fields = [36, 2, 2, 0, 0, 0, 0, 6, 1];
    for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(i * 4), fields[i]);
    return Chunk("RIFF"u8, [.. "ACON"u8, .. Chunk("anih"u8, header),
        .. Chunk("LIST"u8, [.. "fram"u8, .. Chunk("icon"u8, a), .. Chunk("icon"u8, b)])]);
}
[DllImport("user32", CharSet=CharSet.Unicode, ExactSpelling=true)] static extern nint CreateWindowExW(uint ex, string cls, string title, uint style,
    int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
[DllImport("user32")] static extern bool DestroyWindow(nint hwnd);
[DllImport("user32")] static extern nint GetCursor();
[DllImport("user32", ExactSpelling=true)] static extern nint SendMessageW(nint hwnd, uint msg, nint wparam, nint lparam);
[DllImport("user32")] static extern bool GetIconInfo(nint hicon, out IconInfo icon);
[DllImport("gdi32")] static extern bool DeleteObject(nint gdi);
[StructLayout(LayoutKind.Sequential)]
struct IconInfo { public int IsIcon; public uint X,Y; public nint Mask, Color; }
