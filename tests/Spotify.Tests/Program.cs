using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnoPlay.Spotify;

const string clientId = "0123456789abcdef0123456789abcdef";
const string trackUri = "spotify:track:6rqhFgbbKwnb9MLmUQDhG6";
const string playlistId = "3cEYpjA9oz9GiPac4AsH4n";
int assertions = 0;
void Check(bool value, string reason)
{
    assertions++;
    if (!value) throw new InvalidOperationException(reason);
}
async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T exception) { assertions++; return exception; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};
static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? ""));
static SpotifyToken Token(string access = "access-token", DateTimeOffset? expires = null) => new(access, "refresh-token", expires ?? DateTimeOffset.UtcNow.AddHours(1), SpotifySession.DefaultScopes, clientId);

var noNetwork = new StubHandler((_, _) => throw new InvalidOperationException("Unexpected network access"));
using var noNetworkHttp = new HttpClient(noNetwork);
var session = new SpotifySession(noNetworkHttp, clientId);
await Throws<ArgumentException>(() => Task.Run(() => new SpotifySession(noNetworkHttp, "not-a-client-id")));
foreach (string badRedirect in new[] { "http://localhost:1234/", "http://example.com/callback", "ftp://127.0.0.1/", "https://example.com/?x=1", "https://example.com/#fragment" })
    await Throws<ArgumentException>(() => Task.Run(() => SpotifySession.ValidateRedirectUri(new Uri(badRedirect))));
SpotifySession.ValidateRedirectUri(new Uri("http://127.0.0.1:43123/spotify/callback"));
SpotifySession.ValidateRedirectUri(new Uri("http://[::1]:43123/spotify/callback"));
SpotifySession.ValidateRedirectUri(new Uri("https://wieslawsoltes.github.io/UnoPlay/"));
var authorization = session.CreateAuthorization(new Uri("https://example.com/UnoPlay/"));
var anotherAuthorization = session.CreateAuthorization(authorization.RedirectUri);
var authorizationQuery = Query(authorization.AuthorizationUri.Query);
Check(authorization.CodeVerifier.Length is >= 43 and <= 128, "PKCE verifier length");
Check(authorization.State != anotherAuthorization.State && authorization.CodeVerifier != anotherAuthorization.CodeVerifier, "PKCE randomness");
Check(authorizationQuery["code_challenge_method"] == "S256" && authorizationQuery["response_type"] == "code", "PKCE authorization parameters");
string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(authorization.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
Check(authorizationQuery["code_challenge"] == challenge, "S256 challenge");
Check(authorizationQuery["client_id"] == clientId && authorizationQuery["redirect_uri"] == authorization.RedirectUri.AbsoluteUri, "App binding in authorization");
await Throws<SpotifyAuthenticationException>(() => session.CompleteAuthorizationAsync(authorization, new Uri("https://example.com/UnoPlay/?state=wrong&code=x")));
await Throws<SpotifyAuthenticationException>(() => session.CompleteAuthorizationAsync(authorization, new Uri("https://evil.example/UnoPlay/?state=" + authorization.State + "&code=x")));
await Throws<SpotifyAuthenticationException>(() => session.CompleteAuthorizationAsync(authorization, new Uri("https://example.com/UnoPlay/?state=" + authorization.State + "&state=" + authorization.State + "&code=x")));
await Throws<SpotifyAuthenticationException>(() => session.CompleteAuthorizationAsync(authorization with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-11) }, new Uri("https://example.com/UnoPlay/?state=" + authorization.State + "&code=x")));
var denied = await Throws<SpotifyAuthenticationException>(() => session.CompleteAuthorizationAsync(authorization, new Uri("https://example.com/UnoPlay/?state=" + authorization.State + "&error=access_denied")));
Check(denied.Error == "access_denied" && noNetwork.Count == 0, "OAuth validation occurs before token exchange");

