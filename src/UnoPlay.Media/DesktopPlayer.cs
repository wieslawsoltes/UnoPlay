#if !UNOPLAY_BROWSER
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace UnoPlay.Media;

public sealed partial class PlayerEngine
{
    private readonly ContentControl _host;
    private MediaPlayerElement? _element;
    private MediaPlayer? _player;
    private DesktopAudio? _audio;
    private bool _usingAudio;
    private double[] _gains = new double[11];
    private bool _hasVideo;

    public PlayerEngine()
    {
        View = _host = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
    }

    public bool IsPlaying => _usingAudio ? _audio!.Playing : _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool HasVideo => _hasVideo;
    public bool CanSeek => Duration > TimeSpan.Zero;
    public bool SupportsDsp => _usingAudio;
    public TimeSpan Position => _usingAudio ? _audio!.Position : _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _usingAudio ? _audio!.Duration : _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
    public byte[] GetSpectrum() => _usingAudio ? _audio!.Signal.Spectrum() : [];
    public byte[] GetWaveform() => _usingAudio ? _audio!.Signal.Waveform() : [];

    private void EnsureMediaElement()
    {
        if (_player != null) return;
        if (OperatingSystem.IsLinux())
        {
            if (!NativeLibrary.TryLoad("libvlc.so", out var handle))
                throw new InvalidOperationException("Install libvlc-dev and VLC playback plugins, then restart UnoPlay.");
            NativeLibrary.Free(handle);
        }
        var element = new MediaPlayerElement { AutoPlay = false, AreTransportControlsEnabled = false, Stretch = Stretch.Uniform };
        var player = new MediaPlayer { AutoPlay = false, Volume = _volume };
        element.SetMediaPlayer(player);
        player.MediaOpened += OnOpened;
        player.MediaEnded += OnEnded;
        player.MediaFailed += OnFailed;
        player.PlaybackSession.PlaybackStateChanged += OnStateChanged;
        _element = element;
        _player = player;
        _host.Content = element;
    }

    private void OpenUri(Uri source)
    {
        if (!source.IsAbsoluteUri)
            throw new ArgumentException("Desktop playback requires an absolute file or network URI.", nameof(source));
        _usingAudio = false;
        _hasVideo = false;
        _audio?.Clear();
        if (_element?.Source != null) _element.Source = null;
        var extension = Path.GetExtension(source.AbsolutePath).ToLowerInvariant();
        if (source.IsFile && extension is ".mp3" or ".wav" or ".flac")
        {
            try
            {
                if (_audio == null)
                {
                    _audio = new DesktopAudio();
                    _audio.Ended += OnAudioEnded;
                }
                _audio.Open(source.LocalPath);
                _audio.Signal.Equalize(_gains);
                _audio.Signal.Balance(_balance);
                _audio.Signal.Volume(_volume);
                _usingAudio = true;
                RaiseStateChanged();
                return;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
            {
                // A different system codec or device may still decode the source.
                System.Diagnostics.Trace.TraceWarning("PCM output unavailable: {0}", ex.Message);
            }
        }
        EnsureMediaElement();
        _hasVideo = extension is not (".mp3" or ".mp2" or ".flac" or ".wav" or ".m4a" or ".aac" or ".oga" or ".ogg" or ".opus" or ".aiff" or ".wma");
        _element!.Source = MediaSource.CreateFromUri(source);
    }

    private Task OpenFileAsync(StorageFile file, int generation)
    {
        OpenUri(new Uri(Path.GetFullPath(file.Path)));
        return Task.CompletedTask;
    }

    public static Task<IReadOnlyList<BrowserMediaFile>> PickBrowserFilesAsync() =>
        throw new PlatformNotSupportedException("Use a StorageFile picker on desktop.");

    public Task PlayAsync()
    {
        if (_usingAudio) { _audio!.Play(); RaiseStateChanged(); }
        else _player?.Play();
        return Task.CompletedTask;
    }

    public void Pause()
    {
        if (_usingAudio) { _audio!.Pause(); RaiseStateChanged(); }
        else _player?.Pause();
    }

    public void Seek(TimeSpan position)
    {
        if (!CanSeek) return;
        if (_usingAudio) _audio!.Seek(position);
        else _player!.PlaybackSession.Position = position;
    }

    private void ApplyVolume(double value)
    {
        _audio?.Signal.Volume(value);
        if (_player != null) _player.Volume = value;
    }

    private void ApplyBalance(double value) => _audio?.Signal.Balance(value);
    private void ApplyEqualizer(double[] values) { _gains = values; _audio?.Signal.Equalize(values); }

    private void OnAudioEnded(object? sender, EventArgs args)
    {
        int generation = _openGeneration;
        Dispatch(() => { if (_usingAudio && generation == _openGeneration) RaiseEnded(); });
    }

    private void OnOpened(MediaPlayer sender, object args) => Dispatch(() => { if (!_usingAudio) RaiseStateChanged(); });
    private void OnEnded(MediaPlayer sender, object args) => Dispatch(() => { if (!_usingAudio) RaiseEnded(); });
    private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Dispatch(() => { if (!_usingAudio) RaiseFailed(args.ErrorMessage); });
    private void OnStateChanged(MediaPlaybackSession sender, object args) => Dispatch(() => { if (!_usingAudio) RaiseStateChanged(); });
    private void Dispatch(Action update)
    {
        if (_host.DispatcherQueue.HasThreadAccess) { if (!_disposed) update(); }
        else _host.DispatcherQueue.TryEnqueue(() => { if (!_disposed) update(); });
    }

    private void DisposeTransport()
    {
        if (_audio != null)
        {
            _audio.Ended -= OnAudioEnded;
            _audio.Dispose();
        }
        if (_player != null)
        {
            _player.MediaOpened -= OnOpened;
            _player.MediaEnded -= OnEnded;
            _player.MediaFailed -= OnFailed;
            _player.PlaybackSession.PlaybackStateChanged -= OnStateChanged;
            _element!.Source = null;
            _player.Dispose();
        }
        _host.Content = null;
    }
}
#endif
