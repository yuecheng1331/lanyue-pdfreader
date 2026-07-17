namespace LocalPdfReader.Application.Translation;

public interface IWordTranslationService
{
    Task<WordTranslationResult?> TryTranslateAsync(
        string sourceText,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public sealed record WordTranslationResult(string SourceText, string TranslatedText, string ProviderName);
