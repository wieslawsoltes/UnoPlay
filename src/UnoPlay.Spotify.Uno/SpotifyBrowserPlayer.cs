#if UNOPLAY_BROWSER
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace UnoPlay.Spotify.Uno;

/// <summary>Owns one Spotify Web Playback SDK device and its access-token requests.</summary>
public sealed class SpotifyBrowserPlayer : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly Func<CancellationToken, Task<string>> _accessTokenProvider;
    private CancellationTokenSource _tokenLifetime = new();
    private int _generation;
    private bool _disposed;

    public SpotifyBrowserPlayer(Func<CancellationToken, Task<string>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        _accessTokenProvider = accessTokenProvider;
        SpotifySdkNative.Create(_id, OnEvent);
    }

    public string? DeviceId { get; private set; }
    public bool Ready => DeviceId is not null;
    public SpotifyPlaybackState? CurrentState { get; private set; }
    public event EventHandler<SpotifyPlaybackState?>? PlaybackChanged;
    public event EventHandler<string>? Failed;
    public event EventHandler? DeviceChanged;
    public event EventHandler? AutoplayFailed;

    /// <summary>Loads the hosted SDK on demand and waits for its device-ready event.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await SpotifySdkNative.Connect(_id).WaitAsync(cancellationToken);
    }

    public Task PauseAsync() { ThrowIfDisposed(); return SpotifySdkNative.Pause(_id); }
    public Task ResumeAsync() { ThrowIfDisposed(); return SpotifySdkNative.Resume(_id); }
    public Task TogglePlayAsync() { ThrowIfDisposed(); return SpotifySdkNative.TogglePlay(_id); }
    public Task PreviousAsync() { ThrowIfDisposed(); return SpotifySdkNative.Previous(_id); }
    public Task NextAsync() { ThrowIfDisposed(); return SpotifySdkNative.Next(_id); }

    /// <summary>Call directly from a user input handler before awaiting playback transfer.</summary>
    public Task ActivateElementAsync() { ThrowIfDisposed(); return SpotifySdkNative.Activate(_id); }

    public Task SeekAsync(TimeSpan position)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(position, TimeSpan.Zero);
        return SpotifySdkNative.Seek(_id, position.TotalMilliseconds);
    }

    public Task SetVolumeAsync(double volume)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Spotify volume must be between 0 and 1.");
        return SpotifySdkNative.SetVolume(_id, volume);
    }

    public Task<double> GetVolumeAsync() { ThrowIfDisposed(); return SpotifySdkNative.GetVolume(_id); }

    public Task SetNameAsync(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return SpotifySdkNative.SetName(_id, name);
    }

    public async Task<SpotifyPlaybackState?> GetCurrentStateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        int generation = _generation;
        string json = await SpotifySdkNative.CurrentState(_id).WaitAsync(cancellationToken);
        if (_disposed || generation != _generation) return null;
        CurrentState = SpotifyJson.ParsePlayback(json);
        return CurrentState;
    }

    /// <summary>
    /// Releases the SDK device and its callbacks. A host switching to another
    /// media engine should restore that engine's Media Session handlers afterward.
    /// ConnectAsync creates a new SDK device when Spotify is selected again.
    /// </summary>
    public void Disconnect()
    {
        if (_disposed) return;
        ++_generation;
        _tokenLifetime.Cancel();
        _tokenLifetime.Dispose();
        _tokenLifetime = new CancellationTokenSource();
        SpotifySdkNative.Disconnect(_id);
    }

    private void OnEvent(string kind, string value)
    {
        if (_disposed) return;
        switch (kind)
        {
            case "token":
                _ = SupplyTokenAsync(value, _generation, _tokenLifetime.Token);
                break;
            case "ready":
                DeviceId = value;
                DeviceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "not_ready":
                DeviceId = null;
                DeviceChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "state":
                try { CurrentState = SpotifyJson.ParsePlayback(value); }
                catch (JsonException) { Failed?.Invoke(this, "Spotify returned an unreadable playback state."); return; }
                PlaybackChanged?.Invoke(this, CurrentState);
                break;
            case "autoplay":
                AutoplayFailed?.Invoke(this, EventArgs.Empty);
                Failed?.Invoke(this, value);
                break;
            case "error":
                Failed?.Invoke(this, value);
                break;
        }
    }

    private async Task SupplyTokenAsync(string requestId, int generation, CancellationToken cancellationToken)
    {
        string token;
        try { token = await _accessTokenProvider(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (!_disposed && generation == _generation)
                SpotifySdkNative.AnswerToken(_id, requestId, "", "Spotify authorization failed. Sign in again.");
            return;
        }
        if (!_disposed && generation == _generation)
            SpotifySdkNative.AnswerToken(_id, requestId, token, "");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _tokenLifetime.Cancel();
        _tokenLifetime.Dispose();
        SpotifySdkNative.Dispose(_id);
        DeviceId = null;
        CurrentState = null;
    }
}

internal static partial class SpotifySdkNative
{
    private const string Api = "globalThis.UnoPlaySpotifySdk.";
    [JSImport(Api + "create")] internal static partial void Create(string id, [JSMarshalAs<JSType.Function<JSType.String, JSType.String>>] Action<string, string> notify);
    [JSImport(Api + "connect")] internal static partial Task<string> Connect(string id);
    [JSImport(Api + "disconnect")] internal static partial void Disconnect(string id);
    [JSImport(Api + "dispose")] internal static partial void Dispose(string id);
    [JSImport(Api + "answerToken")] internal static partial void AnswerToken(string id, string requestId, string token, string error);
    [JSImport(Api + "currentState")] internal static partial Task<string> CurrentState(string id);
    [JSImport(Api + "getVolume")] internal static partial Task<double> GetVolume(string id);
    [JSImport(Api + "setVolume")] internal static partial Task SetVolume(string id, double volume);
    [JSImport(Api + "setName")] internal static partial Task SetName(string id, string name);
    [JSImport(Api + "pause")] internal static partial Task Pause(string id);
    [JSImport(Api + "resume")] internal static partial Task Resume(string id);
    [JSImport(Api + "togglePlay")] internal static partial Task TogglePlay(string id);
    [JSImport(Api + "seek")] internal static partial Task Seek(string id, double milliseconds);
    [JSImport(Api + "previous")] internal static partial Task Previous(string id);
    [JSImport(Api + "next")] internal static partial Task Next(string id);
    [JSImport(Api + "activate")] internal static partial Task Activate(string id);
}
#endif
