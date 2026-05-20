using System.Windows;
using System.Windows.Input;

namespace SpotifyFavoritesTool;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly SpotifyAuthService _auth;
    private uint _favoriteHotkeyVirtualKey;
    private string _favoriteHotkeyDisplayName;
    private uint _favoriteStatusHotkeyVirtualKey;
    private string _favoriteStatusHotkeyDisplayName;

    public SettingsWindow(SettingsStore settings, SpotifyAuthService auth)
    {
        InitializeComponent();
        _settings = settings;
        _auth = auth;
        _favoriteHotkeyVirtualKey = _settings.Current.LikeHotkeyVirtualKey;
        _favoriteHotkeyDisplayName = GetHotkeyDisplayName(_favoriteHotkeyVirtualKey, _settings.Current.LikeHotkeyDisplayName);
        _favoriteStatusHotkeyVirtualKey = _settings.Current.FavoriteStatusHotkeyVirtualKey;
        _favoriteStatusHotkeyDisplayName = GetHotkeyDisplayName(_favoriteStatusHotkeyVirtualKey, _settings.Current.FavoriteStatusHotkeyDisplayName);

        ClientIdBox.Text = _settings.Current.ClientId;
        DeepLApiKeyBox.Text = _settings.Current.DeepLApiKey;
        DeepLTargetLanguageBox.Text = string.IsNullOrWhiteSpace(_settings.Current.DeepLTargetLanguage)
            ? "RU"
            : _settings.Current.DeepLTargetLanguage;
        RedirectUriBox.Text = SpotifyAuthService.RedirectUri;
        FavoriteHotkeyBox.Text = _favoriteHotkeyDisplayName;
        FavoriteStatusHotkeyBox.Text = _favoriteStatusHotkeyDisplayName;
        UpdateStatus();
    }

    public event EventHandler? AuthChanged;
    public event EventHandler? SettingsChanged;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings())
        {
            return;
        }

        UpdateStatus("Настройки сохранены.");
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings())
        {
            return;
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            LoginButton.IsEnabled = false;
            UpdateStatus("Открыл браузер для входа в Spotify...");
            await _auth.LoginAsync();
            SaveSettings();
            UpdateStatus("Spotify подключен.");
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            AuthChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            UpdateStatus("Spotify не подключен.");
            System.Windows.MessageBox.Show(this, ex.Message, "Spotify Favorites Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        _auth.Logout();
        UpdateStatus("Токены Spotify удалены.");
        AuthChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Tab)
        {
            return;
        }

        var focusedElement = Keyboard.FocusedElement;
        if (ReferenceEquals(focusedElement, FavoriteHotkeyBox)
            || ReferenceEquals(focusedElement, FavoriteStatusHotkeyBox))
        {
            return;
        }

        e.Handled = true;
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

    private void FavoriteHotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FavoriteHotkeyBox.Text = "Нажми клавишу Избранного...";
    }

    private void FavoriteHotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!TryCaptureHotkey(e, out var virtualKey, out var displayName))
        {
            FavoriteHotkeyBox.Text = "Эту клавишу не удалось распознать";
            return;
        }

        _favoriteHotkeyVirtualKey = virtualKey;
        _favoriteHotkeyDisplayName = displayName;
        FavoriteHotkeyBox.Text = _favoriteHotkeyDisplayName;
    }

    private void FavoriteStatusHotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FavoriteStatusHotkeyBox.Text = "Нажми клавишу статуса...";
    }

    private void FavoriteStatusHotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!TryCaptureHotkey(e, out var virtualKey, out var displayName))
        {
            FavoriteStatusHotkeyBox.Text = "Эту клавишу не удалось распознать";
            return;
        }

        _favoriteStatusHotkeyVirtualKey = virtualKey;
        _favoriteStatusHotkeyDisplayName = displayName;
        FavoriteStatusHotkeyBox.Text = _favoriteStatusHotkeyDisplayName;
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _favoriteHotkeyVirtualKey = 0;
        _favoriteHotkeyDisplayName = HotkeyFormatter.Format(0);
        FavoriteHotkeyBox.Text = _favoriteHotkeyDisplayName;
    }

    private void ClearStatusHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _favoriteStatusHotkeyVirtualKey = 0;
        _favoriteStatusHotkeyDisplayName = HotkeyFormatter.Format(0);
        FavoriteStatusHotkeyBox.Text = _favoriteStatusHotkeyDisplayName;
    }

    private bool SaveSettings()
    {
        if (_favoriteHotkeyVirtualKey != 0
            && _favoriteHotkeyVirtualKey == _favoriteStatusHotkeyVirtualKey)
        {
            System.Windows.MessageBox.Show(
                this,
                "Клавиша Избранного и клавиша статуса должны отличаться.",
                "Spotify Favorites Tool",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _settings.Current.ClientId = ClientIdBox.Text.Trim();
        _settings.Current.DeepLApiKey = DeepLApiKeyBox.Text.Trim();
        _settings.Current.DeepLTargetLanguage = string.IsNullOrWhiteSpace(DeepLTargetLanguageBox.Text)
            ? "RU"
            : DeepLTargetLanguageBox.Text.Trim().ToUpperInvariant();
        _settings.Current.LikeHotkeyVirtualKey = _favoriteHotkeyVirtualKey;
        _settings.Current.LikeHotkeyDisplayName = GetHotkeyDisplayName(_favoriteHotkeyVirtualKey, _favoriteHotkeyDisplayName);
        _settings.Current.FavoriteStatusHotkeyVirtualKey = _favoriteStatusHotkeyVirtualKey;
        _settings.Current.FavoriteStatusHotkeyDisplayName = GetHotkeyDisplayName(_favoriteStatusHotkeyVirtualKey, _favoriteStatusHotkeyDisplayName);
        _settings.Save();
        return true;
    }

    private void UpdateStatus(string? prefix = null)
    {
        var account = _auth.HasRefreshToken ? "Аккаунт подключен." : "Аккаунт не подключен.";
        if (_auth.KnowsGrantedScopes && !_auth.HasRequiredScopes)
        {
            account += " Не хватает прав на Избранное или управление плеером: нажми «Выйти», затем «Войти в Spotify».";
        }

        StatusText.Text = string.IsNullOrWhiteSpace(prefix)
            ? account
            : $"{prefix} {account}";
        SpotifyConnectionStatusText.Text = _auth.HasRefreshToken
            ? "Статус: подключен"
            : "Статус: не подключен";
        LoginButton.Visibility = _auth.HasRefreshToken ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = _auth.HasRefreshToken ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool TryCaptureHotkey(System.Windows.Input.KeyEventArgs e, out uint virtualKey, out string displayName)
    {
        e.Handled = true;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        displayName = HotkeyFormatter.Format(virtualKey);
        return virtualKey != 0;
    }

    private static string GetHotkeyDisplayName(uint virtualKey, string savedDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(savedDisplayName))
        {
            return savedDisplayName;
        }

        return HotkeyFormatter.Format(virtualKey);
    }
}
