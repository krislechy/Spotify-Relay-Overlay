using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpotifyFavoritesTool;

public sealed class SpotifyClient
{
    private const string ApiRoot = "https://api.spotify.com/v1";
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SpotifyAuthService _auth;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public SpotifyClient(SpotifyAuthService auth)
    {
        _auth = auth;
    }

    public async Task<PlaybackTrack?> GetCurrentTrackOrNullAsync(CancellationToken cancellationToken = default)
    {
        if (_auth.KnowsGrantedScopes && !_auth.HasPlaybackReadScopes)
        {
            throw new InvalidOperationException(BuildScopeError("полного состояния плеера", SpotifyAuthService.PlaybackReadScopes));
        }

        var response = await SendAsync(
            HttpMethod.Get,
            $"{ApiRoot}/me/player?additional_types=track",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var playback = JsonSerializer.Deserialize<PlaybackResponse>(response.Body, JsonOptions);
        var item = playback?.Item;
        if (item?.Id is null || item.Uri is null || item.Name is null)
        {
            throw new InvalidOperationException("Не удалось получить текущий трек Spotify.");
        }

        if (!string.Equals(item.Type, "track", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Сейчас играет не трек.");
        }

        return CreateTrack(item, playback?.Context?.Uri, playback?.IsPlaying == true, playback?.ProgressMs);
    }

    public async Task<bool> GetTrackLikedStateAsync(PlaybackTrack track, CancellationToken cancellationToken = default)
    {
        return await IsTrackLikedAsync(track, cancellationToken);
    }

    public async Task SetTrackFavoriteAsync(PlaybackTrack track, bool isLiked, CancellationToken cancellationToken = default)
    {
        await SetTrackLikedAsync(track, isLiked, cancellationToken);
    }

    public async Task<IReadOnlyList<PlaybackTrack>> GetPlaylistTracksAsync(string contextUri, CancellationToken cancellationToken = default)
    {
        if (_auth.KnowsGrantedScopes && !_auth.HasPlaylistReadScopes)
        {
            throw new InvalidOperationException(BuildScopeError("чтения плейлистов", SpotifyAuthService.PlaylistReadScopes));
        }

        var playlistId = TryGetPlaylistId(contextUri)
            ?? throw new ArgumentException("Spotify context is not a playlist.", nameof(contextUri));

        try
        {
            var tracks = new List<PlaybackTrack>();
            var nextUrl = $"{ApiRoot}/playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50&additional_types=track";
            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                var response = await SendAsync(HttpMethod.Get, nextUrl, cancellationToken);
                var page = JsonSerializer.Deserialize<PlaylistTracksResponse>(response.Body, JsonOptions);
                if (page?.Items is not null)
                {
                    tracks.AddRange(page.Items
                        .Select(item => item.Track)
                        .Where(item => item?.Id is not null && item.Uri is not null && item.Name is not null)
                        .Where(item => string.Equals(item!.Type, "track", StringComparison.OrdinalIgnoreCase))
                        .Select(item => CreateTrack(item!, contextUri, isPlaying: false, progressMs: null)));
                }

                nextUrl = page?.Next;
            }

            return tracks;
        }
        catch (Exception ex) when (ex is SpotifyApiException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Spotify не дал прочитать текущий плейлист через Web API." + Environment.NewLine +
                $"Context: {contextUri}" + Environment.NewLine +
                $"Playlist id: {playlistId}" + Environment.NewLine +
                $"Granted scopes: {_auth.GrantedScopes}" + Environment.NewLine +
                "Если права выданы, вероятно этот context является персонализированным/служебным плейлистом Spotify, который playback показывает как плейлист, но playlist endpoint не отдает." + Environment.NewLine +
                ex.Message,
                ex);
        }
    }

