namespace SpotifyFavoritesTool;

public sealed class FavoriteTrackService
{
    private readonly SpotifyClient _spotify;
    private readonly TrackFavoriteCache _cache = new();
    private string? _lastObservedTrackUri;

    public FavoriteTrackService(SpotifyClient spotify)
    {
        _spotify = spotify;
    }

    public PlaybackTrack? LastObservedTrack { get; private set; }
    public IReadOnlyList<PlaybackTrack> CachedTracks => _cache.GetTracks();

    public async Task<OverlayTrackList> GetOverlayTrackListAsync(CancellationToken cancellationToken = default)
    {
        var currentTrack = LastObservedTrack;
        var queueTracks = await _spotify.GetQueueTracksAsync(currentTrack, cancellationToken);
        var streamTracks = BuildPlaybackStream(queueTracks, currentTrack, CachedTracks);

        return new OverlayTrackList(
            "Очередь Spotify",
            BuildSectionedTracks(streamTracks, currentTrack),
            IsPlaybackContext: true,
            EmptyMessage: "Spotify не отдал текущую очередь.");
    }

    public void ResetObservation()
    {
        _lastObservedTrackUri = null;
        LastObservedTrack = null;
    }

    public void ClearCache()
    {
        _cache.Clear();
        ResetObservation();
    }

    public PlaybackTrack StoreObservedTrack(PlaybackTrack track)
    {
        return CacheObservedTrack(track);
    }

    public async Task<FavoriteToggleResult> ToggleCurrentTrackAsync()
    {
        var track = await GetCurrentTrackOrThrowAsync();
        return await ToggleTrackAsync(track, updateObservation: true);
    }

    public async Task<FavoriteToggleResult> ToggleCachedTrackAsync(PlaybackTrack track)
    {
        return await ToggleTrackAsync(track, updateObservation: false);
    }

    private async Task<FavoriteToggleResult> ToggleTrackAsync(PlaybackTrack track, bool updateObservation)
    {
        var currentStatus = await GetTrackWithFavoriteStatusAsync(track, updateObservation);
        var nextLiked = currentStatus.Track.IsLiked != true;

        await _spotify.SetTrackFavoriteAsync(currentStatus.Track, nextLiked);

        var updatedTrack = currentStatus.Track.WithFavoriteStatus(nextLiked);
        updatedTrack = CacheTrack(updatedTrack, updateObservation);

        var message = nextLiked ? "Добавлено в Избранное" : "Убрано из Избранного";
        return new FavoriteToggleResult(updatedTrack, message, currentStatus.Source);
    }

    public async Task<FavoriteStatusResult?> GetCurrentTrackWithFavoriteStatusAsync()
    {
        var track = await _spotify.GetCurrentTrackOrNullAsync();
        if (track is null)
        {
            return null;
        }

        return await GetTrackWithFavoriteStatusAsync(track, updateObservation: true);
    }

    public async Task<FavoriteStatusResult?> GetChangedTrackWithFavoriteStatusAsync()
    {
        var track = await _spotify.GetCurrentTrackOrNullAsync();
        if (track is null)
        {
            ResetObservation();
            return null;
        }

        if (string.Equals(_lastObservedTrackUri, track.Uri, StringComparison.Ordinal))
        {
            return null;
        }

        return await GetTrackWithFavoriteStatusAsync(track, updateObservation: true);
    }

    private async Task<PlaybackTrack> GetCurrentTrackOrThrowAsync()
    {
        return await _spotify.GetCurrentTrackOrNullAsync()
            ?? throw new InvalidOperationException("Spotify сейчас ничего не играет.");
    }

    private async Task<FavoriteStatusResult> GetTrackWithFavoriteStatusAsync(PlaybackTrack track, bool updateObservation)
    {
        if (_cache.TryGetWithFavoriteStatus(track, out var cachedTrack))
        {
            return new FavoriteStatusResult(CacheTrack(cachedTrack, updateObservation), FavoriteStatusSource.Cache);
        }

        var isLiked = await _spotify.GetTrackLikedStateAsync(track);
        return new FavoriteStatusResult(
            CacheTrack(track.WithFavoriteStatus(isLiked), updateObservation),
            FavoriteStatusSource.SpotifyApi);
    }

    private PlaybackTrack CacheObservedTrack(PlaybackTrack track)
    {
        _lastObservedTrackUri = track.Uri;
        return CacheTrack(track, updateObservation: true);
    }

    private PlaybackTrack CacheTrack(PlaybackTrack track, bool updateObservation)
    {
        var cachedTrack = _cache.Store(track);
        if (updateObservation || string.Equals(LastObservedTrack?.Uri, cachedTrack.Uri, StringComparison.Ordinal))
        {
            LastObservedTrack = cachedTrack;
        }

        return cachedTrack;
    }

    private IReadOnlyList<OverlayTrackListItem> BuildSectionedTracks(
        IReadOnlyList<OverlayTrackListItem> streamTracks,
        PlaybackTrack? currentTrack)
    {
        var enrichedTracks = _cache.EnrichTracks(streamTracks.Select(item => item.Track), currentTrack);
        return streamTracks
            .Zip(enrichedTracks, (item, track) => item with { Track = track })
            .ToArray();
    }

    private static IReadOnlyList<OverlayTrackListItem> BuildPlaybackStream(
        IReadOnlyList<PlaybackTrack> queueTracks,
        PlaybackTrack? currentTrack,
        IReadOnlyList<PlaybackTrack> cachedTracks)
    {
        var result = new List<OverlayTrackListItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queueUris = queueTracks
            .Select(track => track.Uri)
            .ToHashSet(StringComparer.Ordinal);

        AddSection(
            result,
            seen,
            cachedTracks.Where(track => !IsCurrentTrack(track, currentTrack) && !queueUris.Contains(track.Uri)),
            OverlayTrackSection.RecentlyPlayed);

        if (currentTrack is not null)
        {
            result.Add(new OverlayTrackListItem(currentTrack, OverlayTrackSection.NowPlaying, ShowSectionHeader: true));
            seen.Add(currentTrack.Uri);
        }

        AddSection(result, seen, queueTracks, OverlayTrackSection.Queue);

        return result;
    }

    private static void AddSection(
        List<OverlayTrackListItem> result,
        HashSet<string> seen,
        IEnumerable<PlaybackTrack> tracks,
        OverlayTrackSection section)
    {
        var hasHeader = false;
        foreach (var track in tracks)
        {
            if (!seen.Add(track.Uri))
            {
                continue;
            }

            result.Add(new OverlayTrackListItem(track, section, ShowSectionHeader: !hasHeader));
            hasHeader = true;
        }
    }

    private static bool IsCurrentTrack(PlaybackTrack track, PlaybackTrack? currentTrack)
    {
        return currentTrack is not null && string.Equals(track.Uri, currentTrack.Uri, StringComparison.Ordinal);
    }
}
