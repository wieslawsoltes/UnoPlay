namespace UnoPlay.Spotify;

public sealed record SpotifyToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Scope, string ClientId)
{
    public override string ToString() => "Spotify session expires " + ExpiresAt.ToString("O");
}

public sealed record SpotifyAuthorizationRequest(Uri AuthorizationUri, Uri RedirectUri, string State, string CodeVerifier, DateTimeOffset CreatedAt)
{
    public override string ToString() => "Spotify authorization started " + CreatedAt.ToString("O");
}

public sealed record SpotifyProfile(string Id, string DisplayName, string? ImageUrl, string ExternalUrl);

public sealed record SpotifyArtist(string Id, string Name, string Uri, string ExternalUrl);

public sealed record SpotifyAlbum(string Id, string Name, string Uri, string? ImageUrl, string ExternalUrl, IReadOnlyList<SpotifyArtist> Artists);

public sealed record SpotifyTrack(string Id, string Uri, string Name, IReadOnlyList<SpotifyArtist> Artists,
    SpotifyAlbum? Album, int DurationMs, bool IsPlayable, bool IsLocal, string Type, string ExternalUrl,
    string? RestrictionReason = null, bool IsExplicit = false)
{
    public string ArtistName => string.Join(", ", Artists.Select(artist => artist.Name));
    public string? ImageUrl => Album?.ImageUrl;
}

public sealed record SpotifyPlaylist(string Id, string Uri, string Name, string Description, string OwnerId,
    string OwnerName, bool? IsPublic, bool Collaborative, string SnapshotId, int? TotalItems,
    string? ImageUrl, string ExternalUrl);

public sealed record SpotifyPlaylistItem(int Position, SpotifyTrack? Track, DateTimeOffset? AddedAt, string? AddedBy);

public sealed record SpotifySavedTrack(SpotifyTrack? Track, DateTimeOffset? AddedAt);

public sealed record SpotifyPage<T>(IReadOnlyList<T> Items, int Offset, int Limit, int Total, string? Next)
{
    public bool HasMore => Next is not null;
}

public sealed record SpotifySearchResults(SpotifyPage<SpotifyTrack> Tracks, SpotifyPage<SpotifyPlaylist> Playlists,
    SpotifyPage<SpotifyAlbum> Albums, SpotifyPage<SpotifyArtist> Artists);

public sealed record SpotifyDevice(string Id, string Name, string Type, bool IsActive, bool IsRestricted,
    int? VolumePercent, bool SupportsVolume);

public sealed record SpotifyPlaybackState(SpotifyDevice? Device, SpotifyTrack? Track, bool IsPlaying, int ProgressMs,
    string RepeatState, bool ShuffleState, string? ContextUri, IReadOnlySet<string> DisallowedActions);

public sealed record SpotifyQueue(SpotifyTrack? CurrentlyPlaying, IReadOnlyList<SpotifyTrack> Items);

/// <summary>Spotify removes all playlist occurrences of the specified URI.</summary>
public sealed record SpotifyPlaylistRemoval(string Uri);

public sealed class SpotifyApiException : Exception
{
    public SpotifyApiException(int statusCode, string message, string? reason = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        Reason = reason;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }
    public string? Reason { get; }
    public TimeSpan? RetryAfter { get; }
    public bool IsQuotaExceeded => Reason == "QUOTA_EXCEEDED";
}

public sealed class SpotifyAuthenticationException(string message, string? error = null) : Exception(message)
{
    public string? Error { get; } = error;
}