    public async Task<IReadOnlyList<PlaybackTrack>> GetQueueTracksAsync(PlaybackTrack? currentTrack, CancellationToken cancellationToken = default)
    {
        if (_auth.KnowsGrantedScopes && !_auth.HasPlaybackReadScopes)
        {
            throw new InvalidOperationException(BuildScopeError("очереди Spotify", SpotifyAuthService.PlaybackReadScopes));
        }

        var response = await SendAsync(HttpMethod.Get, $"{ApiRoot}/me/player/queue", cancellationToken);
        var queue = JsonSerializer.Deserialize<QueueResponse>(response.Body, JsonOptions);
        var tracks = new List<PlaybackTrack>();

        if (queue?.CurrentlyPlaying is { } currentlyPlaying && IsTrackItem(currentlyPlaying))
        {
            tracks.Add(CreateTrack(currentlyPlaying, currentTrack?.ContextUri, currentTrack?.IsPlaying == true, currentTrack?.ProgressMs));
        }

        if (queue?.Queue is not null)
        {
            tracks.AddRange(queue.Queue
                .Where(IsTrackItem)
                .Select(item => CreateTrack(item, currentTrack?.ContextUri, isPlaying: false, progressMs: null)));
        }

        return tracks;
    }

    public async Task SkipToPreviousTrackAsync(CancellationToken cancellationToken = default)
    {
        await SendPlaybackCommandAsync(HttpMethod.Post, "previous", cancellationToken);
    }

    public async Task SkipToNextTrackAsync(CancellationToken cancellationToken = default)
    {
        await SendPlaybackCommandAsync(HttpMethod.Post, "next", cancellationToken);
    }

    public async Task PausePlaybackAsync(CancellationToken cancellationToken = default)
    {
        await SendPlaybackCommandAsync(HttpMethod.Put, "pause", cancellationToken);
    }

    public async Task ResumePlaybackAsync(CancellationToken cancellationToken = default)
    {
        await SendPlaybackCommandAsync(HttpMethod.Put, "play", cancellationToken);
    }

