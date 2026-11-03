using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnoPlay.Spotify;

/// <summary>Secret-free Authorization Code with PKCE. The host owns token storage and browser navigation.</summary>
public sealed class SpotifySession
{
    public const string DefaultScopes = "streaming user-read-private user-read-email user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative playlist-modify-public playlist-modify-private user-library-read user-library-modify user-follow-read user-follow-modify ugc-image-upload";
    private static readonly Uri TokenEndpoint = new("https://accounts.spotify.com/api/token");
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _gate = new();
    private SpotifyToken? _token;
    private int _generation;

    public SpotifySession(HttpClient http, string clientId, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (clientId.Length != 32 || !clientId.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Enter the 32-character Client ID from your Spotify developer app.", nameof(clientId));
        _http = http;
        ClientId = clientId;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string ClientId { get; }
    public SpotifyToken? Token { get { lock (_gate) return _token; } }
    public bool IsSignedIn => Token is not null;
    public event EventHandler? TokenChanged;

    public void Restore(SpotifyToken? token)
    {
        if (token is not null && (token.ClientId != ClientId || string.IsNullOrWhiteSpace(token.AccessToken)
            || string.IsNullOrWhiteSpace(token.RefreshToken)))
            throw new SpotifyAuthenticationException("The saved Spotify session belongs to another app or is incomplete. Sign in again.");
        lock (_gate)
        {
            _generation++;
            _token = token;
        }
        TokenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut() => Restore(null);

    public SpotifyAuthorizationRequest CreateAuthorization(Uri redirectUri, string scopes = DefaultScopes, bool showDialog = false)
    {
        ValidateRedirectUri(redirectUri);
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string query = "response_type=code&client_id=" + Uri.EscapeDataString(ClientId)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri.AbsoluteUri)
            + "&scope=" + Uri.EscapeDataString(scopes)
            + "&state=" + state + "&code_challenge_method=S256&code_challenge=" + challenge
            + (showDialog ? "&show_dialog=true" : "");
        return new(new Uri("https://accounts.spotify.com/authorize?" + query), redirectUri, state, verifier, _time.GetUtcNow());
    }

    public async Task CompleteAuthorizationAsync(SpotifyAuthorizationRequest request, Uri callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callback);
        ValidateRedirectUri(request.RedirectUri);
        if (!callback.IsAbsoluteUri || callback.GetLeftPart(UriPartial.Path) != request.RedirectUri.GetLeftPart(UriPartial.Path)
            || callback.Fragment.Length != 0)
            throw new SpotifyAuthenticationException("The Spotify callback does not match this app's redirect address.");
        var query = ReadQuery(callback.Query);
        if (!query.TryGetValue("state", out string? state) || !FixedEquals(state, request.State))
            throw new SpotifyAuthenticationException("Spotify sign-in state did not match. Start sign-in again.");
        TimeSpan age = _time.GetUtcNow() - request.CreatedAt;
        if (age < TimeSpan.FromMinutes(-1) || age > TimeSpan.FromMinutes(10))
            throw new SpotifyAuthenticationException("Spotify sign-in expired. Start sign-in again.");
        if (query.TryGetValue("error", out string? error))
            throw new SpotifyAuthenticationException(error == "access_denied" ? "Spotify sign-in was cancelled." : "Spotify could not authorize this request.", error);
        if (!query.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
            throw new SpotifyAuthenticationException("Spotify did not return an authorization code.");
        if (request.CodeVerifier.Length is < 43 or > 128)
            throw new SpotifyAuthenticationException("The Spotify sign-in verifier is invalid. Start sign-in again.");
        int generation;
        lock (_gate) generation = _generation;
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = request.RedirectUri.AbsoluteUri,
            ["code_verifier"] = request.CodeVerifier
        }, null, cancellationToken).ConfigureAwait(false);
        StoreToken(token, generation);
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => AccessTokenAsync(null, cancellationToken);

    /// <summary>Refreshes at most once when multiple requests reject the same access token.</summary>
    public Task<string> RefreshAccessTokenAsync(string rejectedToken, CancellationToken cancellationToken = default)
        => AccessTokenAsync(rejectedToken, cancellationToken);

    private async Task<string> AccessTokenAsync(string? rejectedToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Token ?? throw new SpotifyAuthenticationException("Sign in to Spotify to continue.");
        if (IsFresh(token, rejectedToken)) return token.AccessToken;
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int generation;
            lock (_gate)
            {
                token = _token ?? throw new SpotifyAuthenticationException("Sign in to Spotify to continue.");
                generation = _generation;
            }
            if (IsFresh(token, rejectedToken)) return token.AccessToken;
            SpotifyToken refreshed;
            try
            {
                refreshed = await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = ClientId,
                    ["refresh_token"] = token.RefreshToken
                }, token, cancellationToken).ConfigureAwait(false);
            }
            catch (SpotifyAuthenticationException exception) when (exception.Error == "invalid_grant")
            {
                bool cleared;
                lock (_gate)
                {
                    cleared = generation == _generation;
                    if (cleared) { _token = null; _generation++; }
                }
                if (cleared) TokenChanged?.Invoke(this, EventArgs.Empty);
                throw;
            }
            StoreToken(refreshed, generation);
            return refreshed.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    private bool IsFresh(SpotifyToken token, string? rejectedToken) => token.AccessToken != rejectedToken
        && token.ExpiresAt > _time.GetUtcNow().AddSeconds(60);

    private void StoreToken(SpotifyToken token, int generation)
    {
        lock (_gate)
        {
            if (_generation != generation)
                throw new SpotifyAuthenticationException("The Spotify session changed during sign-in. Try again.");
            _token = token;
            _generation++;
        }
        TokenChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<SpotifyToken> RequestTokenAsync(Dictionary<string, string> fields, SpotifyToken? previous, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await SpotifyJson.ReadDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? error = SpotifyJson.String(json.RootElement, "error");
            throw new SpotifyAuthenticationException(error == "invalid_grant"
                ? "Spotify sign-in expired or was revoked. Sign in again."
                : "Spotify could not complete sign-in (HTTP " + (int)response.StatusCode + ").", error);
        }
        string accessToken = SpotifyJson.String(json.RootElement, "access_token") ?? "";
        string refreshToken = SpotifyJson.String(json.RootElement, "refresh_token") ?? previous?.RefreshToken ?? "";
        string tokenType = SpotifyJson.String(json.RootElement, "token_type") ?? "";
        int expires = SpotifyJson.Int(json.RootElement, "expires_in");
        if (accessToken.Length == 0 || refreshToken.Length == 0 || expires <= 0 || !tokenType.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            throw new SpotifyAuthenticationException("Spotify returned an incomplete sign-in response.");
        return new(accessToken, refreshToken, _time.GetUtcNow().AddSeconds(expires),
            SpotifyJson.String(json.RootElement, "scope") ?? previous?.Scope ?? "", ClientId);
    }

    public static void ValidateRedirectUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The Spotify redirect must be an absolute URL without query parameters or a fragment.", nameof(uri));
        bool loopback = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
            throw new ArgumentException("Spotify requires HTTPS, or HTTP with a loopback IP such as 127.0.0.1 for desktop sign-in.", nameof(uri));
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string actual, string expected) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    private static Dictionary<string, string> ReadQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            string key = Uri.UnescapeDataString((separator < 0 ? part : part[..separator]).Replace('+', ' '));
            string value = separator < 0 ? "" : Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            if (!result.TryAdd(key, value)) throw new SpotifyAuthenticationException("Spotify returned duplicate callback parameters.");
        }
        return result;
    }
}
