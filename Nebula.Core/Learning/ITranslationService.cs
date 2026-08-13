namespace Nebula.Core.Learning;

public interface ITranslationService
{
    Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default);
}
