using System.Text;
using System.Text.Json;

namespace SpotifyFavoritesTool;

public sealed class GoogleTranslationService : ITranslationService
{
    private static readonly Uri TranslateEndpoint = new("https://translate.googleapis.com/translate_a/single");
    private static readonly HttpClient Http = new();

    public string DisplayName => "GoogleTranslate";

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return string.Empty;
        }

        var targetLanguage = NormalizeTargetLanguage(request.TargetLanguage);
        var uri = new Uri($"{TranslateEndpoint}?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&dt=t&q={Uri.EscapeDataString(request.Text)}");

        using var response = await Http.GetAsync(uri, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Translate вернул {(int)response.StatusCode}: {responseBody}");
        }

        return ParseTranslation(responseBody);
    }

    private static string NormalizeTargetLanguage(string targetLanguage)
    {
        var normalized = targetLanguage.Trim().ToLowerInvariant();
        var dashIndex = normalized.IndexOf('-');
        return dashIndex > 0 ? normalized[..dashIndex] : normalized;
    }

    private static string ParseTranslation(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0
            || document.RootElement[0].ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var translatedText = new StringBuilder();
        foreach (var segment in document.RootElement[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array
                && segment.GetArrayLength() > 0
                && segment[0].ValueKind == JsonValueKind.String)
            {
                translatedText.Append(segment[0].GetString());
            }
        }

        return translatedText.ToString();
    }
}
