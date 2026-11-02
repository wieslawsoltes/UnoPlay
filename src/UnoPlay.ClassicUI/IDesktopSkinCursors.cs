namespace UnoPlay.ClassicUI;

#if !UNOPLAY_BROWSER
internal interface IDesktopSkinCursors : IDisposable
{
    nint Load(ReadOnlyMemory<byte> data);
    void Show(nint cursor);
    void Hide();
    void Release(nint cursor);
}
#endif
