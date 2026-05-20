namespace SpotifyFavoritesTool;

public sealed record TranslationRequest(
    string Text,
    string TargetLanguage,
    string? ApiKey = null,
    Uri? Endpoint = null);
