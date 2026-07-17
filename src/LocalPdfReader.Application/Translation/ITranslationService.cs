using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Translation;

public interface ITranslationService
{
    IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);

    Task CancelAsync(Guid requestId, CancellationToken cancellationToken);
}

public enum TranslationError
{
    InvalidRequest,
    NetworkAccessDisabled,
    MissingCredential,
    AuthenticationFailed,
    RateLimited,
    Timeout,
    NetworkUnavailable,
    ProviderError,
    InvalidResponse
}

public sealed class TranslationException(TranslationError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public TranslationError Error { get; } = error;
}
