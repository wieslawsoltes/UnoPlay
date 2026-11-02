#if !UNOPLAY_BROWSER
using LibVLCSharp.Shared;
using SoundFlow.Abstracts;
using SoundFlow.Structs;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace UnoPlay.Media;

// libVLC handles ICY/HTTP transport, demux, codec, seeking and network clock.
// The bounded queue joins the decoder output to the very same DSP/device graph
// used by files. The audio-device callback never waits for the network.
internal sealed class VlcAudioTrack : SoundComponent
{
    private readonly object _gate = new();
    private readonly short[] _ring;
    private readonly LibVLC _vlc;
    private readonly LibVLCSharp.Shared.MediaPlayer _decoder;
    private int _head, _count;
    private bool _closed, _paused;

    public VlcAudioTrack(AudioEngine engine, AudioFormat format, Uri uri) : base(engine, format)
    {
        _ring = new short[format.SampleRate * format.Channels / 4]; // at most 250 ms
        _vlc = new LibVLC("--aout=amem", "--no-video", "--no-video-title-show");
        try
        {
            _decoder = new LibVLCSharp.Shared.MediaPlayer(_vlc);
            try
            {
                // VLC 3.x's amem module delivers native-endian signed 16-bit PCM.
                _decoder.SetAudioFormat("S16N", (uint)format.SampleRate, (uint)format.Channels);
                _decoder.SetAudioCallbacks(Enqueue, SetPaused, SetResumed, Flush, Drain);
                _decoder.EndReached += OnEnded;
                _decoder.EncounteredError += OnError;
                _decoder.Playing += OnState;
                _decoder.Paused += OnState;
                using var media = new VlcMedia(_vlc, uri);
                _decoder.Media = media;
            }
            catch { _decoder.Dispose(); throw; }
        }
        catch { _vlc.Dispose(); throw; }
    }

    public event EventHandler? Ended, Failed, StateChanged;
    public bool Playing => _decoder.IsPlaying;
    public bool CanSeek => _decoder.IsSeekable;
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _decoder.Time));
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, _decoder.Length));

    public void Play()
    {
        if (!_decoder.Play()) throw new InvalidOperationException("The network audio could not be started.");
    }

    public void Pause() => _decoder.SetPause(true);
    public void Seek(TimeSpan position)
    {
        if (CanSeek) _decoder.Time = (long)Math.Clamp(position.TotalMilliseconds, 0, Math.Max(0, _decoder.Length));
    }

    private unsafe void Enqueue(IntPtr data, IntPtr samples, uint frames, long pts)
    {
        var input = new ReadOnlySpan<short>(samples.ToPointer(), checked((int)frames * Format.Channels));
        lock (_gate)
        {
            while (!input.IsEmpty && !_closed)
            {
                int free = _ring.Length - _count;
                if (free == 0) { Monitor.Wait(_gate); continue; }
                int tail = (_head + _count) % _ring.Length;
                int chunk = Math.Min(input.Length, Math.Min(_ring.Length - tail, free));
                input[..chunk].CopyTo(_ring.AsSpan(tail));
                _count += chunk;
                input = input[chunk..];
            }
        }
    }

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        lock (_gate)
        {
            int count = _paused ? 0 : Math.Min(buffer.Length, _count);
            int first = Math.Min(count, _ring.Length - _head);
            for (int i = 0; i < first; i++) buffer[i] = _ring[_head + i] / 32768f;
            for (int i = first; i < count; i++) buffer[i] = _ring[i - first] / 32768f;
            buffer[count..].Clear();
            _head = (_head + count) % _ring.Length;
            _count -= count;
            Monitor.PulseAll(_gate);
        }
    }

    private void SetPaused(IntPtr data, long pts) { lock (_gate) _paused = true; }
    private void SetResumed(IntPtr data, long pts) { lock (_gate) _paused = false; }
    private void Flush(IntPtr data, long pts)
    {
        lock (_gate) { _head = _count = 0; Monitor.PulseAll(_gate); }
    }
    private void Drain(IntPtr data)
    {
        lock (_gate) while (_count > 0 && !_closed) Monitor.Wait(_gate);
    }
    private void OnEnded(object? sender, EventArgs e) => Ended?.Invoke(this, e);
    private void OnError(object? sender, EventArgs e) => Failed?.Invoke(this, e);
    private void OnState(object? sender, EventArgs e) => StateChanged?.Invoke(this, e);

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Monitor.PulseAll(_gate);
        }
        _decoder.EndReached -= OnEnded;
        _decoder.EncounteredError -= OnError;
        _decoder.Playing -= OnState;
        _decoder.Paused -= OnState;
        _decoder.Stop();
        _decoder.Dispose();
        _vlc.Dispose();
        base.Dispose();
    }
}
#endif
