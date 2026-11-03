using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using UnoPlay.Spotify.Uno;

namespace UnoPlay.ClassicUI;

public sealed partial class ClassicPlayerView
{
    private readonly SpotifyConnection _spotify = new();
    private bool _spotifyActive, _spotifyInitialized, _sendingSpotifyControls;
    private readonly SemaphoreSlim _transportChange = new(1, 1);
    private DispatcherTimer? _spotifyControls;
    private double? _pendingSpotifyVolume;
    private TimeSpan? _pendingSpotifySeek;
    private Button? _spotifyNowPlaying;
    private Image? _spotifyNowArt;
    private TextBlock? _spotifyNowText;
    private string? _spotifyNowImage;

    private bool CurrentIsPlaying => _spotifyActive ? _spotify.Playback?.IsPlaying == true : Engine.IsPlaying;
    private TimeSpan CurrentPosition => _spotifyActive ? _spotify.Position : Engine.Position;
    private TimeSpan CurrentDuration => _spotifyActive ? _spotify.Duration : Engine.Duration;
    private bool CurrentCanSeek => _spotifyActive
        ? _spotify.Playback?.Track is not null && !_spotify.Playback.DisallowedActions.Contains("seeking")
        : Engine.CanSeek;
    private double CurrentVolume => _spotifyActive
        ? _pendingSpotifyVolume ?? (_spotify.Playback?.Device?.VolumePercent ?? 100) / 100d : Engine.Volume;
    private bool CurrentShuffle => _spotifyActive ? _spotify.Playback?.ShuffleState == true : Queue.Shuffle;
    private bool CurrentRepeat => _spotifyActive ? _spotify.Playback?.RepeatState is "track" or "context" : Queue.Repeat;
    private string CurrentTitle => _spotifyActive ? _spotify.Playback?.Track is { } track
        ? track.ArtistName + " – " + track.Name : "SPOTIFY" : _loaded is not null ? TitleOf(_loaded) : "UNOPLAY CLASSIC";

    private async Task InitializeSpotifyAsync()
    {
        if (_spotifyInitialized || _disposed) return;
        _spotifyInitialized = true;
        _spotify.Changed += SpotifyChanged;
        _spotify.Error += SpotifyError;
        await _spotify.InitializeAsync();
        if ((_spotify.IsSignedIn || _spotify.HadAuthorizationResponse) && !_disposed) ShowSpotify();
    }

