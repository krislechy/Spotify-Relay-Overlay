using System.Text.Json.Serialization;

namespace SpotifyFavoritesTool;

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public sealed class PlaybackResponse
{
    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("progress_ms")]
    public int? ProgressMs { get; set; }

    [JsonPropertyName("context")]
    public SpotifyPlaybackContext? Context { get; set; }

    [JsonPropertyName("item")]
    public SpotifyItem? Item { get; set; }
}

public sealed class SpotifyPlaybackContext
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

public sealed class SpotifyItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("artists")]
    public SpotifyArtist[]? Artists { get; set; }

    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }
}

public sealed class SpotifyArtist
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class SpotifyAlbum
{
    [JsonPropertyName("images")]
    public SpotifyImage[]? Images { get; set; }
}

public sealed class SpotifyImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}

public sealed class PlaylistTracksResponse
{
    [JsonPropertyName("items")]
    public PlaylistTrackItem[]? Items { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }
}

public sealed class PlaylistTrackItem
{
    [JsonPropertyName("track")]
    public SpotifyItem? Track { get; set; }
}

public sealed class QueueResponse
{
    [JsonPropertyName("currently_playing")]
    public SpotifyItem? CurrentlyPlaying { get; set; }

    [JsonPropertyName("queue")]
    public SpotifyItem[]? Queue { get; set; }
}
