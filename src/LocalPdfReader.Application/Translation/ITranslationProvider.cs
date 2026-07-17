using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Translation;

public interface ITranslationProvider
{
    IAsyncEnumerable<TranslationChunk> TranslateAsync(
        ProviderTranslationRequest request,
        CancellationToken cancellationToken);
}

public sealed record ProviderTranslationRequest(
    TranslationRequest Translation,
    TranslationSettings Settings,
    string ApiKey);
