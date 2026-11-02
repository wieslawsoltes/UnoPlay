namespace UnoPlay.ClassicSkins;

/// <summary>Rasterizes a REGION.TXT polygon set on the native skin pixel grid.</summary>
public sealed class RegionMask
{
    private readonly int[] coverage;
    public int Width { get; }
    public int Height { get; }

    public RegionMask(IReadOnlyList<SkinPoint[]> polygons, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)(width + 1L) * (height + 1L) > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        coverage = new int[(width + 1) * (height + 1)];

        // Build the horizontal coverage of each polygon. Pixels shared by multiple filled
        // polygons remain part of the union. Horizontal edges do not cross a scanline.
        var intersections = new List<double>();
        foreach (var polygon in polygons)
        {
            if (polygon.Length < 3) continue;
            int first = Math.Max(0, polygon.Min(p => p.Y));
            int last = Math.Min(height, polygon.Max(p => p.Y));
            for (int y = first; y < last; y++)
            {
                double scan = y + 0.5;
                intersections.Clear();
                SkinPoint a = polygon[^1];
                foreach (var b in polygon)
                {
                    if ((a.Y <= scan && b.Y > scan) || (b.Y <= scan && a.Y > scan))
                        intersections.Add(a.X + (scan - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    a = b;
                }
                intersections.Sort();
                for (int i = 1; i < intersections.Count; i += 2)
                {
                    int start = Math.Clamp((int)Math.Ceiling(intersections[i - 1] - 0.5), 0, width);
                    int end = Math.Clamp((int)Math.Ceiling(intersections[i] - 0.5), 0, width);
                    Array.Fill(coverage, 1, (y + 1) * (width + 1) + start + 1, end - start);
                }
            }
        }

        // Integral image gives constant-time rejection/acceptance of entire sprite rectangles.
        for (int y = 1; y <= height; y++)
        {
            int sum = 0, row = y * (width + 1);
            for (int x = 1; x <= width; x++)
            {
                sum += coverage[row + x];
                coverage[row + x] = coverage[row + x - width - 1] + sum;
            }
        }
    }

    public int Count(int x, int y, int width, int height)
    {
        int x1 = Math.Clamp(x, 0, Width), y1 = Math.Clamp(y, 0, Height);
        int x2 = (int)Math.Clamp((long)x + width, 0, Width), y2 = (int)Math.Clamp((long)y + height, 0, Height);
        if (x2 <= x1 || y2 <= y1) return 0;
        int top = y1 * (Width + 1), bottom = y2 * (Width + 1);
        return coverage[bottom + x2] - coverage[bottom + x1] - coverage[top + x2] + coverage[top + x1];
    }

    public bool Contains(int x, int y) => Count(x, y, 1, 1) != 0;

    public void Apply(Span<byte> bgra, int x, int y, int w, int h)
    {
        if (w < 0 || h < 0 || (long)w * h * 4 != bgra.Length)
            throw new ArgumentException("Pixel buffer length does not match the rectangle.", nameof(bgra));
        for (int row = 0; row < h; row++)
        {
            int count = Count(x, y + row, w, 1);
            if (count == w) continue;
            var line = bgra.Slice(row * w * 4, w * 4);
            if (count == 0) line.Clear();
            else
                for (int col = 0; col < w; col++)
                    if (!Contains(x + col, y + row)) line.Slice(col * 4, 4).Clear();
        }
    }
}
