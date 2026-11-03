using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UnoPlay.Spotify;

/// <summary>Spotify Web API client for 2026 endpoints. Each mutation makes one bounded request.</summary>
public sealed class SpotifyWebApi(HttpClient http, SpotifySession session)
{
    private static readonly Uri ApiRoot = new("https://api.spotify.com/v1/");

    public async Task<SpotifyProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me", null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Profile(json.RootElement);
    }

    public async Task<SpotifyPage<SpotifyPlaylist>> GetPlaylistsAsync(int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/playlists" + PageQuery(offset, limit), null, cancellationToken).ConfigureAwait(false);
        return ValidatePage(SpotifyJson.Page(json.RootElement, (item, _) => SpotifyJson.Playlist(item)));
    }

    public async Task<SpotifyPlaylist> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "playlists/" + Id(playlistId), null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Playlist(json.RootElement);
    }

    public async Task<SpotifyPage<SpotifyPlaylistItem>> GetPlaylistItemsAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "playlists/" + Id(playlistId) + "/items" + PageQuery(offset, limit) + "&additional_types=track,episode", null, cancellationToken).ConfigureAwait(false);
        return ValidatePage(SpotifyJson.Page(json.RootElement, SpotifyJson.PlaylistItem));
    }

    public async Task<SpotifyPage<SpotifySavedTrack>> GetSavedTracksAsync(int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/tracks" + PageQuery(offset, limit), null, cancellationToken).ConfigureAwait(false);
        return ValidatePage(SpotifyJson.Page(json.RootElement, (item, _) => SpotifyJson.SavedTrack(item)));
    }

    public async Task<SpotifySearchResults> SearchAsync(string query, int offset = 0, int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (offset is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit), "Spotify search supports 1 to 10 results per type per request.");
        using var json = await SendAsync(HttpMethod.Get, "search?q=" + Uri.EscapeDataString(query) + "&type=track,playlist,album,artist&limit=" + limit + "&offset=" + offset, null, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        return new(
            ValidatePage(SpotifyJson.Page(SpotifyJson.Property(root, "tracks"), (item, _) => SpotifyJson.Track(item) ?? UnavailableTrack)),
            ValidatePage(SpotifyJson.Page(SpotifyJson.Property(root, "playlists"), (item, _) => SpotifyJson.Playlist(item))),
            ValidatePage(SpotifyJson.Page(SpotifyJson.Property(root, "albums"), (item, _) => SpotifyJson.Album(item))),
            ValidatePage(SpotifyJson.Page(SpotifyJson.Property(root, "artists"), (item, _) => SpotifyJson.Artist(item))));
    }

    public async Task<SpotifyTrack?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "tracks/" + Id(trackId), null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Track(json.RootElement);
    }

    public async Task<SpotifyAlbum> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "albums/" + Id(albumId), null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Album(json.RootElement);
    }

    public async Task<SpotifyPage<SpotifyTrack>> GetAlbumTracksAsync(string albumId, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "albums/" + Id(albumId) + "/tracks" + PageQuery(offset, limit), null, cancellationToken).ConfigureAwait(false);
        return ValidatePage(SpotifyJson.Page(json.RootElement, (item, _) => SpotifyJson.Track(item) ?? UnavailableTrack));
    }

    public async Task<SpotifyArtist> GetArtistAsync(string artistId, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "artists/" + Id(artistId), null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Artist(json.RootElement);
    }

    public async Task<SpotifyPlaylist> CreatePlaylistAsync(string name, string description = "", bool isPublic = false, bool collaborative = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (isPublic && collaborative) throw new ArgumentException("Collaborative playlists must be private.");
        var body = Body(writer =>
        {
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WriteBoolean("public", isPublic);
            writer.WriteBoolean("collaborative", collaborative);
        });
        using var json = await SendAsync(HttpMethod.Post, "me/playlists", body, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Playlist(json.RootElement);
    }

    public Task UpdatePlaylistDetailsAsync(string playlistId, string name, string description, bool? isPublic = null, bool? collaborative = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (isPublic == true && collaborative == true) throw new ArgumentException("Collaborative playlists must be private.");
        return CommandAsync(HttpMethod.Put, "playlists/" + Id(playlistId), Body(writer =>
        {
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            if (isPublic.HasValue) writer.WriteBoolean("public", isPublic.Value);
            if (collaborative.HasValue) writer.WriteBoolean("collaborative", collaborative.Value);
        }), cancellationToken);
    }

    public Task<string> AddPlaylistItemsAsync(string playlistId, IReadOnlyList<string> uris, int? position = null, CancellationToken cancellationToken = default)
    {
        ValidateUris(uris, 100, playableOnly: true);
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        return SnapshotAsync(HttpMethod.Post, "playlists/" + Id(playlistId) + "/items", Body(writer =>
        {
            WriteUris(writer, "uris", uris);
            if (position.HasValue) writer.WriteNumber("position", position.Value);
        }), cancellationToken);
    }

    public Task<string> ReplacePlaylistItemsAsync(string playlistId, IReadOnlyList<string> uris, CancellationToken cancellationToken = default)
    {
        ValidateUris(uris, 100, playableOnly: true, allowEmpty: true);
        return SnapshotAsync(HttpMethod.Put, "playlists/" + Id(playlistId) + "/items", Body(writer => WriteUris(writer, "uris", uris)), cancellationToken);
    }

    /// <summary>Removes all occurrences of each URI, matching Spotify's current documented item-removal behavior.</summary>
    public Task<string> RemovePlaylistItemsAsync(string playlistId, IReadOnlyList<SpotifyPlaylistRemoval> items, string? snapshotId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateUris(items.Select(item => item.Uri).ToArray(), 100, playableOnly: true);
        return SnapshotAsync(HttpMethod.Delete, "playlists/" + Id(playlistId) + "/items", Body(writer =>
        {
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("uri", item.Uri);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (!string.IsNullOrEmpty(snapshotId)) writer.WriteString("snapshot_id", snapshotId);
        }), cancellationToken);
    }

    public Task<string> ReorderPlaylistItemsAsync(string playlistId, int rangeStart, int insertBefore, int rangeLength = 1, string? snapshotId = null, CancellationToken cancellationToken = default)
    {
        if (rangeStart < 0) throw new ArgumentOutOfRangeException(nameof(rangeStart));
        if (insertBefore < 0) throw new ArgumentOutOfRangeException(nameof(insertBefore));
        if (rangeLength < 1) throw new ArgumentOutOfRangeException(nameof(rangeLength));
        return SnapshotAsync(HttpMethod.Put, "playlists/" + Id(playlistId) + "/items", Body(writer =>
        {
            writer.WriteNumber("range_start", rangeStart);
            writer.WriteNumber("insert_before", insertBefore);
            writer.WriteNumber("range_length", rangeLength);
            if (!string.IsNullOrEmpty(snapshotId)) writer.WriteString("snapshot_id", snapshotId);
        }), cancellationToken);
    }

    public Task SaveLibraryItemsAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default)
        => CommandAsync(HttpMethod.Put, "me/library" + LibraryQuery(uris), null, cancellationToken);

    public Task RemoveLibraryItemsAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default)
        => CommandAsync(HttpMethod.Delete, "me/library" + LibraryQuery(uris), null, cancellationToken);

    public async Task<IReadOnlyList<bool>> CheckLibraryItemsAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/library/contains" + LibraryQuery(uris), null, cancellationToken).ConfigureAwait(false);
        var values = SpotifyJson.Array(json.RootElement);
        if (values.Length != uris.Count || values.Any(item => item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new SpotifyApiException(502, "Spotify returned an incomplete saved-item check.");
        return values.Select(item => item.ValueKind == JsonValueKind.True).ToArray();
    }

    public async Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/player/devices", null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Array(SpotifyJson.Property(json.RootElement, "devices")).Select(SpotifyJson.Device).ToArray();
    }

    public async Task<SpotifyPlaybackState?> GetPlaybackAsync(CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/player?additional_types=track,episode", null, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.Playback(json.RootElement);
    }

    public async Task<SpotifyQueue> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(HttpMethod.Get, "me/player/queue", null, cancellationToken).ConfigureAwait(false);
        return new(SpotifyJson.Track(SpotifyJson.Property(json.RootElement, "currently_playing")),
            SpotifyJson.Array(SpotifyJson.Property(json.RootElement, "queue")).Select(SpotifyJson.Track).OfType<SpotifyTrack>().ToArray());
    }

    public Task TransferPlaybackAsync(string deviceId, bool play = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return CommandAsync(HttpMethod.Put, "me/player", Body(writer =>
        {
            WriteUris(writer, "device_ids", [deviceId]);
            writer.WriteBoolean("play", play);
        }), cancellationToken);
    }

    public Task PlayAsync(string? deviceId = null, string? contextUri = null, IReadOnlyList<string>? uris = null, int? offsetPosition = null, int? positionMs = null, CancellationToken cancellationToken = default)
    {
        if (contextUri is not null && uris is not null) throw new ArgumentException("Choose either a playback context or a list of items.");
        if (contextUri is not null) ValidateUri(contextUri, false);
        if (uris is not null) ValidateUris(uris, 100, playableOnly: true);
        if (offsetPosition < 0) throw new ArgumentOutOfRangeException(nameof(offsetPosition));
        if (offsetPosition.HasValue && contextUri is null && uris is null) throw new ArgumentException("An offset requires a playback context or items.");
        if (positionMs < 0) throw new ArgumentOutOfRangeException(nameof(positionMs));
        return CommandAsync(HttpMethod.Put, "me/player/play" + DeviceQuery(deviceId), Body(writer =>
        {
            if (contextUri is not null) writer.WriteString("context_uri", contextUri);
            if (uris is not null) WriteUris(writer, "uris", uris);
            if (offsetPosition.HasValue)
            {
                writer.WriteStartObject("offset");
                writer.WriteNumber("position", offsetPosition.Value);
                writer.WriteEndObject();
            }
            if (positionMs.HasValue) writer.WriteNumber("position_ms", positionMs.Value);
        }), cancellationToken);
    }

    public Task PauseAsync(string? deviceId = null, CancellationToken cancellationToken = default)
        => CommandAsync(HttpMethod.Put, "me/player/pause" + DeviceQuery(deviceId), null, cancellationToken);
    public Task NextAsync(string? deviceId = null, CancellationToken cancellationToken = default)
        => CommandAsync(HttpMethod.Post, "me/player/next" + DeviceQuery(deviceId), null, cancellationToken);
    public Task PreviousAsync(string? deviceId = null, CancellationToken cancellationToken = default)
        => CommandAsync(HttpMethod.Post, "me/player/previous" + DeviceQuery(deviceId), null, cancellationToken);

    public Task SeekAsync(int positionMs, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (positionMs < 0) throw new ArgumentOutOfRangeException(nameof(positionMs));
        return CommandAsync(HttpMethod.Put, "me/player/seek?position_ms=" + positionMs + DeviceQuery(deviceId, true), null, cancellationToken);
    }

    public Task SetRepeatAsync(string state, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (state is not ("off" or "context" or "track")) throw new ArgumentException("Repeat must be off, context, or track.", nameof(state));
        return CommandAsync(HttpMethod.Put, "me/player/repeat?state=" + state + DeviceQuery(deviceId, true), null, cancellationToken);
    }

    public Task SetShuffleAsync(bool enabled, string? deviceId = null, CancellationToken cancellationToken = default)
        => CommandAsync(HttpMethod.Put, "me/player/shuffle?state=" + (enabled ? "true" : "false") + DeviceQuery(deviceId, true), null, cancellationToken);

    public Task SetVolumeAsync(int volumePercent, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        if (volumePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volumePercent));
        return CommandAsync(HttpMethod.Put, "me/player/volume?volume_percent=" + volumePercent + DeviceQuery(deviceId, true), null, cancellationToken);
    }

    public Task AddToQueueAsync(string uri, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        ValidateUri(uri, true);
        return CommandAsync(HttpMethod.Post, "me/player/queue?uri=" + Uri.EscapeDataString(uri) + DeviceQuery(deviceId, true), null, cancellationToken);
    }

    public async Task UploadPlaylistCoverAsync(string playlistId, byte[] jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        string encoded = Convert.ToBase64String(jpeg);
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8 || encoded.Length > 256 * 1024)
            throw new ArgumentException("Spotify playlist covers must be JPEG with a base64 encoded size of at most 256 KiB.", nameof(jpeg));
        using var json = await SendAsync(HttpMethod.Put, "playlists/" + Id(playlistId) + "/images", Encoding.ASCII.GetBytes(encoded), cancellationToken, "image/jpeg").ConfigureAwait(false);
    }

    private async Task CommandAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(method, path, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SnapshotAsync(HttpMethod method, string path, byte[] body, CancellationToken cancellationToken)
    {
        using var json = await SendAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        return SpotifyJson.String(json.RootElement, "snapshot_id") ?? throw new SpotifyApiException(502, "Spotify did not return the updated playlist snapshot. Refresh the playlist before editing again.");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken, string contentType = "application/json")
    {
        Uri uri = ApiUri(path);
        string token = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var firstRequest = Request(method, uri, token, body, contentType);
        var response = await http.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await session.RefreshAccessTokenAsync(token, cancellationToken).ConfigureAwait(false);
            using var retryRequest = Request(method, uri, token, body, contentType);
            response = await http.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        using (response)
        {
            var json = await SpotifyJson.ReadDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return json;
            using (json)
            {
                var error = SpotifyJson.Property(json.RootElement, "error");
                string? reason = SpotifyJson.String(error, "reason") ?? SpotifyJson.String(json.RootElement, "reason");
                string message = SpotifyJson.String(error, "message") ?? "Spotify request failed (HTTP " + (int)response.StatusCode + ").";
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    message += " Check Spotify Premium, app user access, and the granted permissions.";
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;
                if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
                throw new SpotifyApiException((int)response.StatusCode, message, reason, retryAfter);
            }
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri uri, string token, byte[]? body, string contentType)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return request;
    }

    private static Uri ApiUri(string path)
    {
        var uri = new Uri(ApiRoot, path);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != ApiRoot.Host || uri.Port != 443 || uri.UserInfo.Length != 0
            || !uri.AbsolutePath.StartsWith(ApiRoot.AbsolutePath, StringComparison.Ordinal) || uri.Fragment.Length != 0)
            throw new ArgumentException("Spotify API pagination must remain on https://api.spotify.com/v1/.", nameof(path));
        return uri;
    }

    private static SpotifyPage<T> ValidatePage<T>(SpotifyPage<T> page)
    {
        if (page.Next is not null) ApiUri(page.Next);
        return page;
    }

    private static string PageQuery(int offset, int limit)
    {
        if (offset is < 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        return "?offset=" + offset + "&limit=" + limit;
    }

    private static string LibraryQuery(IReadOnlyList<string> uris)
    {
        ValidateUris(uris, 40, playableOnly: false);
        return "?uris=" + Uri.EscapeDataString(string.Join(',', uris));
    }

    private static string DeviceQuery(string? deviceId, bool append = false)
        => string.IsNullOrEmpty(deviceId) ? "" : (append ? "&" : "?") + "device_id=" + Uri.EscapeDataString(deviceId);

    private static string Id(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!id.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Spotify IDs contain ASCII letters and digits.", nameof(id));
        return id;
    }

    private static void ValidateUris(IReadOnlyList<string> uris, int maximum, bool playableOnly, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(uris);
        if (uris.Count > maximum || (!allowEmpty && uris.Count == 0))
            throw new ArgumentException("This Spotify request accepts " + (allowEmpty ? "0" : "1") + " to " + maximum + " URIs.", nameof(uris));
        foreach (string uri in uris) ValidateUri(uri, playableOnly);
    }

    private static void ValidateUri(string uri, bool playableOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        string[] parts = uri.Split(':');
        if (parts.Length != 3 || parts[0] != "spotify" || string.IsNullOrWhiteSpace(parts[2])
            || (playableOnly && parts[1] is not ("track" or "episode"))
            || (!playableOnly && parts[1] is not ("track" or "episode" or "album" or "artist" or "playlist" or "show" or "audiobook" or "user")))
            throw new ArgumentException("Use a supported Spotify URI.", nameof(uri));
    }

    private static byte[] Body(Action<Utf8JsonWriter> writeProperties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteUris(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static readonly SpotifyTrack UnavailableTrack = new("", "", "Unavailable item", [], null, 0, false, false, "track", "https://open.spotify.com/");
}
