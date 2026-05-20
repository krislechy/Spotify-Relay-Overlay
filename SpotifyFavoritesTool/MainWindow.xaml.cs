using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SpotifyFavoritesTool;

public partial class MainWindow : Window
{
    private static readonly TimeSpan TrackMonitorInterval = TimeSpan.FromSeconds(8);
    private const double CompactHeight = 390;
    private const double ExpandedLogHeight = 540;
    private const string GenericErrorTitle = "Произошла ошибка";
    private const string GenericErrorMessage = "Смотри в главном окне";

    private readonly SettingsStore _settings = new();
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyClient _spotify;
    private readonly FavoriteTrackService _favorites;
    private readonly ActivityLog _activityLog = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly ToastPresenter _toasts = new();
    private readonly AsyncActionGate _userActionGate = new();
    private readonly AsyncActionGate _trackMonitorGate = new();
    private readonly AsyncActionGate _overlayRefreshGate = new();
    private readonly AsyncActionGate _overlayListRefreshGate = new();
    private bool _overlayListRefreshPending;

    private SettingsWindow? _settingsWindow;
    private OverlayWindow? _overlayWindow;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _isExiting;
    private DispatcherTimer? _trackMonitorTimer;
    private TrayIconController? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        _auth = new SpotifyAuthService(_settings);
        _spotify = new SpotifyClient(_auth);
        _favorites = new FavoriteTrackService(_spotify);
        ActivityLogList.ItemsSource = _activityLog.Entries;
        _trayIcon = new TrayIconController(Dispatcher, BringMainWindowToFront, ShowSettingsWindow, ExitApplication);
        Log("Приложение запущено.");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowPosition();
        StartTrackMonitorIfReady();
        UpdateStatus();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        RegisterHotkeys();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            Log("Окно скрыто в трей.");
            return;
        }

        StopTrackMonitor(clearCache: false);
        SaveWindowPosition();
        CloseOverlayWindow();
        _hotkeys.Unregister();
        _source?.RemoveHook(WndProc);
        _trayIcon?.Dispose();
        _toasts.Dispose();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        Log("Окно свернуто.");
    }

    private void CloseToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow is { IsVisible: true })
        {
            CloseOverlayWindow();
            return;
        }

        OpenOverlayWindow();
    }

    private void ActivityLogToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var shouldShow = ActivityLogPanel.Visibility != Visibility.Visible;
        ActivityLogPanel.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        ActivityLogToggleButton.ToolTip = shouldShow ? "Скрыть журнал действий" : "Показать журнал действий";
        Height = shouldShow ? ExpandedLogHeight : CompactHeight;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

    private void ShowSettingsWindow()
    {
        BringMainWindowToFront();

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            Log("Окно настроек уже открыто.");
            return;
        }

        Log("Открыты настройки.");
        _settingsWindow = new SettingsWindow(_settings, _auth)
        {
            Owner = this
        };
        _settingsWindow.AuthChanged += (_, _) =>
        {
            _favorites.ClearCache();
            _overlayWindow?.ShowMessage(
                _auth.HasRefreshToken ? "Spotify подключен" : "Spotify отключен",
                _auth.HasRefreshToken ? "Жду текущий трек" : "Открой настройки и войди в Spotify");
            _ = RefreshOverlayTrackListAsync();
            StartTrackMonitorIfReady();
            UpdateStatus();
            Log(_auth.HasRefreshToken ? "Spotify подключен, кеш треков очищен." : "Spotify отключен, кеш треков очищен.");
        };
        _settingsWindow.SettingsChanged += (_, _) =>
        {
            RegisterHotkeys();
            _overlayWindow?.SetTranslationSettings(
                _settings.Current.TranslationProvider,
                _settings.Current.DeepLApiKey,
                _settings.Current.LibreTranslateApiKey,
                _settings.Current.LibreTranslateEndpoint,
                _settings.Current.DeepLTargetLanguage);
            StartTrackMonitorIfReady();
            UpdateStatus("Настройки сохранены.");
            Log("Настройки сохранены.");
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private async Task ToggleFavoriteAsync()
    {
        if (!_userActionGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            try
            {
                Log("Нажата клавиша Избранного: получаю текущий трек.");
                var result = await _favorites.ToggleCurrentTrackAsync();
                UpdateOverlayTrack(result.Track);
                _toasts.Show(result);
                Log($"Статус Избранного перед изменением: {DescribeFavoriteStatusSource(result.PreviousStatusSource)}.");
                Log($"{result.Message}: {result.Track.Name}.");
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Spotify вернул 429: {ex.Endpoint}.", ex.Message);
            }
            catch (Exception ex)
            {
                _toasts.ShowError("Избранное недоступно", ex.Message);
                Log("Ошибка Избранного.", ex.Message);
            }
        }
    }

    private async Task ShowFavoriteStatusAsync()
    {
        if (!_userActionGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            try
            {
                Log("Нажата клавиша статуса: получаю текущий трек.");
                var result = await _favorites.GetCurrentTrackWithFavoriteStatusAsync();
                if (result is null)
                {
                    _toasts.ShowError("Статус Избранного неизвестен", "Spotify сейчас ничего не играет.");
                    Log("Статус не показан: Spotify сейчас ничего не играет.");
                    return;
                }

                UpdateOverlayTrack(result.Track);
                ShowFavoriteStatusToast(result.Track);
                Log($"Статус Избранного получен: {DescribeFavoriteStatusSource(result.Source)}.");
                Log($"Показан статус Избранного: {result.Track.Name}.");
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Spotify вернул 429: {ex.Endpoint}.", ex.Message);
            }
            catch (Exception ex)
            {
                _toasts.ShowError("Статус Избранного недоступен", ex.Message);
                Log("Ошибка статуса Избранного.", ex.Message);
            }
        }
    }

    private void StartTrackMonitorIfReady()
    {
        if (!_auth.HasRefreshToken || _isExiting)
        {
            StopTrackMonitor(clearCache: true);
            return;
        }

        _trackMonitorTimer ??= CreateTrackMonitorTimer();
        if (!_trackMonitorTimer.IsEnabled)
        {
            _trackMonitorTimer.Start();
            Log("Мониторинг трека запущен.");
        }
    }

    private DispatcherTimer CreateTrackMonitorTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TrackMonitorInterval
        };
        timer.Tick += TrackMonitorTimer_Tick;
        return timer;
    }

    private void StopTrackMonitor(bool clearCache)
    {
        var wasEnabled = _trackMonitorTimer?.IsEnabled == true;
        _trackMonitorTimer?.Stop();
        if (clearCache)
        {
            _favorites.ClearCache();
        }

        if (wasEnabled)
        {
            Log(clearCache ? "Мониторинг трека остановлен, кеш очищен." : "Мониторинг трека остановлен.");
        }
    }

    private async void TrackMonitorTimer_Tick(object? sender, EventArgs e)
    {
        await CheckTrackChangeAsync();
    }

    private async Task CheckTrackChangeAsync()
    {
        if (!_auth.HasRefreshToken || !_trackMonitorGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            try
            {
                var result = await _favorites.GetChangedTrackWithFavoriteStatusAsync();
                if (result is not null)
                {
                    UpdateOverlayTrack(result.Track);
                    ShowTrackChangedToast(result.Track);
                    Log($"Трек сменился: {result.Track.Name}.");
                    Log($"Статус Избранного для нового трека: {DescribeFavoriteStatusSource(result.Source)}.");
                }
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Мониторинг остановлен: Spotify вернул 429 ({ex.Endpoint}).", ex.Message);
            }
            catch (Exception ex)
            {
                StopTrackMonitor(clearCache: false);
                Log("Мониторинг трека остановлен.", ex.Message);
            }
        }
    }

    private void ShowFavoriteStatusToast(PlaybackTrack track)
    {
        var isLiked = track.IsLiked == true;
        var message = isLiked ? "Уже в Избранном" : "Не в Избранном";
        _toasts.ShowFavoriteStatus(track);
    }

    private void ShowTrackChangedToast(PlaybackTrack track)
    {
        _toasts.ShowTrackChanged(track);
    }

    private void OpenOverlayWindow()
    {
        _overlayWindow = new OverlayWindow(
            _settings.Current.TranslationProvider,
            _settings.Current.DeepLApiKey,
            _settings.Current.LibreTranslateApiKey,
            _settings.Current.LibreTranslateEndpoint,
            _settings.Current.DeepLTargetLanguage);
        SubscribeOverlayEvents(_overlayWindow);
        _overlayWindow.SetTrackList(new OverlayTrackList("Очередь Spotify", Array.Empty<OverlayTrackListItem>(), IsPlaybackContext: true));
        ShowInitialOverlayContent(_overlayWindow);

        _overlayWindow.Show();
        UpdateOverlayButton();
        Log("Overlay открыт.");
        _ = RefreshOverlayAsync();
    }

    private void CloseOverlayWindow()
    {
        _overlayWindow?.Close();
    }

    private void SubscribeOverlayEvents(OverlayWindow overlay)
    {
        overlay.FavoriteRequested += OverlayWindow_FavoriteRequested;
        overlay.PreviousRequested += OverlayWindow_PreviousRequested;
        overlay.PlayPauseRequested += OverlayWindow_PlayPauseRequested;
        overlay.NextRequested += OverlayWindow_NextRequested;
        overlay.CachedTrackPlayRequested += OverlayWindow_CachedTrackPlayRequested;
        overlay.CachedTrackFavoriteRequested += OverlayWindow_CachedTrackFavoriteRequested;
        overlay.Closed += OverlayWindow_Closed;
    }

    private void UnsubscribeOverlayEvents(OverlayWindow overlay)
    {
        overlay.FavoriteRequested -= OverlayWindow_FavoriteRequested;
        overlay.PreviousRequested -= OverlayWindow_PreviousRequested;
        overlay.PlayPauseRequested -= OverlayWindow_PlayPauseRequested;
        overlay.NextRequested -= OverlayWindow_NextRequested;
        overlay.CachedTrackPlayRequested -= OverlayWindow_CachedTrackPlayRequested;
        overlay.CachedTrackFavoriteRequested -= OverlayWindow_CachedTrackFavoriteRequested;
        overlay.Closed -= OverlayWindow_Closed;
    }

    private void ShowInitialOverlayContent(OverlayWindow overlay)
    {
        if (_favorites.LastObservedTrack is { } cachedTrack)
        {
            overlay.ShowTrack(cachedTrack);
            return;
        }

        overlay.ShowMessage("Overlay запущен", "Получаю текущий трек");
    }

    private async Task RefreshOverlayAsync()
    {
        if (_overlayWindow is null || !_overlayRefreshGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            if (!_auth.HasRefreshToken)
            {
                _overlayWindow?.ShowMessage("Spotify не подключен", "Открой настройки и войди в Spotify");
                return;
            }

            try
            {
                var result = await _favorites.GetCurrentTrackWithFavoriteStatusAsync();
                if (result is null)
                {
                    _favorites.ResetObservation();
                    _overlayWindow?.ShowMessage("Spotify молчит", "Сейчас ничего не играет");
                    Log("Overlay не обновлен: Spotify сейчас ничего не играет.");
                    return;
                }

                UpdateOverlayTrack(result.Track);
                Log($"Overlay обновлен: {result.Track.Name}.");
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                ShowOverlayError();
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Overlay не обновлен: Spotify вернул 429 ({ex.Endpoint}).", ex.Message);
            }
            catch (Exception ex)
            {
                ShowOverlayError();
                Log("Overlay не обновлен.", ex.Message);
            }
        }
    }

    private void UpdateOverlayTrack(PlaybackTrack track)
    {
        var cachedTrack = _favorites.StoreObservedTrack(track);
        _overlayWindow?.ShowTrack(cachedTrack);
        _ = RefreshOverlayTrackListAsync();
    }

    private void RefreshOverlayCache()
    {
        _ = RefreshOverlayTrackListAsync();
    }

    private async Task RefreshOverlayTrackListAsync()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        if (!_overlayListRefreshGate.TryEnter(out var action))
        {
            _overlayListRefreshPending = true;
            return;
        }

        using (action)
        {
            do
            {
                _overlayListRefreshPending = false;
                try
                {
                    var trackList = await _favorites.GetOverlayTrackListAsync();
                    _overlayWindow?.SetTrackList(trackList);
                    var recentCount = trackList.Tracks.Count(item => item.Section == OverlayTrackSection.RecentlyPlayed);
                    var currentCount = trackList.Tracks.Count(item => item.Section == OverlayTrackSection.NowPlaying);
                    var queueCount = trackList.Tracks.Count(item => item.Section == OverlayTrackSection.Queue);
                    Log(
                        $"Список Overlay: {recentCount} недавно, {currentCount} сейчас, {queueCount} далее.",
                        trackList.EmptyMessage);
                }
                catch (SpotifyRateLimitException ex)
                {
                    _overlayWindow?.SetTrackList(new OverlayTrackList("Очередь Spotify", Array.Empty<OverlayTrackListItem>(), IsPlaybackContext: true));
                    Log($"Список Overlay не обновлен: Spotify вернул 429 ({ex.Endpoint}).", ex.Message);
                }
                catch (Exception ex)
                {
                    _overlayWindow?.SetTrackList(new OverlayTrackList("Очередь Spotify", Array.Empty<OverlayTrackListItem>(), IsPlaybackContext: true));
                    Log("Список Overlay не обновлен: текущий плейлист недоступен.", ex.Message);
                }
            }
            while (_overlayListRefreshPending && _overlayWindow is not null);
        }
    }

    private void OverlayWindow_FavoriteRequested(object? sender, EventArgs e)
    {
        _ = ToggleFavoriteAsync();
    }

    private void OverlayWindow_PreviousRequested(object? sender, EventArgs e)
    {
        _ = RunPlaybackCommandAsync(PlaybackCommand.Previous);
    }

    private void OverlayWindow_PlayPauseRequested(object? sender, EventArgs e)
    {
        _ = RunPlaybackCommandAsync(PlaybackCommand.PlayPause);
    }

    private void OverlayWindow_NextRequested(object? sender, EventArgs e)
    {
        _ = RunPlaybackCommandAsync(PlaybackCommand.Next);
    }

    private void OverlayWindow_CachedTrackPlayRequested(object? sender, TrackRequestedEventArgs e)
    {
        _ = PlayCachedTrackAsync(e.Track);
    }

    private void OverlayWindow_CachedTrackFavoriteRequested(object? sender, TrackRequestedEventArgs e)
    {
        _ = ToggleCachedTrackFavoriteAsync(e.Track);
    }

    private async Task RunPlaybackCommandAsync(PlaybackCommand command)
    {
        if (!_userActionGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            try
            {
                await ExecutePlaybackCommandAsync(command);
                await Task.Delay(650);
                await RefreshOverlayAsync();
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                ShowOverlayError();
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Команда Overlay остановлена: Spotify вернул 429 ({ex.Endpoint}).", ex.Message);
            }
            catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && command == PlaybackCommand.PlayPause)
            {
                const string message = "Сейчас ничего не играет.";
                _overlayWindow?.ShowMessage("Spotify", message);
                Log("Пауза/воспроизведение из Overlay недоступны: сейчас ничего не играет.");
            }
            catch (Exception ex) when (command == PlaybackCommand.PlayPause && IsSpotifyForbidden(ex))
            {
                ShowOverlayError();
                _toasts.ShowError("Контекст недоступен", GenericErrorMessage);
                Log("Spotify не смог продолжить прошлый контекст. Запусти трек из «Воспроизводилось ранее».", ex.Message);
            }
            catch (Exception ex)
            {
                ShowOverlayError();
                _toasts.ShowError("Команда Overlay не выполнена", ex.Message);
                Log("Команда Overlay не выполнена.", ex.Message);
            }
        }
    }

    private async Task ExecutePlaybackCommandAsync(PlaybackCommand command)
    {
        switch (command)
        {
            case PlaybackCommand.Previous:
                await _spotify.SkipToPreviousTrackAsync();
                Log("Overlay: предыдущий трек.");
                break;
            case PlaybackCommand.PlayPause:
                await TogglePlaybackAsync();
                break;
            case PlaybackCommand.Next:
                await _spotify.SkipToNextTrackAsync();
                Log("Overlay: следующий трек.");
                break;
        }
    }

    private async Task TogglePlaybackAsync()
    {
        var knownTrack = _favorites.LastObservedTrack;
        if (knownTrack?.IsPlaying == true)
        {
            await _spotify.PausePlaybackAsync();
            UpdateKnownPlaybackState(knownTrack, isPlaying: false);
            Log("Overlay: пауза.");
            return;
        }

        await _spotify.ResumePlaybackAsync();
        UpdateKnownPlaybackState(knownTrack, isPlaying: true);
        Log("Overlay: воспроизведение.");
    }

    private void UpdateKnownPlaybackState(PlaybackTrack? knownTrack, bool isPlaying)
    {
        if (knownTrack is not null)
        {
            UpdateOverlayTrack(knownTrack.WithPlaybackState(isPlaying));
        }
    }

    private async Task PlayCachedTrackAsync(PlaybackTrack track)
    {
        if (!_userActionGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            try
            {
                await _spotify.PlayTrackAsync(track);
                Log($"Overlay: запущен трек из кеша: {track.Name}.");

                await Task.Delay(650);
                await RefreshOverlayAsync();
                await Task.Delay(1000);
                await RefreshOverlayTrackListAsync();
            }
            catch (SpotifyApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                const string message = "Сейчас ничего не играет.";
                _overlayWindow?.ShowMessage("Spotify", message);
                Log("Запуск трека из кеша недоступен: сейчас ничего не играет.");
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                ShowOverlayError();
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Запуск трека из кеша остановлен: Spotify вернул 429 ({ex.Endpoint}).", ex.Message);
            }
            catch (Exception ex)
            {
                ShowOverlayError();
                _toasts.ShowError("Трек не запущен", ex.Message);
                Log("Трек из кеша не запущен.", ex.Message);
            }
        }
    }

    private async Task ToggleCachedTrackFavoriteAsync(PlaybackTrack track)
    {
        if (!_userActionGate.TryEnter(out var action))
        {
            return;
        }

        using (action)
        {
            try
            {
                var result = await _favorites.ToggleCachedTrackAsync(track);
                RefreshOverlayCache();
                if (string.Equals(_favorites.LastObservedTrack?.Uri, result.Track.Uri, StringComparison.Ordinal))
                {
                    _overlayWindow?.ShowTrack(result.Track);
                }

                _toasts.Show(result);
                Log($"Overlay: {result.Message.ToLowerInvariant()} для трека из истории: {result.Track.Name}.");
            }
            catch (SpotifyRateLimitException ex)
            {
                StopTrackMonitor(clearCache: false);
                ShowOverlayError();
                _toasts.ShowError("Spotify ограничил запросы", ex.Message);
                Log($"Избранное для трека из истории остановлено: Spotify вернул 429 ({ex.Endpoint}).", ex.Message);
            }
            catch (Exception ex)
            {
                ShowOverlayError();
                _toasts.ShowError("Избранное не изменено", ex.Message);
                Log("Избранное для трека из истории не изменено.", ex.Message);
            }
        }
    }

    private void OverlayWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is OverlayWindow overlay)
        {
            UnsubscribeOverlayEvents(overlay);
        }

        if (ReferenceEquals(_overlayWindow, sender))
        {
            _overlayWindow = null;
        }

        UpdateOverlayButton();
        if (!_isExiting)
        {
            Log("Overlay закрыт.");
        }
    }

    private void UpdateOverlayButton()
    {
        OverlayButton.Content = _overlayWindow is { IsVisible: true }
            ? "Убрать Overlay"
            : "Запустить Overlay";
    }

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _hotkeys.Register(_hwnd, _settings.Current);
        var registration = _hotkeys.GetRegistrationMessage();
        Log(string.IsNullOrWhiteSpace(registration)
            ? "Горячие клавиши зарегистрированы."
            : $"Горячие клавиши зарегистрированы с предупреждением:{registration}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == NativeMethods.ShowExistingWindowMessage)
        {
            handled = true;
            BringMainWindowToFront();
            return IntPtr.Zero;
        }

        if (msg != NativeMethods.WmHotkey)
        {
            return IntPtr.Zero;
        }

        if (!_hotkeys.TryGetAction(wParam, out var action))
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (action)
        {
            case HotkeyAction.ToggleFavorite:
                _ = ToggleFavoriteAsync();
                break;
            case HotkeyAction.ShowFavoriteStatus:
                _ = ShowFavoriteStatusAsync();
                break;
        }

        return IntPtr.Zero;
    }

    private void BringMainWindowToFront()
    {
        if (!IsVisible)
        {
            Show();
            Log("Окно восстановлено из трея.");
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        NativeMethods.BringWindowToFront(_hwnd);
    }

    private void UpdateStatus(string? prefix = null)
    {
        var favoriteHotkey = !string.IsNullOrWhiteSpace(_settings.Current.LikeHotkeyDisplayName)
            ? _settings.Current.LikeHotkeyDisplayName
            : HotkeyFormatter.Format(_settings.Current.LikeHotkeyVirtualKey);
        var statusHotkey = !string.IsNullOrWhiteSpace(_settings.Current.FavoriteStatusHotkeyDisplayName)
            ? _settings.Current.FavoriteStatusHotkeyDisplayName
            : HotkeyFormatter.Format(_settings.Current.FavoriteStatusHotkeyVirtualKey);
        HotkeyText.Text = $"Избранное: {favoriteHotkey} · Статус: {statusHotkey}";

        var account = _auth.HasRefreshToken ? "Spotify подключен." : "Spotify не подключен.";
        var registration = _hotkeys.GetRegistrationMessage();
        var monitor = _trackMonitorTimer?.IsEnabled == true ? " Мониторинг трека: каждые 8 секунд." : string.Empty;
        var hint =
            $"• Клавиша «Избранное» добавляет или убирает текущий трек.{Environment.NewLine}" +
            $"• Клавиша «Статус» показывает текущий трек и состояние Избранного.{Environment.NewLine}" +
            $"• Кнопка Overlay открывает отдельное компактное окно поверх остальных окон.{Environment.NewLine}" +
            $"• Кнопка журнала показывает последние 50 действий и подробности ошибок.{monitor}{registration}";
        StatusText.Text = string.IsNullOrWhiteSpace(prefix)
            ? $"{account}{Environment.NewLine}{hint}"
            : $"{prefix} {account}{Environment.NewLine}{hint}";
    }

    private void ExitApplication()
    {
        Log("Выход из приложения.");
        _isExiting = true;
        Close();
    }

    private void RestoreWindowPosition()
    {
        var left = _settings.Current.WindowLeft;
        var top = _settings.Current.WindowTop;
        if (left.HasValue && top.HasValue && IsOnVirtualScreen(left.Value, top.Value))
        {
            Left = left.Value;
            Top = top.Value;
            return;
        }

        var workArea = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        Left = workArea.Left + (workArea.Width - Width) / 2.0;
        Top = workArea.Top + (workArea.Height - Height) / 2.0;
    }

    private void SaveWindowPosition()
    {
        _settings.Current.WindowLeft = Left;
        _settings.Current.WindowTop = Top;
        _settings.Save();
    }

    private static bool IsOnVirtualScreen(double left, double top)
    {
        return left >= SystemParameters.VirtualScreenLeft - 20
            && top >= SystemParameters.VirtualScreenTop - 20
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 1)] + "...";
    }

    private static bool IsSpotifyForbidden(Exception exception)
    {
        if (exception is SpotifyApiException { StatusCode: HttpStatusCode.Forbidden })
        {
            return true;
        }

        return exception.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeFavoriteStatusSource(FavoriteStatusSource source)
    {
        return source switch
        {
            FavoriteStatusSource.Cache => "из кеша",
            FavoriteStatusSource.SpotifyApi => "через Spotify API",
            _ => "неизвестно"
        };
    }

    private void ShowOverlayError()
    {
        _overlayWindow?.ShowMessage(GenericErrorTitle, GenericErrorMessage);
    }

    private void Log(string title, string? details = null)
    {
        _activityLog.Add(title, details);
    }
}

internal enum PlaybackCommand
{
    Previous,
    PlayPause,
    Next
}
