using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using UnoPlay.ClassicSkins;
using System.Runtime.InteropServices.WindowsRuntime;

namespace UnoPlay.ClassicUI;

/// <summary>Crops immutable sprite cells once per skin, sharing them across each live window.</summary>
internal sealed class SpriteAtlas(ClassicSkin skin)
{
    private readonly Dictionary<(string, int, int, int, int), WriteableBitmap?> _sprites = new();

    public WriteableBitmap? Cell(string sheet, int x, int y, int w, int h)
    {
        var key = (sheet, x, y, w, h);
        if (_sprites.TryGetValue(key, out var cached)) return cached;
        // Many skins deliberately end VOLUME/BALANCE before the optional thumb rows.
        // Those rows are transparent when absent, allowing the painted track to remain visible.
        bool optionalThumb = y == 422 && sheet is "balance" or "volume";
        var source = optionalThumb ? skin.GetBitmap(sheet) : skin.GetBitmap(sheet, x, y, w, h);
        if (source is null || x < 0 || y < 0 || x >= source.Width || y >= source.Height)
            return _sprites[key] = null;
        var result = new WriteableBitmap(w, h);
        using (var dst = result.PixelBuffer.AsStream())
        {
            var pixels = source.Bgra32.Span;
            int copyWidth = Math.Min(w, source.Width - x);
            int rows = Math.Min(h, source.Height - y);
            for (int row = 0; row < rows; row++)
            {
                dst.Write(pixels.Slice(((y + row) * source.Width + x) * 4, copyWidth * 4));
                if (copyWidth < w) dst.Seek((w - copyWidth) * 4, SeekOrigin.Current);
            }
        }
        result.Invalidate();
        return _sprites[key] = result;
    }
}

internal sealed class ClassicSurface : Grid
{
    internal readonly Canvas Art = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    internal readonly Canvas Targets = new() { Background = Brush(0x00000000) };
    private int _next;
    private readonly Dictionary<(WriteableBitmap, int, int), WriteableBitmap> _masked = new();
    private readonly Dictionary<(int, int, int, int), (WriteableBitmap Bitmap, byte[] Bytes)> _dynamic = new();
    private IReadOnlyList<SkinPoint[]>? _polygons;
    private RegionMask? _region;
    public SpriteAtlas Atlas { get; set; }
    public double Zoom { get; private set; } = 1;
    private int _logicalWidth, _logicalHeight;

    public ClassicSurface(SpriteAtlas atlas, string name)
    {
        Atlas = atlas;
        AutomationProperties.SetName(this, name);
        Children.Add(Art);
        Children.Add(Targets);
    }

