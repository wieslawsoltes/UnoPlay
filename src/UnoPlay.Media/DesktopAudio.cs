#if !UNOPLAY_BROWSER
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace UnoPlay.Media;

// Uno's native video transport owns its decoder and audio output. Local audio
// uses a PCM graph so the very same samples drive the EQ and visualization.
internal sealed class DesktopAudio : IDisposable
{
    private readonly MiniAudioEngine _engine = new();
    private readonly AudioPlaybackDevice _device;
    private SoundPlayer? _track;
    public ClassicSignal Signal { get; } = new(AudioFormat.DvdHq.SampleRate);

    public DesktopAudio()
    {
        try
        {
            _device = _engine.InitializePlaybackDevice(null, AudioFormat.DvdHq);
            // Neutralize the mixer's constant-power center attenuation. The
            // classic balance fader applies its own linear channel gains.
            _device.MasterMixer.Volume = MathF.Sqrt(2);
        }
        catch { _engine.Dispose(); throw; }
    }

    public bool Playing => _track?.State == PlaybackState.Playing;
    public TimeSpan Position => TimeSpan.FromSeconds(_track?.Time ?? 0);
    public TimeSpan Duration => TimeSpan.FromSeconds(_track?.Duration ?? 0);
    public event EventHandler? Ended;

    public void Open(string path)
    {
        Clear();
        var stream = File.OpenRead(path);
        try
        {
            var provider = new StreamDataProvider(_engine, _device.Format, stream);
            try { _track = new SoundPlayer(_engine, _device.Format, provider); }
            catch { provider.Dispose(); throw; }
            _track.Volume = MathF.Sqrt(2);
            _track.AddModifier(Signal);
            _track.PlaybackEnded += OnEnded;
            _device.MasterMixer.AddComponent(_track);
        }
        catch { stream.Dispose(); throw; }
    }

    public void Play()
    {
        if (_track == null || Playing) return;
        if (_track.Time >= _track.Duration) _track.Seek(TimeSpan.Zero);
        _track.Play();
        if (!_device.IsRunning) _device.Start();
    }

    public void Pause()
    {
        if (_device.IsRunning) _device.Stop();
        _track?.Pause();
    }

    public void Seek(TimeSpan position)
    {
        bool running = _device.IsRunning;
        if (running) _device.Stop();
        _track?.Seek(position);
        Signal.Reset();
        if (running) _device.Start();
    }

    public void Clear()
    {
        if (_device.IsRunning) _device.Stop();
        if (_track != null)
        {
            _track.PlaybackEnded -= OnEnded;
            _device.MasterMixer.RemoveComponent(_track);
            _track.Dispose();
            _track = null;
        }
        Signal.Reset();
    }

    private void OnEnded(object? sender, EventArgs args) => Ended?.Invoke(this, args);

    public void Dispose()
    {
        Clear();
        _device.Dispose();
        _engine.Dispose();
    }
}
#endif