var grantHandler = new StubHandler((request, _) =>
{
    Check(request.Url == "https://accounts.spotify.com/api/token" && request.Method == "POST", "Official token endpoint");
    var fields = Query(request.Body!.Replace('+', ' '));
    Check(fields["code_verifier"] == authorization.CodeVerifier && fields["code"] == "authorization-code", "Authorization code exchange");
    Check(fields["client_id"] == clientId && !fields.ContainsKey("client_secret"), "PKCE has no client secret");
    return Task.FromResult(Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"streaming\"}"));
});
using var grantHttp = new HttpClient(grantHandler);
var grantSession = new SpotifySession(grantHttp, clientId);
int tokenChanges = 0;
grantSession.TokenChanged += (_, _) => tokenChanges++;
await grantSession.CompleteAuthorizationAsync(authorization, new Uri("https://example.com/UnoPlay/?state=" + authorization.State + "&code=authorization-code"));
Check(grantSession.IsSignedIn && tokenChanges == 1, "Successful authorization persists session");
Check(await grantSession.GetAccessTokenAsync() == "new-access" && grantHandler.Count == 1, "Fresh token reused");
Check(SpotifyJson.DeserializeToken(SpotifyJson.SerializeToken(grantSession.Token!)) == grantSession.Token, "Trim-safe token serialization");
Check(SpotifyJson.DeserializeAuthorization(SpotifyJson.SerializeAuthorization(authorization)) == authorization, "PKCE state survives browser redirect");
await Throws<SpotifyAuthenticationException>(() => Task.Run(() => grantSession.Restore(Token() with { ClientId = "another-app" })));

var refreshHandler = new StubHandler(async (request, cancellationToken) =>
{
    await Task.Delay(30, cancellationToken);
    Check(Query(request.Body!)["grant_type"] == "refresh_token", "Refresh grant");
    return Json("{\"access_token\":\"refreshed-access\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
});
using var refreshHttp = new HttpClient(refreshHandler);
var refreshSession = new SpotifySession(refreshHttp, clientId);
refreshSession.Restore(Token(expires: DateTimeOffset.UtcNow.AddMinutes(-1)));
var concurrent = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => refreshSession.GetAccessTokenAsync()));
Check(concurrent.All(value => value == "refreshed-access") && refreshHandler.Count == 1, "Concurrent expired tokens refresh once");
Check(refreshSession.Token!.RefreshToken == "refresh-token", "Refresh token preserved when Spotify omits rotation");
Check(await refreshSession.RefreshAccessTokenAsync("access-token") == "refreshed-access" && refreshHandler.Count == 1, "Late 401 uses already refreshed token");

var revokedHandler = new StubHandler((_, _) => Task.FromResult(Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest)));
using var revokedHttp = new HttpClient(revokedHandler);
var revokedSession = new SpotifySession(revokedHttp, clientId);
revokedSession.Restore(Token(expires: DateTimeOffset.UtcNow.AddMinutes(-1)));
await Throws<SpotifyAuthenticationException>(() => revokedSession.GetAccessTokenAsync());
Check(!revokedSession.IsSignedIn, "Revoked refresh token clears session");

