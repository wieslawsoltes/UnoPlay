using System.Diagnostics;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.System;

namespace UnoPlay.Spotify.Uno;

/// <summary>Uno host for PKCE authorization, the browser SDK and Spotify Connect transport.</summary>
public sealed class SpotifyConnection : IDisposable
{
    private const string ClientKey = "unoplay.spotify.clientId";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _signIn;
    private SpotifySession? _session;
    private SpotifyWebApi? _api;
    private Task? _initialization;
    private bool _disposed, _polling, _monitoring, _quotaBlocked;
    private long _playbackTimestamp;
    private int _generation;
    private long _playbackRevision;
#if UNOPLAY_BROWSER
    private SpotifyBrowserPlayer? _browser;
    private string? _browserDeviceId;
#endif

    public SpotifyConnection(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = http is null;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        if (ApplicationData.Current.LocalSettings.Values.TryGetValue(ClientKey, out var value) && value is string clientId)
            ClientId = clientId;
        _poll.Tick += PollPlayback;
    }

    public string ClientId { get; private set; } = "";
    public bool IsSignedIn => _session?.IsSignedIn == true;
    public SpotifyProfile? Profile { get; private set; }
    public SpotifyPlaybackState? Playback { get; private set; }
    public SpotifyWebApi Api => _api ?? throw new SpotifyAuthenticationException("Enter your Spotify app Client ID and sign in.");
    public string? SelectedDeviceId { get; private set; }
    public string Status { get; private set; } = "Connect your Spotify account.";
    public bool SupportsBrowserPlayer => OperatingSystem.IsBrowser();
    public bool IsBrowserDevice
    {
        get
        {
#if UNOPLAY_BROWSER
            return IsLocalDevice;
#else
            return false;
#endif
        }
    }
    public event EventHandler? Changed;
    public event EventHandler<string>? Error;

    public Uri RedirectUri
    {
        get
        {
#if UNOPLAY_BROWSER
            return new Uri(SpotifyBrowserAuthorization.RedirectUri());
#else
            return SpotifyDesktopAuthorization.RegisteredRedirectUri;
#endif
        }
    }

    public bool IsMonitoring
    {
        get => _monitoring;
        set { _monitoring = value; UpdatePolling(); }
    }

    public TimeSpan Position
    {
        get
        {
            if (Playback is not { } state) return TimeSpan.Zero;
            double elapsed = state.IsPlaying ? Stopwatch.GetElapsedTime(_playbackTimestamp).TotalMilliseconds : 0;
            return TimeSpan.FromMilliseconds(Math.Clamp(state.ProgressMs + elapsed, 0, state.Track?.DurationMs ?? int.MaxValue));
        }
    }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(Playback?.Track?.DurationMs ?? 0);

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ClientId))
            {
#if UNOPLAY_BROWSER
                if (SpotifyBrowserAuthorization.Callback().Length > 0)
                {
                    SpotifyBrowserAuthorization.ClearCallback();
                    throw new SpotifyAuthenticationException("Enter your Spotify Client ID and start sign-in again.");
                }
#endif
                return;
            }
            Configure(ClientId);
#if UNOPLAY_BROWSER
            string callback = SpotifyBrowserAuthorization.Callback();
            if (callback.Length > 0)
            {
                string pending = SpotifyBrowserAuthorization.Read("pending");
                SpotifyBrowserAuthorization.Write("pending", "");
                SpotifyBrowserAuthorization.ClearCallback();
                var request = pending.Length > 0 ? SpotifyJson.DeserializeAuthorization(pending) : null;
                if (request is null)
                    throw new SpotifyAuthenticationException("Spotify sign-in must finish in the same browser tab where it started.");
                await _session!.CompleteAuthorizationAsync(request, new Uri(callback), _lifetime.Token);
            }
            else
            {
                string saved = SpotifyBrowserAuthorization.Read("token");
                if (saved.Length > 0) _session!.Restore(SpotifyJson.DeserializeToken(saved));
            }
