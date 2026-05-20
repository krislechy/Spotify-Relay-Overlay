using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyFavoritesTool;

public sealed class DeepLTranslationService : ITranslationService
{
    private static readonly Uri TranslateEndpoint = new("https://api-free.deepl.com/v2/translate");
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string DisplayName => "DeepL";

    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new InvalidOperationException("DeepL API key не указан.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return string.Empty;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, TranslateEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", request.ApiKey.Trim());
        httpRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = request.Text,
            ["target_lang"] = request.TargetLanguage
        });

        using var response = await Http.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, responseBody);
        }

        var result = JsonSerializer.Deserialize<DeepLTranslationResponse>(responseBody, JsonOptions);
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

    private sealed class DeepLTranslationResponse
    {
        [JsonPropertyName("translations")]
        public DeepLTranslationItem[]? Translations { get; set; }
    }

    private sealed class DeepLTranslationItem
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