var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var raceHandler = new StubHandler(async (_, cancellationToken) =>
{
    refreshStarted.SetResult();
    await releaseRefresh.Task.WaitAsync(cancellationToken);
    return Json("{\"access_token\":\"late-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
});
using var raceHttp = new HttpClient(raceHandler);
var raceSession = new SpotifySession(raceHttp, clientId);
raceSession.Restore(Token(expires: DateTimeOffset.UtcNow.AddMinutes(-1)));
Task<string> pendingRefresh = raceSession.GetAccessTokenAsync();
await refreshStarted.Task;
raceSession.SignOut();
releaseRefresh.SetResult();
await Throws<SpotifyAuthenticationException>(() => pendingRefresh);
Check(!raceSession.IsSignedIn, "An in-flight refresh cannot resurrect a signed-out session");

const string trackJson = "{\"id\":\"6rqhFgbbKwnb9MLmUQDhG6\",\"uri\":\"spotify:track:6rqhFgbbKwnb9MLmUQDhG6\",\"name\":\"First track\",\"artists\":[{\"id\":\"artist1\",\"name\":\"An artist\"}],\"album\":{\"name\":\"An album\",\"images\":[{\"url\":\"https://i.scdn.co/cover\"}]},\"duration_ms\":180000,\"type\":\"track\"}";
const string playlistJson = "{\"id\":\"3cEYpjA9oz9GiPac4AsH4n\",\"name\":\"A playlist\",\"uri\":\"spotify:playlist:3cEYpjA9oz9GiPac4AsH4n\",\"owner\":{\"id\":\"user1\"},\"public\":null,\"snapshot_id\":\"snapshot1\",\"items\":{\"total\":3}}";
var responses = new Queue<HttpResponseMessage>();
var apiHandler = new StubHandler((request, _) =>
{
    Check(request.Authorization == "Bearer access-token", "API uses bearer authorization");
    return Task.FromResult(responses.Dequeue());
});
using var apiHttp = new HttpClient(apiHandler);
var apiSession = new SpotifySession(apiHttp, clientId);
apiSession.Restore(Token());
var api = new SpotifyWebApi(apiHttp, apiSession);
responses.Enqueue(Json("{\"id\":\"user1\",\"display_name\":null}"));
var profile = await api.GetProfileAsync();
Check(profile.Id == "user1" && profile.DisplayName == "user1", "Development profile tolerates removed fields");
responses.Enqueue(Json("{\"items\":[" + playlistJson + "],\"offset\":0,\"limit\":50,\"total\":1,\"next\":null}"));
var playlists = await api.GetPlaylistsAsync();
Check(playlists.Items[0].TotalItems == 3 && playlists.Items[0].IsPublic is null, "2026 playlist items metadata");
responses.Enqueue(Json("{\"items\":[{\"item\":" + trackJson + "},{\"item\":null},{\"track\":" + trackJson + "}],\"offset\":50,\"limit\":50,\"total\":53}"));
var items = await api.GetPlaylistItemsAsync(playlistId, 50);
Check(apiHandler.Last!.Url.Contains("/items?offset=50&limit=50"), "2026 playlist endpoint");
Check(items.Items.Count == 3 && items.Items[0].Position == 50 && items.Items[1].Track is null && items.Items[2].Position == 52, "Playlist null items preserve original positions");
Check(items.Items[0].Track!.ArtistName == "An artist" && items.Items[0].Track!.ImageUrl == "https://i.scdn.co/cover", "Track metadata projection");
Check(items.Items[2].Track!.Name == "First track", "Legacy extended-quota item parsing");
responses.Enqueue(Json("{\"items\":[],\"next\":\"https://evil.example/steal\"}"));
await Throws<ArgumentException>(() => api.GetPlaylistsAsync());
responses.Enqueue(Json("{\"items\":[],\"next\":\"http://api.spotify.com/v1/me/playlists\"}"));
await Throws<ArgumentException>(() => api.GetPlaylistsAsync());
int beforeValidation = apiHandler.Count;
await Throws<ArgumentOutOfRangeException>(() => api.SearchAsync("a", limit: 11));
await Throws<ArgumentOutOfRangeException>(() => api.GetPlaylistsAsync(-1));
await Throws<ArgumentException>(() => api.GetPlaylistAsync("../me"));
await Throws<ArgumentException>(() => api.AddPlaylistItemsAsync(playlistId, Enumerable.Repeat(trackUri, 101).ToArray()));
await Throws<ArgumentException>(() => api.SaveLibraryItemsAsync(Enumerable.Repeat(trackUri, 41).ToArray()));
Check(apiHandler.Count == beforeValidation, "Invalid requests rejected before network");
responses.Enqueue(Json("{\"tracks\":{\"items\":[" + trackJson + "],\"offset\":0,\"limit\":10,\"total\":1}}"));
var search = await api.SearchAsync("name & artist");
Check(search.Tracks.Items.Count == 1 && search.Playlists.Items.Count == 0, "Partial search payload");
Check(Query(new Uri(apiHandler.Last!.Url).Query)["q"] == "name & artist", "Search query encoding");
responses.Enqueue(Json(playlistJson, HttpStatusCode.Created));
await api.CreatePlaylistAsync("Private by default");
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!))
    Check(apiHandler.Last.Url.EndsWith("/me/playlists") && !body.RootElement.GetProperty("public").GetBoolean(), "Current create endpoint and private default");
