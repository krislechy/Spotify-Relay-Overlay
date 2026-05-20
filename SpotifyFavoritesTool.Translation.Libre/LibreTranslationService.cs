using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyFavoritesTool;

public sealed class LibreTranslationService : ITranslationService
{
    private static readonly Uri DefaultEndpoint = new("https://libretranslate.com/translate");
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string DisplayName => "LibreTranslate";

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return string.Empty;
        }

        var endpoint = request.Endpoint ?? DefaultEndpoint;
        var body = new LibreTranslationRequest(
            request.Text,
            "auto",
            NormalizeTargetLanguage(request.TargetLanguage),
            "text",
            string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

        using var response = await Http.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, responseBody);
        }

        var result = JsonSerializer.Deserialize<LibreTranslationResponse>(responseBody, JsonOptions);
        return result?.TranslatedText ?? string.Empty;
    }

    private static string NormalizeTargetLanguage(string targetLanguage)
    {
        var normalized = targetLanguage.Trim().ToLowerInvariant();
        var dashIndex = normalized.IndexOf('-');
        return dashIndex > 0 ? normalized[..dashIndex] : normalized;
    }

    private static Exception CreateException(HttpStatusCode statusCode, string body)
    {
        return statusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new InvalidOperationException("LibreTranslate отклонил API key."),
            HttpStatusCode.TooManyRequests => new InvalidOperationException("LibreTranslate ограничил запросы."),
            _ => new InvalidOperationException($"LibreTranslate вернул {(int)statusCode}: {body}")
        };
    }

    private sealed record LibreTranslationRequest(
        [property: JsonPropertyName("q")] string Text,
        [property: JsonPropertyName("source")] string SourceLanguage,
        [property: JsonPropertyName("target")] string TargetLanguage,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("api_key")] string? ApiKey);

    private sealed class LibreTranslationResponse
    {
        [JsonPropertyName("translatedText")]
        public string? TranslatedText { get; set; }
    }
}
