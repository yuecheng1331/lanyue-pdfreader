using System.Runtime.CompilerServices;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Translation;

public sealed class TranslationService(
    ISettingsService settingsService,
    ICredentialStore credentialStore,
    ITranslationProvider provider) : ITranslationService
{
    private readonly object _activeRequestLock = new();
    private ActiveRequest? _activeRequest;

    public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var settings = await settingsService.LoadAsync(cancellationToken);
        if (!settings.Privacy.AllowTranslationNetworkAccess)
        {
            throw new TranslationException(
                TranslationError.NetworkAccessDisabled,
                "隐私设置已禁止翻译网络访问。");
        }

        var apiKey = await credentialStore.ReadSecretAsync(CredentialKeys.DeepSeekApiKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationException(
                TranslationError.MissingCredential,
                "尚未保存 DeepSeek API 密钥。");
        }

        using var requestCancellation = new CancellationTokenSource();
        using var timeoutCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(settings.Translation.TimeoutSeconds, 1, 300)));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            requestCancellation.Token,
            timeoutCancellation.Token);
        var activeRequest = new ActiveRequest(request.RequestId, requestCancellation);

        lock (_activeRequestLock)
        {
            _activeRequest?.CancellationSource.Cancel();
            _activeRequest = activeRequest;
        }

        try
        {
            var providerRequest = new ProviderTranslationRequest(request, settings.Translation, apiKey);
            await using var enumerator = provider
                .TranslateAsync(providerRequest, linkedCancellation.Token)
                .GetAsyncEnumerator(linkedCancellation.Token);
            while (await MoveNextAsync(
                enumerator,
                timeoutCancellation,
                requestCancellation,
                cancellationToken))
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            lock (_activeRequestLock)
            {
                if (ReferenceEquals(_activeRequest, activeRequest))
                {
                    _activeRequest = null;
                }
            }
        }
    }

    private static async Task<bool> MoveNextAsync(
        IAsyncEnumerator<TranslationChunk> enumerator,
        CancellationTokenSource timeoutCancellation,
        CancellationTokenSource requestCancellation,
        CancellationToken callerCancellationToken)
    {
        try
        {
            return await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException exception)
            when (timeoutCancellation.IsCancellationRequested
                && !callerCancellationToken.IsCancellationRequested
                && !requestCancellation.IsCancellationRequested)
        {
            throw new TranslationException(TranslationError.Timeout, "翻译请求超时。", exception);
        }
    }

    public Task CancelAsync(Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_activeRequestLock)
        {
            if (_activeRequest?.RequestId == requestId)
            {
                _activeRequest.CancellationSource.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    private static void ValidateRequest(TranslationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.SourceText)
            || string.IsNullOrWhiteSpace(request.TargetLanguage)
            || !Enum.IsDefined(request.Preset)
            || !Enum.IsDefined(request.Purpose)
            || (request.Preset == TranslationPreset.Custom
                && request.Purpose == TranslationRequestPurpose.Translate
                && string.IsNullOrWhiteSpace(request.StyleInstruction)))
        {
            throw new TranslationException(TranslationError.InvalidRequest, "翻译请求缺少必要内容。");
        }
    }

    private sealed record ActiveRequest(Guid RequestId, CancellationTokenSource CancellationSource);
}