    private void SpotifyChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        if (!_spotify.IsSignedIn && _spotifyActive)
        {
            _loadRequest++;
            _spotifyActive = false;
            Engine.ReclaimMediaSession();
        }
        if (_spotify.IsBrowserDevice && _spotify.Playback?.IsPlaying == true && !_spotifyActive)
        {
            _loadRequest++;
            SetSpotifyActive();
        }
        _spotify.IsMonitoring = _spotifyActive || _spotifyWindow?.Visibility == Visibility.Visible;
        UpdateSpotifyView();
        UpdateSpotifyAttribution();
        UpdateAnimationClock();
        ArrangeVideo();
        DrawAll();
    });

    private void SpotifyError(object? sender, string message) => OnUi(() => Notify(message));

    private void SetSpotifyActive()
    {
        if (_spotifyActive) return;
        Engine.Pause();
        Engine.ReleaseMediaSession();
        _spotifyActive = true;
        _spotify.IsMonitoring = true;
        _stopped = false;
        CancelProbe();
        UpdateSpotifyAttribution();
        ArrangeVideo();
        DrawAll();
    }

    private Task PlaySpotifyAsync(string? contextUri = null, IReadOnlyList<string>? uris = null, int? offset = null)
        => RunTransportChangeAsync(async generation =>
    {
        SetSpotifyActive();
        _stopped = false;
        await _spotify.PlayAsync(contextUri, uris, offset);
        if (_disposed || generation != _loadRequest) return;
        UpdateAnimationClock();
        DrawAll();
    });

    private Task SpotifyCommandAsync(Func<Task> command) => RunTransportChangeAsync(async generation =>
    {
        SetSpotifyActive();
        await command();
        if (_disposed || generation != _loadRequest) return;
        UpdateAnimationClock();
        DrawAll();
    });

    private async Task RunTransportChangeAsync(Func<long, Task> change)
    {
        long generation = ++_loadRequest;
        await _transportChange.WaitAsync();
        try
        {
            if (!_disposed && generation == _loadRequest) await change(generation);
        }
        finally { _transportChange.Release(); }
    }

    private async Task UseLocalTransportAsync(long generation)
    {
        if (_spotifyActive && _spotify.IsSignedIn && _spotify.Playback?.IsPlaying == true)
        {
            try { await _spotify.PauseAsync(); }
            catch (Exception error) { Notify(error.Message); }
        }
        if (_disposed || generation != _loadRequest) return;
        _spotifyActive = false;
        _spotify.DisconnectBrowserPlayer();
        _spotifyControls?.Stop();
        _pendingSpotifySeek = null;
        _pendingSpotifyVolume = null;
        UpdateSpotifyAttribution();
        Engine.ReclaimMediaSession();
    }

    private async Task StopSpotifyAsync()
    {
        await _spotify.PauseAsync();
        if (CurrentCanSeek) await _spotify.SeekAsync(TimeSpan.Zero);
        _stopped = true;
        DrawAll();
    }

    private void ToggleShuffle()
    {
        if (_spotifyActive) _ = Guard(() => _spotify.SetShuffleAsync(!CurrentShuffle));
        else { Queue.Shuffle = !Queue.Shuffle; Draw(PanelId.Main); }
    }

    private void ToggleRepeat()
    {
        if (_spotifyActive)
        {
            string state = _spotify.Playback?.RepeatState switch { "off" => "context", "context" => "track", _ => "off" };
            _ = Guard(() => _spotify.SetRepeatAsync(state));
        }
        else { Queue.Repeat = !Queue.Repeat; Draw(PanelId.Main); }
    }

    private void SeekCurrent(TimeSpan position)
    {
        if (!CurrentCanSeek) return;
        if (_spotifyActive)
        {
            _pendingSpotifySeek = position;
            ScheduleSpotifyControls();
        }
        else Engine.Seek(position);
    }

    private void ScheduleSpotifyControls()
    {
        if (_spotifyControls is null)
        {
            _spotifyControls = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _spotifyControls.Tick += (_, _) => _ = Guard(SendSpotifyControlsAsync);
        }
        _spotifyControls.Stop();
        _spotifyControls.Start();
    }

    private async Task SendSpotifyControlsAsync()
    {
        if (_sendingSpotifyControls) return;
        _spotifyControls!.Stop();
        if (!_spotifyActive || !_spotify.IsSignedIn) return;
        var volume = _pendingSpotifyVolume;
        var seek = _pendingSpotifySeek;
        _pendingSpotifyVolume = null;
        _pendingSpotifySeek = null;
        _sendingSpotifyControls = true;
        try
        {
            if (volume is { } level) await _spotify.SetVolumeAsync(level);
            if (seek is { } position && _spotifyActive) await _spotify.SeekAsync(position);
        }
        finally { _sendingSpotifyControls = false; }
    }

    private void UpdateSpotifyAttribution()
    {
        bool visible = _spotifyActive && _spotify.Playback?.Track is not null;
        if (_spotifyNowPlaying is null && visible)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            _spotifyNowArt = new Image { Width = 52, Height = 52, Stretch = Stretch.Uniform };
            row.Children.Add(_spotifyNowArt);
            var description = new StackPanel { Spacing = 4, Width = 200 };
            description.Children.Add(new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/spotify-logo.png")),
                Width = 72, Height = 22, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            _spotifyNowText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontSize = 11 };
            description.Children.Add(_spotifyNowText);
            row.Children.Add(description);
            _spotifyNowPlaying = new Button { Content = row, Padding = new Thickness(10), Background = ClassicSurface.Brush(0xff191414) };
            AutomationProperties.SetName(_spotifyNowPlaying, "Open Spotify library and now playing");
            _spotifyNowPlaying.Click += (_, _) => ShowSpotify();
            Canvas.SetLeft(_spotifyNowPlaying, 8);
            Canvas.SetTop(_spotifyNowPlaying, 8);
            Canvas.SetZIndex(_spotifyNowPlaying, 4);
            _desk.Children.Add(_spotifyNowPlaying);
        }
        if (_spotifyNowPlaying is null) return;
        _spotifyNowPlaying.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_spotify.Playback?.Track is not { } track) return;
        _spotifyNowText!.Text = track.Name + "\n" + track.ArtistName;
        ToolTipService.SetToolTip(_spotifyNowPlaying, track.Name + "\n" + track.ArtistName);
        if (_spotifyNowImage != track.ImageUrl)
        {
            _spotifyNowImage = track.ImageUrl;
            _spotifyNowArt!.Source = Uri.TryCreate(track.ImageUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                ? new BitmapImage(uri) : null;
        }
    }

    private void ShutdownSpotify()
    {
        CloseSpotifyView();
        _spotifyControls?.Stop();
        _spotify.Changed -= SpotifyChanged;
        _spotify.Error -= SpotifyError;
        _spotify.Dispose();
    }
}
