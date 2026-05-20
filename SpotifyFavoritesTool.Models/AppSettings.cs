using System.Text.Json.Serialization;

namespace SpotifyFavoritesTool;

public sealed class AppSettings
{
    public string ClientId { get; set; } = string.Empty;

    [JsonIgnore]
    public string AccessToken { get; set; } = string.Empty;

    [JsonIgnore]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonIgnore]
    public string DeepLApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public string LibreTranslateApiKey { get; set; } = string.Empty;

    public string ProtectedAccessToken { get; set; } = string.Empty;
    public string ProtectedRefreshToken { get; set; } = string.Empty;
    public string ProtectedDeepLApiKey { get; set; } = string.Empty;
    public string ProtectedLibreTranslateApiKey { get; set; } = string.Empty;
    public TranslationProvider TranslationProvider { get; set; } = TranslationProvider.DeepL;
    public string DeepLTargetLanguage { get; set; } = "RU";
    public string LibreTranslateEndpoint { get; set; } = "https://libretranslate.com/translate";
    public string GrantedScopes { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; } = DateTimeOffset.MinValue;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public uint LikeHotkeyVirtualKey { get; set; } = 0xB3;
    public string LikeHotkeyDisplayName { get; set; } = string.Empty;
    public uint FavoriteStatusHotkeyVirtualKey { get; set; }
    public string FavoriteStatusHotkeyDisplayName { get; set; } = string.Empty;
}
