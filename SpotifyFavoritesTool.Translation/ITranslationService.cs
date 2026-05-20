namespace SpotifyFavoritesTool;

public interface ITranslationService
{
    string DisplayName { get; }

    Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}