    public async Task PlayTrackAsync(PlaybackTrack track, CancellationToken cancellationToken = default)
    {
        if (_auth.KnowsGrantedScopes && !_auth.HasPlaybackControlScopes)
        {
            throw new InvalidOperationException(BuildScopeError("управления плеером", SpotifyAuthService.PlaybackControlScopes));
        }

        var body = JsonSerializer.Serialize(CreatePlaybackBody(track), JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        await SendAsync(HttpMethod.Put, $"{ApiRoot}/me/player/play", cancellationToken, content);
    }

    private async Task<bool> IsTrackLikedAsync(PlaybackTrack track, CancellationToken cancellationToken)
    {
        var uri = Uri.EscapeDataString(track.Uri);
        var response = await SendAsync(HttpMethod.Get, $"{ApiRoot}/me/library/contains?uris={uri}", cancellationToken);
        var values = JsonSerializer.Deserialize<bool[]>(response.Body, JsonOptions);
        return values is { Length: > 0 } && values[0];
    }

    private async Task SetTrackLikedAsync(PlaybackTrack track, bool isLiked, CancellationToken cancellationToken)
    {
        if (_auth.KnowsGrantedScopes && !_auth.HasFavoriteScopes)
        {
            throw new InvalidOperationException(BuildScopeError("Избранного", SpotifyAuthService.FavoriteScopes));
        }

        var uri = Uri.EscapeDataString(track.Uri);
        var method = isLiked ? HttpMethod.Put : HttpMethod.Delete;
        await SendAsync(method, $"{ApiRoot}/me/library?uris={uri}", cancellationToken);
    }

    private async Task SendPlaybackCommandAsync(HttpMethod method, string command, CancellationToken cancellationToken)
    {
        if (_auth.KnowsGrantedScopes && !_auth.HasPlaybackControlScopes)
        {
            throw new InvalidOperationException(BuildScopeError("управления плеером", SpotifyAuthService.PlaybackControlScopes));
        }

        await SendAsync(method, $"{ApiRoot}/me/player/{command}", cancellationToken);
    }

    private static PlaybackTrack CreateTrack(SpotifyItem item, string? contextUri, bool isPlaying, int? progressMs)
    {
        var artists = item.Artists is { Length: > 0 }
            ? string.Join(", ", item.Artists.Select(artist => artist.Name).Where(name => !string.IsNullOrWhiteSpace(name)))
            : "Unknown artist";
        var image = item.Album?.Images?
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderBy(image => Math.Abs((image.Width ?? 300) - 300))
            .FirstOrDefault()
            ?.Url;

        return new PlaybackTrack(item.Id!, item.Uri!, item.Name!, artists, image, contextUri, IsPlaying: isPlaying, DurationMs: item.DurationMs, ProgressMs: progressMs);
    }

    private static bool IsTrackItem(SpotifyItem? item)
    {
        return item?.Id is not null
            && item.Uri is not null
            && item.Name is not null
            && string.Equals(item.Type, "track", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPlaylistContext(string? contextUri)
    {
        return TryGetPlaylistId(contextUri) is not null;
    }

    private static string? TryGetPlaylistId(string? contextUri)
    {
        const string prefix = "spotify:playlist:";
        if (string.IsNullOrWhiteSpace(contextUri))
        {
            return null;
        }

        if (contextUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var spotifyId = contextUri[prefix.Length..];
            return string.IsNullOrWhiteSpace(spotifyId) ? null : spotifyId;
        }

        if (!Uri.TryCreate(contextUri, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var playlistIndex = Array.FindIndex(segments, segment => string.Equals(segment, "playlist", StringComparison.OrdinalIgnoreCase));
        if (playlistIndex < 0 || playlistIndex + 1 >= segments.Length)
        {
            return null;
        }

        var id = segments[playlistIndex + 1];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static object CreatePlaybackBody(PlaybackTrack track)
    {
        if (track.ContextUri?.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new
            {
                context_uri = track.ContextUri,
                offset = new { uri = track.Uri }
            };
        }

        return new { uris = new[] { track.Uri } };
    }

    private async Task<ApiResponse> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken, HttpContent? content = null)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var token = await _auth.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Spotify не подключен. Открой настройки и войди в аккаунт.");
            }

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = content;

            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                throw new SpotifyRateLimitException(GetRetryAfter(response), DescribeEndpoint(url), body);
            }

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
            {
                throw CreateApiException(response.StatusCode, body, url);
            }

            return new ApiResponse(response.StatusCode, body);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static Exception CreateApiException(HttpStatusCode statusCode, string body, string url)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new InvalidOperationException(
                "Spotify вернул 401. Открой настройки, нажми «Выйти», потом «Войти в Spotify» и выдай новые права."),
            HttpStatusCode.Forbidden => new SpotifyApiException(
                statusCode,
                $"Endpoint: {DescribeEndpoint(url)}. Ответ Spotify: {FormatBody(body)}"),
            _ => new SpotifyApiException(statusCode, body)
        };
    }

    private static string BuildScopeError(string feature, string scopes)
    {
        return $"Spotify вернул 403 Forbidden. Для {feature} нужны права {scopes}. " +
            "Открой настройки, нажми «Выйти», затем «Войти в Spotify» и подтверди доступ.";
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static string DescribeEndpoint(string url)
    {
        if (url.Contains("/me/library/contains", StringComparison.OrdinalIgnoreCase))
        {
            return "проверка Избранного";
        }

        if (url.Contains("/me/library", StringComparison.OrdinalIgnoreCase))
        {
            return "изменение Избранного";
        }

        if (url.Contains("/me/player/currently-playing", StringComparison.OrdinalIgnoreCase))
        {
            return "получение текущего трека";
        }

        if (url.Contains("/me/player?", StringComparison.OrdinalIgnoreCase))
        {
            return "получение состояния плеера";
        }

        if (url.Contains("/me/player/queue", StringComparison.OrdinalIgnoreCase))
        {
            return "получение очереди Spotify";
        }

        if (url.Contains("/me/player/previous", StringComparison.OrdinalIgnoreCase))
        {
            return "переключение на предыдущий трек";
        }

        if (url.Contains("/me/player/next", StringComparison.OrdinalIgnoreCase))
        {
            return "переключение на следующий трек";
        }

        if (url.Contains("/me/player/pause", StringComparison.OrdinalIgnoreCase))
        {
            return "пауза Spotify";
        }

        if (url.Contains("/me/player/play", StringComparison.OrdinalIgnoreCase))
        {
            return "воспроизведение Spotify";
        }

        if (url.Contains("/playlists/", StringComparison.OrdinalIgnoreCase))
        {
            return "получение треков плейлиста";
        }

        return "Spotify API";
    }

    private static string FormatBody(string body)
    {
        return string.IsNullOrWhiteSpace(body) ? "пустой ответ" : body;
    }

    private sealed record ApiResponse(HttpStatusCode StatusCode, string Body);
}
