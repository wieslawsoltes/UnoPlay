#if !UNOPLAY_BROWSER
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace UnoPlay.Media;

public sealed partial class PlayerEngine
{
    private readonly MediaPlayerElement _element;
    private readonly MediaPlayer _player;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".flac", ".wav", ".m4a", ".aac", ".oga", ".ogg", ".opus", ".aiff", ".wma"
    };
    private bool _hasVideo;

    public PlayerEngine()
    {
        View = _element = new MediaPlayerElement
        {
            AutoPlay = false,
            AreTransportControlsEnabled = false,
            Stretch = Stretch.Uniform
        };
        _player = new MediaPlayer { AutoPlay = false };
        _element.SetMediaPlayer(_player);
        _player.MediaOpened += OnOpened;
        _player.MediaEnded += OnEnded;
        _player.MediaFailed += OnFailed;
        _player.PlaybackSession.PlaybackStateChanged += OnStateChanged;
    }

    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool HasVideo => _hasVideo;
    public bool CanSeek => Duration > TimeSpan.Zero;
    public bool SupportsDsp => false;
    public TimeSpan Position => _player.PlaybackSession.Position;
    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;
    public byte[] GetSpectrum() => Array.Empty<byte>();
    public byte[] GetWaveform() => Array.Empty<byte>();

    private void OpenUri(Uri source)
    {
        if (!source.IsAbsoluteUri)
            throw new ArgumentException("Desktop playback requires an absolute file or network URI.", nameof(source));
        _hasVideo = !AudioExtensions.Contains(Path.GetExtension(source.AbsolutePath));
        _element.Source = MediaSource.CreateFromUri(source);
    }

    private Task OpenFileAsync(StorageFile file, int generation)
    {
        OpenUri(new Uri(Path.GetFullPath(file.Path)));
        return Task.CompletedTask;
    }

    public static Task<IReadOnlyList<BrowserMediaFile>> PickBrowserFilesAsync() =>
        throw new PlatformNotSupportedException("Use a StorageFile picker on desktop.");

    public Task PlayAsync() { _player.Play(); return Task.CompletedTask; }
    public void Pause() => _player.Pause();
    public void Seek(TimeSpan position) { if (CanSeek) _player.PlaybackSession.Position = position; }
    private void ApplyVolume(double value) => _player.Volume = value;

    // Uno's desktop MediaPlayer does not expose PCM or implement AudioBalance
    // and AddAudioEffect. Hosts query SupportsDsp to gate those controls.
    private void ApplyBalance(double value) { }
    private void ApplyEqualizer(double[] values) { }

    private void OnOpened(MediaPlayer sender, object args) => Dispatch(RaiseStateChanged);
    private void OnEnded(MediaPlayer sender, object args) => Dispatch(RaiseEnded);
    private void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => Dispatch(() => RaiseFailed(args.ErrorMessage));
    private void OnStateChanged(MediaPlaybackSession sender, object args) => Dispatch(RaiseStateChanged);
    private void Dispatch(Action update)
    {
        if (_element.DispatcherQueue.HasThreadAccess) { if (!_disposed) update(); }
        else _element.DispatcherQueue.TryEnqueue(() => { if (!_disposed) update(); });
    }

    private void DisposeTransport()
    {
        _player.MediaOpened -= OnOpened;
        _player.MediaEnded -= OnEnded;
        _player.MediaFailed -= OnFailed;
        _player.PlaybackSession.PlaybackStateChanged -= OnStateChanged;
        _element.Source = null;
        _player.Dispose();
    }
}
#endif