    public static SolidColorBrush Brush(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    public void SetRegion(IReadOnlyList<SkinPoint[]> polygons)
    {
        if (ReferenceEquals(_polygons, polygons) &&
            (_region is null || (_region.Width == _logicalWidth && _region.Height == _logicalHeight))) return;
        _polygons = polygons;
        _region = polygons.Count == 0 ? null : new RegionMask(polygons, _logicalWidth, _logicalHeight);
        _masked.Clear();
        _dynamic.Clear();
    }

    public bool AcceptsPoint(Point point) =>
        _region?.Contains((int)Math.Floor(point.X / Zoom), (int)Math.Floor(point.Y / Zoom)) ?? true;

    public void Begin() => _next = 0;
    public void End()
    {
        for (var i = _next; i < Art.Children.Count; i++)
            Art.Children[i].Visibility = Visibility.Collapsed;
    }

    private T Element<T>() where T : FrameworkElement, new()
    {
        if (_next < Art.Children.Count && Art.Children[_next] is T existing)
        {
            _next++;
            existing.Visibility = Visibility.Visible;
            return existing;
        }
        if (_next < Art.Children.Count) Art.Children.RemoveAt(_next);
        var result = new T();
        Art.Children.Insert(_next++, result);
        return result;
    }

    public static void Place(FrameworkElement item, int x, int y, int w, int h)
    {
        if (Canvas.GetLeft(item) != x) Canvas.SetLeft(item, x);
        if (Canvas.GetTop(item) != y) Canvas.SetTop(item, y);
        if (item.Width != w) item.Width = w;
        if (item.Height != h) item.Height = h;
    }

    public void Sprite(string sheet, int sx, int sy, int w, int h, int x, int y)
    {
        var source = Atlas.Cell(sheet, sx, sy, w, h);
        int count = _region?.Count(x, y, w, h) ?? w * h;
        if (source is not null && count > 0 && count < w * h)
        {
            var key = (source, x, y);
            if (!_masked.TryGetValue(key, out var clipped))
            {
                clipped = new WriteableBitmap(w, h);
                WriteMasked(source, clipped, new byte[w * h * 4], x, y, w, h);
                _masked[key] = clipped;
            }
            source = clipped;
        }
        Present(count == 0 ? null : source, x, y, w, h);
    }

    public void Picture(WriteableBitmap? source, int x, int y, int w, int h)
    {
        int count = _region?.Count(x, y, w, h) ?? w * h;
        if (source is not null && count > 0 && count < w * h)
        {
            var key = (x, y, w, h);
            if (!_dynamic.TryGetValue(key, out var cached))
                _dynamic[key] = cached = (new WriteableBitmap(w, h), new byte[w * h * 4]);
            WriteMasked(source, cached.Bitmap, cached.Bytes, x, y, w, h);
            source = cached.Bitmap;
        }
        Present(count == 0 ? null : source, x, y, w, h);
    }

    private void WriteMasked(WriteableBitmap source, WriteableBitmap destination, byte[] bytes, int x, int y, int w, int h)
    {
        using (var input = source.PixelBuffer.AsStream()) input.ReadExactly(bytes);
        _region!.Apply(bytes, x, y, w, h);
        using (var output = destination.PixelBuffer.AsStream()) output.Write(bytes);
        destination.Invalidate();
    }

    private void Present(ImageSource? source, int x, int y, int w, int h)
    {
        var image = Element<Image>();
        if (!ReferenceEquals(image.Source, source)) image.Source = source;
        Place(image, x, y, w, h);
    }

    public void Fill(uint color, int x, int y, int w, int h)
    {
        var border = Element<Border>();
        if (border.Tag is not uint last || last != color)
        {
            border.Tag = color;
            border.Background = Brush(color);
        }
        Place(border, x, y, w, h);
    }

    public void Label(string value, string face, uint color, int x, int y, int w, int h)
    {
        var label = Element<TextBlock>();
        if (label.Tag is not string current || current != face)
        {
            label.Tag = face;
            label.FontFamily = new FontFamily(face);
            label.FontSize = 10;
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        var desired = Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        if (label.Foreground is not SolidColorBrush brush || brush.Color != desired)
            label.Foreground = new SolidColorBrush(desired);
        if (label.Text != value) label.Text = value;
        Place(label, x, y, w, h);
    }

    private static (int X, int Y) Glyph(char ch)
    {
        ch = char.ToUpperInvariant(ch);
        ch = ch switch
        {
            'À' or 'Á' or 'Â' or 'Æ' => 'A', 'Ç' => 'C', 'È' or 'É' or 'Ê' or 'Ë' => 'E',
            'Ì' or 'Í' or 'Î' or 'Ï' => 'I', 'Ñ' => 'N', 'Ò' or 'Ó' or 'Ô' => 'O',
            'Ù' or 'Ú' or 'Û' or 'Ü' => 'U', 'Ÿ' => 'Y', 'Ƒ' => 'F', '°' => '0',
            '~' => '^', (char)96 => (char)39, (char)1 => '…', _ => ch
        };
        if (ch is >= 'A' and <= 'Z') return ((ch - 'A') * 5, 0);
        if (ch == '"') return (130, 0);
        if (ch == '@') return (135, 0);
        if (ch == ' ') return (150, 0);
        const string row = "0123456789….:()-'!_+\\/[]^&%,=$#";
        int i = row.IndexOf(ch switch { '<' or '{' => '[', '>' or '}' => ']', _ => ch });
        if (i >= 0) return (i * 5, 6);
        i = "ÅÖÄ?*".IndexOf(ch);
        return i >= 0 ? (i * 5, 12) : (150, 0);
    }

    public void Text(string text, int x, int y, int cells, int start = 0)
    {
        for (int i = 0; i < cells; i++)
        {
            var glyph = Glyph(start + i < text.Length ? text[start + i] : ' ');
            Sprite("text", glyph.X, glyph.Y, 5, 6, x + i * 5, y);
        }
    }

    public void PlaceTarget(FrameworkElement target, int x, int y, int w, int h)
    {
        var rect = new Rect(x, y, w, h);
        target.Tag = rect;
        PositionTarget(target, rect);
    }

    private void PositionTarget(FrameworkElement target, Rect rect)
    {
        int x = (int)Math.Round(rect.X * Zoom);
        int y = (int)Math.Round(rect.Y * Zoom);
        Place(target, x, y, (int)Math.Round(rect.Right * Zoom)-x, (int)Math.Round(rect.Bottom * Zoom)-y);
    }

    public void SetZoom(double scale)
    {
        if (Zoom == scale) return;
        Zoom = scale;
        // Keep input controls in viewport DIPs. Bitmap zoom must never change the input
        // coordinate system that ButtonBase and the browser use for pointer capture.
        Art.RenderTransform = scale == 1 ? null : new ScaleTransform { ScaleX = scale, ScaleY = scale };
        DefineSize(_logicalWidth, _logicalHeight);
        foreach (var child in Targets.Children.OfType<FrameworkElement>())
            if (child.Tag is Rect rect) PositionTarget(child, rect);
    }
    public void DefineSize(int w, int h)
    {
        _logicalWidth = w; _logicalHeight = h;
        Art.Width = w; Art.Height = h;
        Width = w * Zoom; Height = h * Zoom;
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) };
    }
}