responses.Enqueue(Json("{\"snapshot_id\":\"snapshot2\"}", HttpStatusCode.Created));
Check(await api.AddPlaylistItemsAsync(playlistId, [trackUri], 2) == "snapshot2", "Mutation snapshot returned");
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!)) Check(body.RootElement.GetProperty("position").GetInt32() == 2, "Insertion position");
responses.Enqueue(Json("{\"snapshot_id\":\"snapshot3\"}"));
await api.RemovePlaylistItemsAsync(playlistId, [new SpotifyPlaylistRemoval(trackUri)], "snapshot2");
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!))
{
    Check(body.RootElement.GetProperty("items")[0].GetProperty("uri").GetString() == trackUri, "Current removal body uses items");
    Check(!body.RootElement.TryGetProperty("tracks", out _) && body.RootElement.GetProperty("snapshot_id").GetString() == "snapshot2", "Removal carries snapshot");
}
responses.Enqueue(Json("{\"snapshot_id\":\"snapshot4\"}"));
await api.ReorderPlaylistItemsAsync(playlistId, 0, 3, snapshotId: "snapshot3");
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!))
    Check(body.RootElement.GetProperty("insert_before").GetInt32() == 3 && body.RootElement.GetProperty("snapshot_id").GetString() == "snapshot3", "Snapshot-aware reorder");
responses.Enqueue(Json("{\"snapshot_id\":\"snapshot5\"}"));
await api.ReplacePlaylistItemsAsync(playlistId, []);
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!)) Check(body.RootElement.GetProperty("uris").GetArrayLength() == 0, "Explicit clear through replace");
responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
await api.SaveLibraryItemsAsync([trackUri]);
Check(apiHandler.Last!.Method == "PUT" && apiHandler.Last.Url.Contains("/me/library?uris="), "Current generic library endpoint");
responses.Enqueue(Json("[true,false]"));
var saved = await api.CheckLibraryItemsAsync([trackUri, "spotify:album:album1"]);
Check(saved.SequenceEqual([true, false]), "Saved library items map in input order");
responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
await api.PlayAsync("device1", contextUri: "spotify:playlist:" + playlistId, offsetPosition: 4, positionMs: 1000);
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!))
    Check(body.RootElement.GetProperty("offset").GetProperty("position").GetInt32() == 4 && Query(new Uri(apiHandler.Last.Url).Query)["device_id"] == "device1", "Context playback and selected-device targeting");
responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
await api.TransferPlaybackAsync("device1", true);
using (var body = JsonDocument.Parse(apiHandler.Last!.Body!))
    Check(body.RootElement.GetProperty("device_ids")[0].GetString() == "device1" && body.RootElement.GetProperty("play").GetBoolean(), "Device transfer");
responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
Check(await api.GetPlaybackAsync() is null, "204 playback means no active device");
var playback = SpotifyJson.ParsePlayback("{\"device\":{\"id\":\"device1\",\"name\":\"Browser\",\"supports_volume\":true,\"volume_percent\":42},\"item\":" + trackJson + ",\"is_playing\":true,\"progress_ms\":1200,\"repeat_state\":\"context\",\"shuffle_state\":true,\"actions\":{\"disallows\":{\"seeking\":true,\"skipping_next\":false}}}");
Check(playback!.Track!.Name == "First track" && playback.Device!.VolumePercent == 42 && playback.IsPlaying && playback.ProgressMs == 1200, "Playback SDK/API shared metadata");
Check(playback.DisallowedActions.SetEquals(["seeking"]) && playback.ShuffleState && playback.RepeatState == "context", "Playback restrictions and modes");
responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
await api.SetVolumeAsync(73, "device&one");
var volumeQuery = Query(new Uri(apiHandler.Last!.Url).Query);
Check(volumeQuery["volume_percent"] == "73" && volumeQuery["device_id"] == "device&one", "Volume and escaped device query");

