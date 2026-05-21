using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace SpotifyFavoritesTool;

public partial class OverlayWindow : Window, IDisposable
{
    private const double CollapsedHeight = 150;
    private const double ExpandedHeight = 438;
    private const double MaxAlbumArtPreviewSize = 320;

    private readonly ObservableCollection<OverlayTrackListItem> _cachedTracks = [];
    private readonly Dictionary<string, BitmapImage> _albumArtCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lyricsTranslationCache = new(StringComparer.Ordinal);
    private readonly LrclibLyricsService _lyricsService = new();
    private readonly ToolTip _lyricsTranslationToolTip = new();
    private ITranslationService _translationService = new DeepLTranslationService();
    private string? _requestedAlbumImageUrl;
    private PlaybackTrack? _currentTrack;
    private CancellationTokenSource? _karaokeLoadCancellation;
    private CancellationTokenSource? _lyricsTranslationCancellation;
    private TranslationProvider _translationProvider = TranslationProvider.DeepL;
    private string _translationApiKey = string.Empty;
    private string _translationTargetLanguage = "RU";
    private Uri? _translationEndpoint;
    private bool _isHistoryExpanded;
    private bool _isKaraokeExpanded;
    private bool _disposed;

    public event EventHandler? FavoriteRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? TrackSyncRequested;
    public event EventHandler<TrackRequestedEventArgs>? CachedTrackPlayRequested;
    public event EventHandler<TrackRequestedEventArgs>? CachedTrackFavoriteRequested;

    public OverlayWindow(
        TranslationProvider translationProvider = TranslationProvider.DeepL,
        string? deepLApiKey = null,
        string? libreTranslateApiKey = null,
        string? libreTranslateEndpoint = null,
        string? targetLanguage = null)
    {
        InitializeComponent();
        CachedTracksList.ItemsSource = _cachedTracks;
        SetTranslationSettings(translationProvider, deepLApiKey, libreTranslateApiKey, libreTranslateEndpoint, targetLanguage);
        _lyricsTranslationToolTip.Placement = PlacementMode.Relative;
        _lyricsTranslationToolTip.PlacementTarget = PlainLyricsText;
        _lyricsTranslationToolTip.StaysOpen = false;
        ShowMessage("Overlay готов", "Жду текущий трек");
    }

    public void SetTranslationSettings(
        TranslationProvider translationProvider,
        string? deepLApiKey,
        string? libreTranslateApiKey,
        string? libreTranslateEndpoint,
        string? targetLanguage)
    {
        _translationProvider = translationProvider;
        _translationService = translationProvider switch
        {
            TranslationProvider.LibreTranslate => new LibreTranslationService(),
            TranslationProvider.GoogleTranslate => new GoogleTranslationService(),
            _ => new DeepLTranslationService()
        };
        _translationApiKey = translationProvider == TranslationProvider.LibreTranslate
            ? libreTranslateApiKey ?? string.Empty
            : deepLApiKey ?? string.Empty;
        _translationTargetLanguage = string.IsNullOrWhiteSpace(targetLanguage)
            ? "RU"
            : targetLanguage.Trim().ToUpperInvariant();
        _translationEndpoint = Uri.TryCreate(libreTranslateEndpoint, UriKind.Absolute, out var endpoint)
            ? endpoint
            : null;
        _lyricsTranslationCache.Clear();
    }

    public void ShowTrack(PlaybackTrack track)
    {
        var previousTrackUri = _currentTrack?.Uri;
        _currentTrack = track;
        TrackTitle.Text = track.Name;
        ArtistText.Text = track.Artists;
        SetFavoriteState(track.IsLiked);
        SetPlaybackState(track.IsPlaying);
        _requestedAlbumImageUrl = track.AlbumImageUrl;
        _ = SetAlbumArtAsync(track.AlbumImageUrl);
        if (_isKaraokeExpanded && !string.Equals(previousTrackUri, track.Uri, StringComparison.Ordinal))
        {
            _ = LoadLyricsAsync(track);
        }
    }

