using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyFavoritesTool;

public sealed class DeepLTranslationService
{
    private const string TranslateEndpoint = "https://api-free.deepl.com/v2/translate";
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<string> TranslateAsync(
        string authKey,
        string text,
        string targetLanguage = "RU",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authKey))
        {
            throw new InvalidOperationException("DeepL API key не указан.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TranslateEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", authKey.Trim());
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text,
            ["target_lang"] = targetLanguage
        });

        using var response = await Http.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, responseBody);
        }

        var result = JsonSerializer.Deserialize<TranslationResponse>(responseBody, JsonOptions);
        return result?.Translations?.FirstOrDefault()?.Text ?? string.Empty;
    }

    private static Exception CreateException(HttpStatusCode statusCode, string body)
    {
        return statusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new InvalidOperationException("DeepL отклонил API key."),
            HttpStatusCode.TooManyRequests => new InvalidOperationException("DeepL ограничил запросы."),
            _ => new InvalidOperationException($"DeepL вернул {(int)statusCode}: {body}")
        };
    }

    private sealed class TranslationResponse
    {
        [JsonPropertyName("translations")]
        public TranslationItem[]? Translations { get; set; }
    }

    private sealed class TranslationItem
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