var throttled = Json("{\"error\":{\"status\":429,\"message\":\"Quota reached\",\"reason\":\"QUOTA_EXCEEDED\"}}", HttpStatusCode.TooManyRequests);
throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
responses.Enqueue(throttled);
int beforeThrottle = apiHandler.Count;
var quota = await Throws<SpotifyApiException>(() => api.GetDevicesAsync());
Check(quota.StatusCode == 429 && quota.IsQuotaExceeded && quota.RetryAfter == TimeSpan.FromSeconds(7), "2026 quota reason and retry delay");
Check(apiHandler.Count == beforeThrottle + 1, "Throttled requests do not retry in a loop");
responses.Enqueue(Json("{\"error\":{\"message\":\"Premium required\"}}", HttpStatusCode.Forbidden));
var premium = await Throws<SpotifyApiException>(() => api.PlayAsync());
Check(premium.StatusCode == 403 && premium.Message.Contains("Premium"), "Premium/API access failure surfaced");

int authRetryStep = 0;
var authRetryHandler = new StubHandler((request, _) =>
{
    authRetryStep++;
    if (authRetryStep == 1) return Task.FromResult(Json("{\"error\":{\"message\":\"expired\"}}", HttpStatusCode.Unauthorized));
    if (authRetryStep == 2)
    {
        Check(request.Url == "https://accounts.spotify.com/api/token", "401 refresh endpoint");
        return Task.FromResult(Json("{\"access_token\":\"retry-access\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));
    }
    Check(request.Authorization == "Bearer retry-access", "401 retry uses fresh access token");
    return Task.FromResult(Json("{\"id\":\"user1\"}"));
});
using var authRetryHttp = new HttpClient(authRetryHandler);
var authRetrySession = new SpotifySession(authRetryHttp, clientId);
authRetrySession.Restore(Token());
await new SpotifyWebApi(authRetryHttp, authRetrySession).GetProfileAsync();
Check(authRetryStep == 3, "API refresh retries once");
var sdkEpisode = SpotifyJson.ParsePlayback("{\"item\":" + trackJson.Replace("\"type\":\"track\"", "\"type\":\"episode\"") + ",\"actions\":{\"seeking\":true}}");
Check(sdkEpisode!.Track!.Type == "episode" && sdkEpisode.Track.ArtistName == "An artist" && sdkEpisode.Track.ImageUrl == "https://i.scdn.co/cover", "SDK episode album and artists preserved");
Check(sdkEpisode.DisallowedActions.SetEquals(["seeking"]), "Current direct playback restrictions");
Check(!grantSession.Token!.ToString().Contains("new-access") && !authorization.ToString().Contains(authorization.CodeVerifier), "Session diagnostics redact credentials");
var explicitTrack = SpotifyJson.ParsePlayback("{\"item\":" + trackJson.Replace("\"type\":\"track\"", "\"type\":\"track\",\"explicit\":true") + "}");
Check(explicitTrack!.Track!.IsExplicit, "Explicit content marker retained");
var localTrack = SpotifyJson.ParsePlayback("{\"item\":{\"uri\":\"spotify:local:artist:album:track:120\",\"type\":\"track\"}}");
Check(localTrack!.Track!.IsLocal && !localTrack.Track.IsPlayable, "Spotify local-file URIs are unavailable to SDK playback");
Check(responses.Count == 0, "All expected HTTP responses consumed");
Console.WriteLine($"Spotify PKCE, token lifecycle, current Web API endpoints, playlist editing, playback, pagination and rate-limit handling passed ({assertions} assertions).");

sealed record CapturedRequest(string Url, string Method, string? Body, string? Authorization);
sealed class StubHandler(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    private int _count;
    public int Count => _count;
    public CapturedRequest? Last { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        var captured = new CapturedRequest(request.RequestUri!.AbsoluteUri, request.Method.Method,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Authorization?.ToString());
        Last = captured;
        return await response(captured, cancellationToken);
    }
}