    public void ShowMessage(string title, string message)
    {
        _currentTrack = null;
        TrackTitle.Text = title;
        ArtistText.Text = message;
        FavoriteButton.Content = "♡";
        SetPlaybackState(isPlaying: true);
        _requestedAlbumImageUrl = null;
        AlbumArt.Source = null;
        AlbumArtPreview.Source = null;
        AlbumArtPreviewToolTip.IsEnabled = false;
        ResetAlbumArtPreviewSize();
        AlbumPlaceholder.Visibility = Visibility.Visible;
    }

    public void SetCachedTracks(IEnumerable<PlaybackTrack> tracks)
    {
        SetTrackList(new OverlayTrackList("Очередь Spotify", Array.Empty<OverlayTrackListItem>(), IsPlaybackContext: true));
    }

    public void SetTrackList(OverlayTrackList trackList)
    {
        _cachedTracks.Clear();
        HistoryTitleText.Text = trackList.Title;
        foreach (var item in trackList.Tracks)
        {
            _cachedTracks.Add(item);
        }

        TrackListEmptyText.Text = trackList.EmptyMessage ?? "Spotify не отдал плейлист для текущего трека.";
        TrackListEmptyText.Visibility = _cachedTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollToNowPlayingTrack();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AlbumArt.Source = null;
        _albumArtCache.Clear();
        _lyricsTranslationCache.Clear();
        _cachedTracks.Clear();
        _karaokeLoadCancellation?.Cancel();
        _karaokeLoadCancellation?.Dispose();
        _lyricsTranslationCancellation?.Cancel();
        _lyricsTranslationCancellation?.Dispose();
        _lyricsTranslationToolTip.IsOpen = false;
        FavoriteRequested = null;
        PreviousRequested = null;
        PlayPauseRequested = null;
        NextRequested = null;
        TrackSyncRequested = null;
        CachedTrackPlayRequested = null;
        CachedTrackFavoriteRequested = null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PlaceNearTopRight();
        NativeMethods.ForceTopmost(new WindowInteropHelper(this).Handle);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
        }
    }

    private void OverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if Windows has already ended the mouse capture.
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        FavoriteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        PreviousRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        PlayPauseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        NextRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TrackSyncButton_Click(object sender, RoutedEventArgs e)
    {
        TrackSyncRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HistoryToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedPanel(_isHistoryExpanded ? OverlayExpandedPanel.None : OverlayExpandedPanel.History);
        HistoryToggleButton.ToolTip = _isHistoryExpanded ? "Скрыть историю" : "Показать историю";
    }

    private void KaraokeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedPanel(_isKaraokeExpanded ? OverlayExpandedPanel.None : OverlayExpandedPanel.Karaoke);
        KaraokeToggleButton.ToolTip = _isKaraokeExpanded ? "Скрыть текст" : "Показать текст";
        if (_isKaraokeExpanded && _currentTrack is { } track)
        {
            _ = LoadLyricsAsync(track);
        }
    }

    private void SetExpandedPanel(OverlayExpandedPanel panel)
    {
        _isHistoryExpanded = panel == OverlayExpandedPanel.History;
        _isKaraokeExpanded = panel == OverlayExpandedPanel.Karaoke;

        HistoryPanel.Visibility = _isHistoryExpanded ? Visibility.Visible : Visibility.Collapsed;
        KaraokePanel.Visibility = _isKaraokeExpanded ? Visibility.Visible : Visibility.Collapsed;
        HistoryRow.Height = panel == OverlayExpandedPanel.None ? new GridLength(0) : new GridLength(288);
        Height = panel == OverlayExpandedPanel.None ? CollapsedHeight : ExpandedHeight;

        if (!_isKaraokeExpanded)
        {
            CancelLyricsLoad();
        }

        if (_isHistoryExpanded)
        {
            ScrollToNowPlayingTrack();
        }
    }

    private void ScrollToNowPlayingTrack()
    {
        if (!_isHistoryExpanded || _cachedTracks.Count == 0)
        {
            return;
        }

        var currentItem = _cachedTracks.FirstOrDefault(item => item.Section == OverlayTrackSection.NowPlaying)
            ?? _cachedTracks.FirstOrDefault(item => item.Track.IsPlaying == true);
        if (currentItem is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            CachedTracksList.ScrollIntoView(currentItem);
            if (CachedTracksList.ItemContainerGenerator.ContainerFromItem(currentItem) is ListBoxItem container)
            {
                container.Focus();
            }
        }, DispatcherPriority.ContextIdle);
    }

    private void CachedTrackPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Controls.Button { Tag: PlaybackTrack track })
        {
            CachedTrackPlayRequested?.Invoke(this, new TrackRequestedEventArgs(track));
        }
        else if (sender is Controls.Button { Tag: OverlayTrackListItem item })
        {
            CachedTrackPlayRequested?.Invoke(this, new TrackRequestedEventArgs(item.Track));
        }
    }

    private void CachedTrackFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Controls.Button { Tag: PlaybackTrack track })
        {
            CachedTrackFavoriteRequested?.Invoke(this, new TrackRequestedEventArgs(track));
        }
        else if (sender is Controls.Button { Tag: OverlayTrackListItem item })
        {
            CachedTrackFavoriteRequested?.Invoke(this, new TrackRequestedEventArgs(item.Track));
        }
    }

    private async Task LoadLyricsAsync(PlaybackTrack track)
    {
        _karaokeLoadCancellation?.Cancel();
        _karaokeLoadCancellation?.Dispose();
        _karaokeLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _karaokeLoadCancellation.Token;

        try
        {
            PlainLyricsText.Text = string.Empty;
            PlainLyricsText.Visibility = Visibility.Visible;
            KaraokeStatusText.Text = "поиск";

            var lyrics = await _lyricsService.GetLyricsAsync(track, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !string.Equals(_currentTrack?.Uri, track.Uri, StringComparison.Ordinal))
            {
                return;
            }

            ShowLyrics(lyrics);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PlainLyricsText.Text = "Не удалось загрузить текст песни.";
            PlainLyricsText.Visibility = Visibility.Visible;
            KaraokeStatusText.Text = ex is JsonException ? "ошибка формата текста" : "ошибка";
        }
    }

    private void ShowLyrics(KaraokeLyrics lyrics)
    {
        _lyricsTranslationCancellation?.Cancel();
        _lyricsTranslationToolTip.IsOpen = false;

        if (!string.IsNullOrWhiteSpace(lyrics.DisplayText))
        {
            PlainLyricsText.Text = lyrics.DisplayText;
            PlainLyricsText.Visibility = Visibility.Visible;
            KaraokeStatusText.Text = string.IsNullOrWhiteSpace(lyrics.SourceName) ? "найдено" : lyrics.SourceName;
            return;
        }

        PlainLyricsText.Text = "Текст для этого трека не найден.";
        PlainLyricsText.Visibility = Visibility.Visible;
        KaraokeStatusText.Text = "не найдено";
    }

    private void CancelLyricsLoad()
    {
        _karaokeLoadCancellation?.Cancel();
        _lyricsTranslationCancellation?.Cancel();
        _lyricsTranslationToolTip.IsOpen = false;
    }

    private void PlainLyricsText_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var selectedText = PlainLyricsText.SelectedText?.Trim();
        _lyricsTranslationCancellation?.Cancel();
        _lyricsTranslationToolTip.IsOpen = false;

        if (string.IsNullOrWhiteSpace(selectedText) || selectedText.Length < 2)
        {
            return;
        }

        if (_translationProvider == TranslationProvider.DeepL && string.IsNullOrWhiteSpace(_translationApiKey))
        {
            ShowLyricsTranslationToolTip("DeepL API key не указан в настройках.", PlainLyricsText.SelectionStart);
            return;
        }

        _lyricsTranslationCancellation?.Dispose();
        _lyricsTranslationCancellation = new CancellationTokenSource();
        _ = TranslateSelectedLyricsAsync(selectedText, PlainLyricsText.SelectionStart, _lyricsTranslationCancellation.Token);
    }

    private async Task TranslateSelectedLyricsAsync(string text, int selectionStart, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            var cacheKey = $"{_translationService.DisplayName}\n{_translationTargetLanguage}\n{text}";
            if (_lyricsTranslationCache.TryGetValue(cacheKey, out var cachedTranslation))
            {
                ShowLyricsTranslationToolTip(cachedTranslation, selectionStart);
                return;
            }

            var translated = await _translationService.TranslateAsync(
                new TranslationRequest(text, _translationTargetLanguage, _translationApiKey, _translationEndpoint),
                cancellationToken);
            if (!cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(translated))
            {
                _lyricsTranslationCache[cacheKey] = translated;
                ShowLyricsTranslationToolTip(translated, selectionStart);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowLyricsTranslationToolTip(ex.Message, selectionStart);
        }
    }

    private void ShowLyricsTranslationToolTip(string text, int selectionStart)
    {
        _lyricsTranslationToolTip.Content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };
        SetLyricsTranslationToolTipPlacement(selectionStart);
        _lyricsTranslationToolTip.IsOpen = true;
    }

    private void SetLyricsTranslationToolTipPlacement(int selectionStart)
    {
        var characterIndex = Math.Clamp(selectionStart, 0, Math.Max(PlainLyricsText.Text.Length - 1, 0));
        var selectionRect = PlainLyricsText.GetRectFromCharacterIndex(characterIndex, trailingEdge: false);
        if (selectionRect.IsEmpty)
        {
            selectionRect = new Rect(0, 0, 0, 0);
        }

        _lyricsTranslationToolTip.Placement = PlacementMode.Relative;
        _lyricsTranslationToolTip.PlacementTarget = PlainLyricsText;
        _lyricsTranslationToolTip.HorizontalOffset = Math.Max(0, selectionRect.Left);
        _lyricsTranslationToolTip.VerticalOffset = Math.Max(0, selectionRect.Bottom + 6);
    }

    private void AlbumArt_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AlbumArt.Source = null;
        AlbumPlaceholder.Visibility = Visibility.Visible;
    }

    private void SetFavoriteState(bool? isLiked)
    {
        FavoriteButton.Content = isLiked == true ? "♥" : "♡";
    }

    private void SetPlaybackState(bool? isPlaying)
    {
        var showPause = isPlaying != false;
        PlayGlyph.Visibility = showPause ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyphLeft.Visibility = showPause ? Visibility.Visible : Visibility.Collapsed;
        PauseGlyphRight.Visibility = showPause ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SetAlbumArtAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            AlbumArt.Source = null;
            AlbumArtPreview.Source = null;
            AlbumArtPreviewToolTip.IsEnabled = false;
            ResetAlbumArtPreviewSize();
            AlbumPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        if (_albumArtCache.TryGetValue(imageUrl, out var cachedImage))
        {
            SetAlbumArt(cachedImage);
            return;
        }

        try
        {
            var image = await AlbumArtLoader.LoadAsync(imageUrl);
            if (_disposed || image is null || !string.Equals(_requestedAlbumImageUrl, imageUrl, StringComparison.Ordinal))
            {
                return;
            }

            _albumArtCache[imageUrl] = image;
            SetAlbumArt(image);
        }
        catch
        {
            if (_disposed || !string.Equals(_requestedAlbumImageUrl, imageUrl, StringComparison.Ordinal))
            {
                return;
            }

            AlbumArt.Source = null;
            AlbumArtPreview.Source = null;
            AlbumArtPreviewToolTip.IsEnabled = false;
            ResetAlbumArtPreviewSize();
            AlbumPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void SetAlbumArt(BitmapImage image)
    {
        AlbumArt.Source = image;
        AlbumArtPreview.Source = image;
        AlbumArtPreviewToolTip.IsEnabled = true;
        SetAlbumArtPreviewSize(image);
        AlbumPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void SetAlbumArtPreviewSize(BitmapImage image)
    {
        AlbumArtPreviewFrame.Width = GetPreviewDimension(image.PixelWidth);
        AlbumArtPreviewFrame.Height = GetPreviewDimension(image.PixelHeight);
    }

    private void ResetAlbumArtPreviewSize()
    {
        AlbumArtPreviewFrame.Width = MaxAlbumArtPreviewSize;
        AlbumArtPreviewFrame.Height = MaxAlbumArtPreviewSize;
    }

    private static double GetPreviewDimension(int pixelSize)
    {
        return pixelSize <= 0 ? MaxAlbumArtPreviewSize : Math.Min(pixelSize, MaxAlbumArtPreviewSize);
    }

    private void PlaceNearTopRight()
    {
        var workArea = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Top + 18;
    }
}

public sealed class TrackRequestedEventArgs : EventArgs
{
    public TrackRequestedEventArgs(PlaybackTrack track)
    {
        Track = track;
    }

    public PlaybackTrack Track { get; }
}

internal enum OverlayExpandedPanel
{
    None,
    History,
    Karaoke
}