#endif
            if (IsSignedIn) await LoadAccountAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { Report(exception.Message); }
        finally { RaiseChanged(); }
    }

    private void Configure(string clientId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = new SpotifySession(_http, clientId.Trim());
        if (_session is not null) _session.TokenChanged -= TokenChanged;
#if UNOPLAY_BROWSER
        _browser?.Dispose();
        _browser = null;
        _browserDeviceId = null;
#endif
        _generation++;
        _quotaBlocked = false;
        _session = session;
        _session.TokenChanged += TokenChanged;
        _api = new SpotifyWebApi(_http, session);
        ClientId = session.ClientId;
        ApplicationData.Current.LocalSettings.Values[ClientKey] = ClientId;
        Profile = null;
        SelectedDeviceId = null;
        ApplyPlayback(null);
    }

    public async Task SignInAsync(string clientId, CancellationToken cancellationToken = default)
    {
        _signIn?.Cancel();
        _signIn?.Dispose();
        _signIn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        Configure(clientId);
        Status = "Complete sign-in in your browser.";
        RaiseChanged();
#if UNOPLAY_BROWSER
        SpotifyBrowserAuthorization.Write("token", "");
        var request = _session!.CreateAuthorization(RedirectUri);
        SpotifyBrowserAuthorization.Write("pending", SpotifyJson.SerializeAuthorization(request));
        SpotifyBrowserAuthorization.Authorize(request.AuthorizationUri.AbsoluteUri);
        await Task.CompletedTask;
#else
        int generation = _generation;
        var session = _session!;
        await SpotifyDesktopAuthorization.SignInAsync(session, uri => Launcher.LaunchUriAsync(uri).AsTask(), _signIn.Token);
        if (_disposed || generation != _generation) return;
        await LoadAccountAsync();
#endif
    }

    private async Task LoadAccountAsync()
    {
        int generation = _generation;
        var profile = await Api.GetProfileAsync(_lifetime.Token);
        if (_disposed || generation != _generation) return;
        Profile = profile;
        Status = "Signed in as " + profile.DisplayName + ".";
        RaiseChanged();
        UpdatePolling();
        if (SupportsBrowserPlayer)
        {
            try { await ConnectBrowserAsync(); }
            catch (Exception exception) { Report(exception.Message); }
        }
    }

    private void TokenChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (!ReferenceEquals(sender, _session)) return;
#if UNOPLAY_BROWSER
        try
        {
            SpotifyBrowserAuthorization.Write("token", _session?.Token is { } token ? SpotifyJson.SerializeToken(token) : "");
        }
        catch (Exception) { Status = "Browser session storage is unavailable. Sign in again after reloading."; }
