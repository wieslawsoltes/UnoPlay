namespace UnoPlay.ClassicSkins;

public enum SpectrumStyle { Normal, Fire, Line }
public enum ScopeStyle { Dots, Lines, Solid }

/// <summary>Stateful classic visualization rasterizer. Samples and BGRA output are supplied by the caller.</summary>
public sealed class ClassicAnalyzer
{
    private readonly double[] _levels;
    private readonly double[] _peaks;
    private readonly double[] _holdUntil;
    private double _previousTime;
    public int Width { get; }
    public int Height { get; }

    public ClassicAnalyzer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        _levels = new double[width];
        _peaks = new double[width];
        _holdUntil = new double[width];
    }

    public void Render(Span<byte> bgra, IReadOnlyList<uint> colors, ReadOnlySpan<byte> samples,
        bool spectrum, double seconds, SpectrumStyle bars, ScopeStyle scope, bool thin, bool peaks, double falloff)
    {
        if (bgra.Length < checked(Width * Height * 4)) throw new ArgumentException("Output buffer is too small.", nameof(bgra));
        if (colors.Count < 24) throw new ArgumentException("VISColor requires 24 colors.", nameof(colors));
        double elapsed = Math.Clamp(seconds - _previousTime, 0, 1);
        _previousTime = seconds;
        int group = thin ? 1 : Width >= 75 ? 4 : 2;
        int mid = Height / 2, last = mid;
        for (int x = 0; x < Width; x++)
        {
            int start = spectrum ? x - x % group : x;
            int index = start * samples.Length / Width;
            int value = samples.IsEmpty ? 0 : samples[index];
            int wave = Math.Clamp((255 - value) * Height / 256, 0, Height - 1);
            if (spectrum && x == start)
            {
                int end = Math.Min(samples.Length, (start + group) * samples.Length / Width);
                for (int i = index + 1; i < end; i++) value = Math.Max(value, samples[i]);
                _levels[start] = Math.Max(value * Height / 255d, _levels[start] - elapsed * Height * falloff);
                if (samples.IsEmpty) { _levels[start] = _peaks[start] = 0; }
                if (_levels[start] >= _peaks[start])
                {
                    _peaks[start] = _levels[start];
                    _holdUntil[start] = seconds + .2;
                }
                else if (seconds > _holdUntil[start])
                    _peaks[start] = Math.Max(_levels[start], _peaks[start] - elapsed * Height * falloff * .6);
            }
            int level = (int)Math.Round(_levels[start]);
            int top = Height - level;
            int high = Height - (int)Math.Ceiling(_peaks[start]);
            for (int y = 0; y < Height; y++)
            {
                int color = (x ^ y) & 1;
                if (spectrum && (thin || x % group != group - 1) && !samples.IsEmpty)
                {
                    if (y >= top && (bars != SpectrumStyle.Line || y == top))
                        color = 2 + (bars == SpectrumStyle.Fire ? Math.Min(15, (y - top) * 16 / Height) : y * 16 / Height);
                    if (peaks && y == high && _peaks[start] > 0) color = 23;
                }
                else if (!spectrum && !samples.IsEmpty)
                {
                    int other = scope switch { ScopeStyle.Solid => mid, ScopeStyle.Dots => wave, _ => last };
                    if (y >= Math.Min(wave, other) && y <= Math.Max(wave, other))
                        color = 18 + Math.Min(4, Math.Abs(y - mid) * 5 / Math.Max(1, mid));
                }
                uint pixel = colors[color];
                int offset = (y * Width + x) * 4;
                bgra[offset] = (byte)pixel;
                bgra[offset + 1] = (byte)(pixel >> 8);
                bgra[offset + 2] = (byte)(pixel >> 16);
                bgra[offset + 3] = (byte)(pixel >> 24);
            }
            last = wave;
        }
    }
}
