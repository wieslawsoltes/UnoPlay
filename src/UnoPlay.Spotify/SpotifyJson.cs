using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnoPlay.Spotify;

/// <summary>Trim-safe token storage and parsing shared by the Web API and Web Playback SDK adapters.</summary>
public static class SpotifyJson
{
    public static string SerializeToken(SpotifyToken token) => JsonSerializer.Serialize(token, SpotifyJsonContext.Default.SpotifyToken);
    public static SpotifyToken? DeserializeToken(string json) => JsonSerializer.Deserialize(json, SpotifyJsonContext.Default.SpotifyToken);
    public static string SerializeAuthorization(SpotifyAuthorizationRequest request) => JsonSerializer.Serialize(request, SpotifyJsonContext.Default.SpotifyAuthorizationRequest);
    public static SpotifyAuthorizationRequest? DeserializeAuthorization(string json) => JsonSerializer.Deserialize(json, SpotifyJsonContext.Default.SpotifyAuthorizationRequest);

    public static SpotifyPlaybackState? ParsePlayback(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Playback(document.RootElement);
    }

    internal static async Task<JsonDocument> ReadDocumentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        if (content.Headers.ContentLength > maximumBytes) throw new SpotifyApiException(502, "Spotify returned a response larger than the supported page size.");
        using var buffer = new MemoryStream();
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > maximumBytes) throw new SpotifyApiException(502, "Spotify returned a response larger than the supported page size.");
            buffer.Write(bytes, 0, read);
        }
        if (buffer.Length == 0) return JsonDocument.Parse("null");
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { throw new SpotifyApiException(502, "Spotify returned an unreadable response. Try again later."); }
    }

    internal static JsonElement Property(JsonElement json, string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;
    internal static string? String(JsonElement json, string name) => Property(json, name).ValueKind == JsonValueKind.String ? Property(json, name).GetString() : null;
    internal static int Int(JsonElement json, string name, int fallback = 0) => Property(json, name).TryInt(fallback);
    internal static bool Bool(JsonElement json, string name, bool fallback = false) => Property(json, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    private static int TryInt(this JsonElement value, int fallback) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : fallback;
    internal static JsonElement[] Array(JsonElement json) => json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToArray() : [];
    internal static string External(JsonElement json, string type, string id) => String(Property(json, "external_urls"), "spotify") ?? (id.Length == 0 ? "https://open.spotify.com/" : "https://open.spotify.com/" + type + "/" + Uri.EscapeDataString(id));
    internal static string? Image(JsonElement json) => Array(Property(json, "images")).Select(image => String(image, "url")).FirstOrDefault(url => url is not null);
    private static DateTimeOffset? Date(JsonElement json, string name) => DateTimeOffset.TryParse(String(json, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;

    internal static SpotifyProfile Profile(JsonElement json)
    {
        string id = String(json, "id") ?? "";
        return new(id, String(json, "display_name") ?? id, Image(json), External(json, "user", id));
    }

    internal static SpotifyArtist Artist(JsonElement json)
    {
        string id = String(json, "id") ?? "";
        return new(id, String(json, "name") ?? "Unknown artist", String(json, "uri") ?? "", External(json, "artist", id));
    }

    internal static SpotifyAlbum Album(JsonElement json)
    {
        string id = String(json, "id") ?? "";
        return new(id, String(json, "name") ?? "", String(json, "uri") ?? "", Image(json), External(json, "album", id),
            Array(Property(json, "artists")).Select(Artist).ToArray());
    }

    internal static SpotifyTrack? Track(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        string type = String(json, "type") ?? "track";
        string id = String(json, "id") ?? "";
        string uri = String(json, "uri") ?? "";
        bool local = Bool(json, "is_local") || uri.StartsWith("spotify:local:", StringComparison.Ordinal);
        string? restriction = String(Property(json, "restrictions"), "reason");
        var albumJson = Property(json, "album");
        SpotifyAlbum? album = albumJson.ValueKind == JsonValueKind.Object ? Album(albumJson) : null;
        var artists = Array(Property(json, "artists")).Select(Artist).ToArray();
        if (type == "episode" && Property(json, "show").ValueKind == JsonValueKind.Object)
        {
            var show = Property(json, "show");
            string showName = String(show, "name") ?? "Podcast";
            artists = [new SpotifyArtist(String(show, "id") ?? "", showName, String(show, "uri") ?? "", External(show, "show", String(show, "id") ?? ""))];
            album = new(String(show, "id") ?? "", showName, String(show, "uri") ?? "", Image(json) ?? Image(show), External(show, "show", String(show, "id") ?? ""), artists);
        }
        return new(id, uri, String(json, "name") ?? "Unavailable item", artists, album, Int(json, "duration_ms"),
            Bool(json, "is_playable", !local && restriction is null && uri.Length > 0), local, type,
            External(json, type, id), restriction, Bool(json, "explicit"));
    }

    internal static SpotifyPlaylist Playlist(JsonElement json)
    {
        string id = String(json, "id") ?? "";
        var owner = Property(json, "owner");
        var items = Property(json, "items");
        if (items.ValueKind == JsonValueKind.Undefined) items = Property(json, "tracks");
        int? total = Property(items, "total").ValueKind == JsonValueKind.Number ? Int(items, "total") : null;
        bool? isPublic = Property(json, "public").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        return new(id, String(json, "uri") ?? "", String(json, "name") ?? "Untitled playlist", String(json, "description") ?? "",
            String(owner, "id") ?? "", String(owner, "display_name") ?? String(owner, "id") ?? "", isPublic,
            Bool(json, "collaborative"), String(json, "snapshot_id") ?? "", total, Image(json), External(json, "playlist", id));
    }

    internal static SpotifyPlaylistItem PlaylistItem(JsonElement json, int position)
    {
        var track = Property(json, "item");
        if (track.ValueKind == JsonValueKind.Undefined) track = Property(json, "track");
        var parsed = Track(track);
        if (parsed is not null && Bool(json, "is_local")) parsed = parsed with { IsLocal = true, IsPlayable = false };
        return new(position, parsed, Date(json, "added_at"), String(Property(json, "added_by"), "id"));
    }

    internal static SpotifySavedTrack SavedTrack(JsonElement json) => new(Track(Property(json, "track")), Date(json, "added_at"));

    internal static SpotifyPage<T> Page<T>(JsonElement json, Func<JsonElement, int, T> parse)
    {
        int offset = Int(json, "offset");
        var items = Array(Property(json, "items")).Select((item, index) => parse(item, offset + index)).ToArray();
        return new(items, offset, Int(json, "limit", items.Length), Int(json, "total", items.Length), String(json, "next"));
    }

    internal static SpotifyDevice Device(JsonElement json) => new(String(json, "id") ?? "", String(json, "name") ?? "Spotify device",
        String(json, "type") ?? "", Bool(json, "is_active"), Bool(json, "is_restricted"),
        Property(json, "volume_percent").ValueKind == JsonValueKind.Number ? Int(json, "volume_percent") : null,
        Bool(json, "supports_volume", Property(json, "volume_percent").ValueKind == JsonValueKind.Number));

    internal static SpotifyPlaybackState? Playback(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        var device = Property(json, "device");
        var actions = Property(json, "actions");
        var disallows = Property(actions, "disallows");
        if (disallows.ValueKind != JsonValueKind.Object) disallows = actions;
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (disallows.ValueKind == JsonValueKind.Object)
            foreach (var property in disallows.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.True) disabled.Add(property.Name);
        return new(device.ValueKind == JsonValueKind.Object ? Device(device) : null, Track(Property(json, "item")),
            Bool(json, "is_playing"), Int(json, "progress_ms"), String(json, "repeat_state") ?? "off",
            Bool(json, "shuffle_state"), String(Property(json, "context"), "uri"), disabled);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SpotifyToken))]
[JsonSerializable(typeof(SpotifyAuthorizationRequest))]
public partial class SpotifyJsonContext : JsonSerializerContext;
