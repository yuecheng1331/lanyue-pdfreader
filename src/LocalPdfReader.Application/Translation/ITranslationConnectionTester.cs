using LocalPdfReader.Application.Configuration;

namespace LocalPdfReader.Application.Translation;

public interface ITranslationConnectionTester
{
    Task<ProviderConnectionResult> TestAsync(
        TranslationSettings settings,
        string apiKey,
        CancellationToken cancellationToken);
}

public sealed record ProviderConnectionResult(
    bool IsSuccess,
    ProviderConnectionError Error,
    string Message);

public enum ProviderConnectionError
{
    None,
    InvalidSettings,
    AuthenticationFailed,
    RateLimited,
    Timeout,
    NetworkUnavailable,
    ProviderError,
    InvalidResponse
}
