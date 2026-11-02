using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UnoPlay.ClassicSkins;
using Windows.Foundation;

namespace UnoPlay.ClassicUI;

public enum ClassicWindowStyle
{
    General, MiniBrowser
}
public enum MiniBrowserCommand
{
    Back, Forward, Stop, Refresh, Menu
}

/// <summary>Hosts an arbitrary Uno view inside the original GEN.BMP or MB.BMP window chrome.</summary>
public sealed class ClassicWindowFrame : Grid
{
    private readonly ClassicSurface _frame;
    private readonly Grid _client = new();
    private readonly ClassicWindowStyle _style;
    private readonly string _title;
    private ClassicSkin _skin;
    private (int X, int W)[][] _letters = [[], []];
    private int _pressed = -1;
    private bool _active;
    public event EventHandler? CloseRequested;
    public event EventHandler? FocusRequested;
    public event EventHandler<MiniBrowserCommand>? BrowserCommand;
    public FrameworkElement? Client
    {
        get => _client.Children.OfType<FrameworkElement>().FirstOrDefault();
        set
        {
            _client.Children.Clear();
            if (value is not null)
                _client.Children.Add(value);
        }
    }

    public ClassicWindowFrame(ClassicSkin skin, ClassicWindowStyle style, string title)
    {
        _skin = skin;
        _style = style;
        _title = title;
        _frame = new ClassicSurface(new SpriteAtlas(skin), title + " chrome");
        Children.Add(_frame);
        _client.Margin = new Thickness(11, 20, 8, BottomHeight);
        Children.Add(_client);
        AutomationProperties.SetName(this, title);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => FocusRequested?.Invoke(this, EventArgs.Empty)), true);
        SetSkin(skin);
        SetSize(410, 280);
    }
    private int BottomHeight => _style == ClassicWindowStyle.MiniBrowser ? 38 : 14;

    public void SetSkin(ClassicSkin skin)
    {
        _skin = skin;
        _frame.Atlas = new SpriteAtlas(skin);
        _client.Background = ClassicSurface.Brush(skin.Playlist.NormalBG);
        _letters = [FindLetters(skin, 88), FindLetters(skin, 96)];
        Draw();
    }
    private static (int, int)[] FindLetters(ClassicSkin skin, int row)
    {
        var bmp = skin.GetBitmap("gen");
        if (bmp is null || row + 7 > bmp.Height)
            return [];
        var pixels = bmp.Bgra32.Span.Slice(row * bmp.Width * 4, bmp.Width * 4);
        var value = new (int, int)[26];
        int x = 1;
        for (int i = 0; i < value.Length && x < bmp.Width; i++)
        {
            int start = x;
            while (x < bmp.Width && !pixels.Slice(x * 4, 4).SequenceEqual(pixels[..4]))
                x++;
            value[i] = (start, x - start);
            x++;
        }
        return value;
    }
    public void SetActive(bool active)
    {
        if (_active != active)
        {
            _active = active;
            Draw();
        }
    }
    public void SetSize(int width, int height)
    {
        Width = Math.Clamp(width, 250, 2400);
        Height = Math.Clamp(height, 116, 1800);
        _frame.DefineSize((int)Width, (int)Height);
        BuildTargets();
        Draw();
    }
    private void Draw()
    {
        if (!double.IsFinite(Width) || !double.IsFinite(Height))
            return;
        int w = (int)Width, h = (int)Height, row = _active ? 0 : 21;
        _frame.Begin();
        var sheet = _style == ClassicWindowStyle.General ? "gen" : "mb";
        int side = _style == ClassicWindowStyle.General ? 104 : 127;
        for (int x = 25; x < w - 25; x += 25)
            _frame.Sprite(sheet, side, row, Math.Min(25, w - 25 - x), 20, x, 0);
        _frame.Sprite(sheet, 0, row, 25, 20, 0, 0);
        if (_style == ClassicWindowStyle.General)
        {
            _frame.Sprite(sheet, 130, row, 25, 20, w - 25, 0);
            DrawTitle(w, row);
        }
        else
        {
            _frame.Sprite(sheet, 26, row, 100, 20, (w - 100) / 2, 0);
            _frame.Sprite(sheet, 153, row, 25, 20, w - 25, 0);
        }
        if (_pressed == 5)
            _frame.Sprite(sheet, 148, 42, 9, 9, w - 11, 3);
        for (int y = 20; y < h - BottomHeight; y += 29)
        {
            int tile = Math.Min(29, h - BottomHeight - y);
            _frame.Sprite(sheet, 127, 42, 11, tile, 0, y);
            _frame.Sprite(sheet, 139, 42, 8, tile, w - 8, y);
        }
        if (_style == ClassicWindowStyle.General)
        {
            _frame.Sprite(sheet, 158, 42, 11, 24, 0, h - 38);
            _frame.Sprite(sheet, 170, 42, 8, 24, w - 8, h - 38);
        }
        for (int x = 125; x < w - 125; x += 25)
            _frame.Sprite(sheet, 127, _style == ClassicWindowStyle.General ? 72 : 81, Math.Min(25, w - 125 - x), BottomHeight, x, h - BottomHeight);
        _frame.Sprite(sheet, 0, 42, 125, BottomHeight, 0, h - BottomHeight);
        _frame.Sprite(sheet, 0, _style == ClassicWindowStyle.General ? 57 : 81, 125, BottomHeight, w - 125, h - BottomHeight);
        if (_style == ClassicWindowStyle.MiniBrowser && _pressed is >= 0 and < 5)
            _frame.Sprite(sheet, 158 + _pressed * 15, 42, 15, 18, 9 + _pressed * 15, h - 29);
        _frame.End();
    }
    private void DrawTitle(int width, int row)
    {
        string text = _title.ToUpperInvariant();
        var letters = _letters[_active ? 0 : 1];
        int CharWidth(char c) => c is >= 'A' and <= 'Z' && letters.Length == 26 ? letters[c - 'A'].W : 5;
        int titleWidth = text.Sum(CharWidth) + 8, left = (width - titleWidth) / 2;
        _frame.Sprite("gen", 26, row, 25, 20, left - 25, 0);
        for (int x = 0; x < titleWidth; x += 25)
            _frame.Sprite("gen", 52, row, Math.Min(25, titleWidth - x), 20, left + x, 0);
        _frame.Sprite("gen", 78, row, 25, 20, left + titleWidth, 0);
        int pen = left + 4;
        if (letters.Length == 0 || letters.All(g => g.W == 0))
        {
            _frame.Text(text, pen, 4, text.Length);
            return;
        }
        foreach (char c in text)
        {
            if (c is >= 'A' and <= 'Z' && letters[c - 'A'] is var g && g.W > 0)
                _frame.Sprite("gen", g.X, _active ? 88 : 96, g.W, 7, pen, 3);
            pen += CharWidth(c);
        }
    }
    private void BuildTargets()
    {
        _frame.Targets.Children.Clear();
        int w = (int)Width, h = (int)Height;
        DragHandle(0, 0, w, 20, false);
        int first = _style == ClassicWindowStyle.MiniBrowser ? 0 : 5;
        for (int i = first; i <= 5; i++)
        {
            int index = i;
            string label = i == 5 ? "Close " + _title : ((MiniBrowserCommand)i).ToString();
            var b = new Button { Opacity = 0, Content = label, MinHeight = 0, MinWidth = 0 };
            AutomationProperties.SetName(b, label);
            ToolTipService.SetToolTip(b, label);
            ClassicSurface.Place(b, i == 5 ? w - 11 : 9 + i * 15, i == 5 ? 3 : h - 29, i == 5 ? 9 : 15, i == 5 ? 9 : 18);
            b.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => { _pressed = index; Draw(); }), true);
            b.PointerCaptureLost += (_, _) => { _pressed = -1; Draw(); };
            b.Click += (_, _) => { _pressed = -1; Draw(); if (index == 5) CloseRequested?.Invoke(this, EventArgs.Empty); else BrowserCommand?.Invoke(this, (MiniBrowserCommand)index); };
            _frame.Targets.Children.Add(b);
        }
        DragHandle(w - 16, h - 14, 16, 14, true);
    }
    private void DragHandle(int x, int y, int width, int height, bool resize)
    {
        var hit = new Border { Background = ClassicSurface.Brush(0) };
        AutomationProperties.SetName(hit, (resize ? "Resize " : "Move ") + _title);
        ClassicSurface.Place(hit, x, y, width, height);
        _frame.Targets.Children.Add(hit);
        bool dragging = false;
        Point start = default, origin = default;
        int oldW = 0, oldH = 0;
        hit.PointerPressed += (_, e) =>
        {
            if (Parent is not Canvas parent || !e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed)
                return;
            start = e.GetCurrentPoint(parent).Position;
            origin = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            oldW = (int)Width;
            oldH = (int)Height;
            dragging = hit.CapturePointer(e.Pointer);
        };
        hit.PointerMoved += (_, e) =>
        {
            if (!dragging || resize || Parent is not Canvas parent)
                return;
            var pt = e.GetCurrentPoint(parent).Position;
            Canvas.SetLeft(this, Math.Clamp(origin.X + pt.X - start.X, 0, Math.Max(0, parent.ActualWidth - 45)));
            Canvas.SetTop(this, Math.Clamp(origin.Y + pt.Y - start.Y, 0, Math.Max(0, parent.ActualHeight - 15)));
        };
        hit.PointerReleased += (_, e) =>
        {
            if (!dragging || Parent is not Canvas parent)
                return;
            var pt = e.GetCurrentPoint(parent).Position;
            hit.ReleasePointerCapture(e.Pointer);
            if (resize)
                SetSize((int)Math.Min(parent.ActualWidth, oldW + pt.X - start.X), (int)Math.Min(parent.ActualHeight, oldH + pt.Y - start.Y));
        };
        hit.PointerCaptureLost += (_, _) => dragging = false;
    }
}
