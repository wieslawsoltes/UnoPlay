#if !UNOPLAY_BROWSER
using System.Numerics;
using SoundFlow.Abstracts;
using SoundFlow.Enums;
using SoundFlow.Utils;

namespace UnoPlay.Media;

/// <summary>One audio-thread writer. Filter state is private to each channel.
/// Spectrum and oscilloscope reads run on the UI thread, outside the audio deadline.</summary>
internal sealed class ClassicSignal : SoundModifier
{
    private static readonly int[] Frequencies = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];
    private const int WindowSize = 1024;
    private readonly BiquadFilter[][] _filters =
        [Frequencies.Select(_ => new BiquadFilter()).ToArray(), Frequencies.Select(_ => new BiquadFilter()).ToArray()];
    private readonly int _rate;
    private readonly float[] _ring = new float[WindowSize * 8];
    private readonly Complex[] _fft = new Complex[WindowSize];
    private readonly Complex[] _roots = new Complex[WindowSize / 2];
    private readonly double[] _window = new double[WindowSize];
    private readonly int[] _reversed = new int[WindowSize];
    private readonly byte[] _spectrum = new byte[75], _waveform = new byte[75];
    private volatile double[] _settings = new double[11];
    private double[]? _applied;
    private volatile float _volume = 1, _balance;
    private float _preamp = 1;
    private long _published;
    private long _lastFft = -1;
    private bool _flat;

    public ClassicSignal(int rate)
    {
        _rate = rate;
        for (var i = 0; i < WindowSize; i++)
        {
            _window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1));
            _reversed[i] = (_reversed[i >> 1] >> 1) | ((i & 1) * (WindowSize / 2));
            if (i < _roots.Length) _roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / WindowSize);
        }
    }

    public void Equalize(double[] settings) => _settings = settings;
    public void Volume(double value) => _volume = (float)value;
    public void Balance(double value) => _balance = (float)value;

    public override void Process(Span<float> buffer, int channels)
    {
        var settings = _settings;
        if (!ReferenceEquals(settings, _applied))
        {
            _preamp = MathF.Pow(10, (float)settings[0] / 20);
            _flat = true;
            for (int i = 0; i < Frequencies.Length; i++)
            {
                _flat &= settings[i + 1] == 0;
                foreach (var filters in _filters)
                    filters[i].Update(FilterType.Peaking, _rate, Frequencies[i], 1.4f, (float)settings[i + 1]);
            }
            _applied = settings;
        }
        var balance = _balance;
        var volume = _volume;
        var left = volume * (1 - Math.Max(0, balance));
        var right = volume * (1 + Math.Min(0, balance));
        long cursor = _published;
        for (int i = 0; i < buffer.Length; i += channels)
        {
            float l = ProcessSample(buffer[i], 0), r = channels > 1 ? ProcessSample(buffer[i + 1], 1) : l;
            _ring[(int)(cursor++ & (_ring.Length - 1))] = (l + r) * .5f;
            buffer[i] = l * left;
            if (channels > 1) buffer[i + 1] = r * right;
        }
        Volatile.Write(ref _published, cursor);
    }

    public override float ProcessSample(float sample, int channel)
    {
        sample *= _preamp;
        if (!_flat)
            foreach (var filter in _filters[channel]) sample = filter.Process(sample);
        return sample;
    }

    // Call only while the output device is stopped.
    public void Reset()
    {
        foreach (var filters in _filters) foreach (var filter in filters) filter.Reset();
        Array.Clear(_ring);
        _published = 0;
        _lastFft = -1;
    }

    public byte[] Waveform()
    {
        var start = Volatile.Read(ref _published) - WindowSize;
        for (int i = 0; i < _waveform.Length; i++)
        {
            int index = (int)((start + i * WindowSize / _waveform.Length) & (_ring.Length - 1));
            _waveform[i] = (byte)Math.Clamp(128 + _ring[index] * 127, 0, 255);
        }
        return _waveform;
    }

    public byte[] Spectrum()
    {
        var end = Volatile.Read(ref _published);
        if (end == _lastFft) return _spectrum;
        _lastFft = end;
        for (int i = 0; i < WindowSize; i++)
            _fft[_reversed[i]] = new Complex(_ring[(int)((end - WindowSize + i) & (_ring.Length - 1))] * _window[i], 0);
        for (int width = 2; width <= WindowSize; width *= 2)
        {
            int half = width / 2;
            for (int start = 0; start < WindowSize; start += width)
            for (int j = 0; j < half; j++)
            {
                var a = _fft[start + j];
                var b = _fft[start + j + half] * _roots[j * (WindowSize / width)];
                _fft[start + j] = a + b;
                _fft[start + j + half] = a - b;
            }
        }
        int from = 0;
        for (var i = 0; i < _spectrum.Length; i++)
        {
            int to = Math.Clamp((int)Math.Round(40 * Math.Pow(400, (i + 1d) / _spectrum.Length) * WindowSize / _rate), from + 1, WindowSize / 2);
            double peak = 0;
            for (int bin = from; bin < to; bin++) peak = Math.Max(peak, _fft[bin].Magnitude * 4 / WindowSize);
            _spectrum[i] = (byte)Math.Clamp((20 * Math.Log10(Math.Max(peak, 1e-8)) + 100) * (255d / 70), 0, 255);
            from = to;
        }
        return _spectrum;
    }
}
#endif
