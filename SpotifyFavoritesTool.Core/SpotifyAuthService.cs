using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyFavoritesTool;

public sealed class SpotifyAuthService
{
    public const int RedirectPort = 53154;
    public const string RedirectUri = "http://127.0.0.1:53154/callback/";
    public const string PlaybackReadScopes = "user-read-currently-playing user-read-playback-state";
    public const string FavoriteScopes = $"{PlaybackReadScopes} user-library-read user-library-modify";
    public const string PlaybackControlScopes = "user-modify-playback-state";
    public const string PlaylistReadScopes = "playlist-read-private playlist-read-collaborative";
    public const string RequiredScopes = $"{FavoriteScopes} {PlaybackControlScopes} {PlaylistReadScopes}";

    private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private static readonly string[] RequiredScopeItems = RequiredScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static readonly HttpClient Http = new();

    private readonly SettingsStore _settings;

    public SpotifyAuthService(SettingsStore settings)
    {
        _settings = settings;
    }

    public bool HasClientId => !string.IsNullOrWhiteSpace(_settings.Current.ClientId);
    public bool HasRefreshToken => !string.IsNullOrWhiteSpace(_settings.Current.RefreshToken);
    public bool HasAnyToken => !string.IsNullOrWhiteSpace(_settings.Current.AccessToken) || HasRefreshToken;
    public string GrantedScopes => _settings.Current.GrantedScopes;
    public bool KnowsGrantedScopes => !string.IsNullOrWhiteSpace(_settings.Current.GrantedScopes);
    public bool HasPlaybackReadScopes => HasScopes(_settings.Current.GrantedScopes, PlaybackReadScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    public bool HasFavoriteScopes => HasScopes(_settings.Current.GrantedScopes, FavoriteScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    public bool HasPlaybackControlScopes => HasScopes(_settings.Current.GrantedScopes, PlaybackControlScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    public bool HasPlaylistReadScopes => HasScopes(_settings.Current.GrantedScopes, PlaylistReadScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    public bool HasRequiredScopes => HasScopes(_settings.Current.GrantedScopes, RequiredScopeItems);

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        if (!HasClientId)
        {
            throw new InvalidOperationException("Сначала укажи Spotify Client ID в настройках.");
        }

        var codeVerifier = CreateCodeVerifier();
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var state = CreateCodeVerifier();

        using var listener = new TcpListener(IPAddress.Loopback, RedirectPort);
        listener.Start();

        var authorizeUrl = BuildAuthorizeUrl(codeChallenge, state);
        Process.Start(new ProcessStartInfo
        {
            FileName = authorizeUrl,
            UseShellExecute = true
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        using var client = await listener.AcceptTcpClientAsync(linked.Token);
        var callback = await ReadCallbackAsync(client, linked.Token);
        await SendBrowserResponseAsync(client, callback.Error is null, linked.Token);

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            throw new InvalidOperationException($"Spotify вернул ошибку авторизации: {callback.Error}");
        }

        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Spotify вернул неожиданный state. Авторизация остановлена.");
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            throw new InvalidOperationException("Spotify не вернул authorization code.");
        }

        await ExchangeCodeAsync(callback.Code, codeVerifier, cancellationToken);
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Current.AccessToken))
        {
            return HasRefreshToken ? await RefreshAsync(cancellationToken) : null;
        }

        if (_settings.Current.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _settings.Current.AccessToken;
        }

        return HasRefreshToken ? await RefreshAsync(cancellationToken) : null;
    }

    public void Logout()
    {
        _settings.ClearTokens();
    }

    private string BuildAuthorizeUrl(string codeChallenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _settings.Current.ClientId.Trim(),
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = RequiredScopes,
            ["show_dialog"] = "true",
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = codeChallenge,
            ["state"] = state
        };

        return $"{AuthorizeEndpoint}?{ToQueryString(query)}";
    }

    private async Task ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = _settings.Current.ClientId.Trim(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier
        };

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(fields), cancellationToken);
        await SaveTokenResponseAsync(response, keepExistingRefreshToken: false, cancellationToken);
    }

    private async Task<string> RefreshAsync(CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = _settings.Current.ClientId.Trim(),
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _settings.Current.RefreshToken
        };

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(fields), cancellationToken);
        await SaveTokenResponseAsync(response, keepExistingRefreshToken: true, cancellationToken);
        return _settings.Current.AccessToken;
    }

    private async Task SaveTokenResponseAsync(HttpResponseMessage response, bool keepExistingRefreshToken, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify не выдал токен ({(int)response.StatusCode}): {body}");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new InvalidOperationException("Spotify вернул пустой token response.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Spotify token response не содержит access_token.");
        }

        _settings.Current.AccessToken = token.AccessToken;
        if (!keepExistingRefreshToken || !string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            _settings.Current.RefreshToken = token.RefreshToken ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(token.Scope))
        {
            _settings.Current.GrantedScopes = token.Scope;
        }

        _settings.Current.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 30));
        _settings.Save();

        if (KnowsGrantedScopes && !HasRequiredScopes)
        {
            _settings.ClearTokens();
            throw new InvalidOperationException(
                $"Spotify выдал токен без нужных прав. Нужны scopes: {RequiredScopes}. " +
                "Открой настройки, нажми «Выйти», затем «Войти в Spotify» и подтверди доступ.");
        }
    }

    private static bool HasScopes(string grantedScopes, IEnumerable<string> requiredScopes)
    {
        if (string.IsNullOrWhiteSpace(grantedScopes))
        {
            return false;
        }

        var granted = grantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return requiredScopes.All(granted.Contains);
    }

    private static async Task<CallbackResult> ReadCallbackAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            throw new InvalidOperationException("Локальный callback получил пустой HTTP запрос.");
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
            // Drain headers before sending the browser response.
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Локальный callback получил некорректный HTTP запрос.");
        }

        var uri = new Uri($"http://127.0.0.1:{RedirectPort}{parts[1]}");
        var query = ParseQuery(uri.Query);
        query.TryGetValue("code", out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("error", out var error);
        return new CallbackResult(code, state, error);
    }

    private static async Task SendBrowserResponseAsync(TcpClient client, bool success, CancellationToken cancellationToken)
    {
        var title = success ? "Spotify подключен" : "Не удалось подключить Spotify";
        var message = success
            ? "Можно закрыть эту вкладку и вернуться к приложению."
            : "Вернись к приложению и попробуй еще раз.";
        var html = $"""
            <!doctype html>
            <html lang="ru">
            <head><meta charset="utf-8"><title>{title}</title></head>
            <body style="font-family:Segoe UI,Arial,sans-serif;background:#101512;color:#f5faf7;padding:40px">
              <h1>{title}</h1>
              <p>{message}</p>
            </body>
            </html>
            """;

        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64Url(hash);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ToQueryString(Dictionary<string, string> values)
    {
        return string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private sealed record CallbackResult(string? Code, string? State, string? Error);
}