#endif
        if (!IsSignedIn)
        {
            _generation++;
            Profile = null;
            SelectedDeviceId = null;
#if UNOPLAY_BROWSER
            _browser?.Dispose();
            _browser = null;
            _browserDeviceId = null;
#endif
            ApplyPlayback(null);
        }
        UpdatePolling();
        RaiseChanged();
    });

    public Task SignOutAsync()
    {
        _generation++;
        _signIn?.Cancel();
#if UNOPLAY_BROWSER
        _browser?.Dispose();
        _browser = null;
        _browserDeviceId = null;
        try
        {
            SpotifyBrowserAuthorization.Write("pending", "");
            SpotifyBrowserAuthorization.Write("token", "");
        }
        catch (Exception) { /* The in-memory session still ends if the browser blocks storage. */ }
#endif
        _session?.SignOut();
        Profile = null;
        SelectedDeviceId = null;
        ApplyPlayback(null);
        Status = "Signed out of Spotify.";
        UpdatePolling();
        RaiseChanged();
        return Task.CompletedTask;
    }

    public async Task ConnectBrowserAsync()
    {
        RequireAccount();
#if UNOPLAY_BROWSER
        if (_browser is null)
        {
            var created = new SpotifyBrowserPlayer(_session!.GetAccessTokenAsync);
            _browser = created;
            created.PlaybackChanged += (_, state) =>
            {
                if (ReferenceEquals(_browser, created) && SelectedDeviceId == _browserDeviceId) ApplyPlayback(state);
            };
            created.Failed += (_, message) => { if (ReferenceEquals(_browser, created)) Report(message); };
            created.AutoplayFailed += (_, _) =>
            {
                if (ReferenceEquals(_browser, created)) Report("Press Play to allow Spotify audio in this browser.");
            };
            created.DeviceChanged += (_, _) =>
            {
                if (!ReferenceEquals(_browser, created)) return;
                if (created.DeviceId is null && SelectedDeviceId == _browserDeviceId)
                {
                    SelectedDeviceId = null;
                    ApplyPlayback(null);
                }
                _browserDeviceId = created.DeviceId;
                RaiseChanged();
            };
        }
        int generation = _generation;
        var player = _browser;
        await player.ConnectAsync(_lifetime.Token);
        if (_disposed || generation != _generation) return;
        SelectedDeviceId = player.DeviceId;
        _browserDeviceId = player.DeviceId;
        Status = "UnoPlay browser player is ready. Select music and press Play.";
        RaiseChanged();
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Choose a Spotify Connect device for desktop playback.");
#endif
    }

    public Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync() => Api.GetDevicesAsync(_lifetime.Token);

    public void DisconnectBrowserPlayer()
    {
#if UNOPLAY_BROWSER
        _browser?.Disconnect();
#endif
    }

    public async Task SelectDeviceAsync(string deviceId, bool play = false)
    {
        RequireAccount();
        int generation = _generation;
#if UNOPLAY_BROWSER
        if (deviceId == _browser?.DeviceId) await _browser.ActivateElementAsync();
#endif
        await Api.TransferPlaybackAsync(deviceId, play, _lifetime.Token);
        if (_disposed || generation != _generation) return;
        SelectedDeviceId = deviceId;
        await RefreshPlaybackAsync();
        RaiseChanged();
    }

    public async Task RefreshPlaybackAsync()
    {
        if (!IsSignedIn || _disposed) return;
        int generation = _generation;
        long revision = _playbackRevision;
        string? deviceId = SelectedDeviceId;
        SpotifyPlaybackState? state;
#if UNOPLAY_BROWSER
        if (IsLocalDevice)
            state = await _browser!.GetCurrentStateAsync();
        else
#endif
            state = await Api.GetPlaybackAsync(_lifetime.Token);
        if (!_disposed && generation == _generation && revision == _playbackRevision && deviceId == SelectedDeviceId)
            ApplyPlayback(state);
    }

    public async Task PlayAsync(string? contextUri = null, IReadOnlyList<string>? uris = null, int? offset = null)
    {
        RequireAccount();
#if UNOPLAY_BROWSER
        if (SelectedDeviceId is null) await ConnectBrowserAsync();
        if (IsLocalDevice) await _browser!.ActivateElementAsync();
#endif
        await Api.PlayAsync(SelectedDeviceId, contextUri, uris, offset, cancellationToken: _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task ResumeAsync()
    {
        RequireAction("resuming");
#if UNOPLAY_BROWSER
        if (SelectedDeviceId is null) await ConnectBrowserAsync();
        if (IsLocalDevice)
        {
            await _browser!.ActivateElementAsync();
            if (_browser.CurrentState is not null)
            {
                await _browser.ResumeAsync();
                return;
            }
        }
#endif
        await Api.PlayAsync(SelectedDeviceId, cancellationToken: _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task PauseAsync()
    {
        RequireAction("pausing");
#if UNOPLAY_BROWSER
        if (IsLocalDevice) { await _browser!.PauseAsync(); return; }
#endif
        await Api.PauseAsync(SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task PreviousAsync()
    {
        RequireAction("skipping_prev");
#if UNOPLAY_BROWSER
        if (IsLocalDevice) { await _browser!.PreviousAsync(); return; }
#endif
        await Api.PreviousAsync(SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task NextAsync()
    {
        RequireAction("skipping_next");
#if UNOPLAY_BROWSER
        if (IsLocalDevice) { await _browser!.NextAsync(); return; }
#endif
        await Api.NextAsync(SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        RequireAction("seeking");
        int milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, Playback?.Track?.DurationMs ?? int.MaxValue);
#if UNOPLAY_BROWSER
        if (IsLocalDevice) { await _browser!.SeekAsync(TimeSpan.FromMilliseconds(milliseconds)); return; }
#endif
        await Api.SeekAsync(milliseconds, SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task SetVolumeAsync(double volume)
    {
        RequireAccount();
        if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        volume = Math.Clamp(volume, 0, 1);
#if UNOPLAY_BROWSER
        if (IsLocalDevice) { await _browser!.SetVolumeAsync(volume); await RefreshPlaybackAsync(); return; }
#endif
        if (Playback?.Device is { SupportsVolume: false })
            throw new InvalidOperationException("Adjust volume on the selected Spotify device.");
        await Api.SetVolumeAsync((int)Math.Round(volume * 100), SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task SetShuffleAsync(bool enabled)
    {
        RequireAction("toggling_shuffle");
        await Api.SetShuffleAsync(enabled, SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public async Task SetRepeatAsync(string state)
    {
        RequireAction(state == "track" ? "toggling_repeat_track" : "toggling_repeat_context");
        await Api.SetRepeatAsync(state, SelectedDeviceId, _lifetime.Token);
        await RefreshPlaybackAsync();
    }

    public Task AddToQueueAsync(string uri) => Api.AddToQueueAsync(uri, SelectedDeviceId, _lifetime.Token);

#if UNOPLAY_BROWSER
    private bool IsLocalDevice => _browser?.Ready == true && SelectedDeviceId == _browser.DeviceId;
#endif

    private void RequireAccount()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsSignedIn) throw new SpotifyAuthenticationException("Sign in to Spotify to continue.");
    }

    private void RequireAction(string action)
    {
        RequireAccount();
        if (Playback?.DisallowedActions.Contains(action) == true)
            throw new InvalidOperationException("Spotify has restricted this action for the current playback.");
    }

    private void ApplyPlayback(SpotifyPlaybackState? state)
    {
        _playbackRevision++;
        Playback = state;
        _playbackTimestamp = Stopwatch.GetTimestamp();
        RaiseChanged();
    }

    private async void PollPlayback(object? sender, object e)
    {
        if (_polling || !IsSignedIn || !_monitoring || _quotaBlocked) return;
        _polling = true;
        try { await RefreshPlaybackAsync(); _poll.Interval = TimeSpan.FromSeconds(5); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (SpotifyApiException exception)
        {
            _poll.Interval = exception.RetryAfter is { } retry ? TimeSpan.FromSeconds(Math.Max(5, retry.TotalSeconds)) : TimeSpan.FromSeconds(30);
            if (exception.IsQuotaExceeded) { _quotaBlocked = true; _poll.Stop(); }
            Report(exception.Message);
        }
        catch (Exception exception) { Report(exception.Message); }
        finally { _polling = false; }
    }

    private void UpdatePolling()
    {
        if (!_disposed && IsSignedIn && _monitoring && !_quotaBlocked) _poll.Start();
        else _poll.Stop();
    }

    private void Report(string message)
    {
        if (_disposed) return;
        Status = message;
        Error?.Invoke(this, message);
        RaiseChanged();
    }

    private void RaiseChanged() { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }
    private void Post(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => { if (!_disposed) action(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _poll.Stop();
        _poll.Tick -= PollPlayback;
        _signIn?.Cancel();
        _signIn?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_session is not null) _session.TokenChanged -= TokenChanged;
#if UNOPLAY_BROWSER
        _browser?.Dispose();
#endif
        if (_ownsHttp) _http.Dispose();
    }
}
