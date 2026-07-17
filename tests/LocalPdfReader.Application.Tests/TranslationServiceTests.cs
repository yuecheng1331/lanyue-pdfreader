using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Tests;

public class TranslationServiceTests
{
    [Fact]
    public async Task DisabledNetworkAccessStopsBeforeReadingCredential()
    {
        var credentialStore = new StubCredentialStore(Guid.NewGuid().ToString("N"));
        var service = new TranslationService(
            new StubSettingsService(CreateSettings(allowNetwork: false)),
            credentialStore,
            new ControlledProvider());

        var exception = await Assert.ThrowsAsync<TranslationException>(() =>
            CollectAsync(service.TranslateAsync(CreateRequest(), CancellationToken.None)));

        Assert.Equal(TranslationError.NetworkAccessDisabled, exception.Error);
        Assert.Equal(0, credentialStore.ReadCount);
    }

    [Fact]
    public async Task CancelAsyncCancelsTheMatchingRequest()
    {
        var provider = new ControlledProvider(blockRequests: true);
        var service = CreateService(provider, timeoutSeconds: 30);
        var request = CreateRequest();
        var operation = CollectAsync(service.TranslateAsync(request, CancellationToken.None));
        await provider.WaitUntilStartedAsync(request.RequestId);

        await service.CancelAsync(request.RequestId, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await provider.WaitUntilCancelledAsync(request.RequestId);
    }

    [Fact]
    public async Task StartingANewRequestCancelsThePreviousRequest()
    {
        var provider = new ControlledProvider(blockRequests: true);
        var service = CreateService(provider, timeoutSeconds: 30);
        var first = CreateRequest();
        var second = CreateRequest();
        var firstOperation = CollectAsync(service.TranslateAsync(first, CancellationToken.None));
        await provider.WaitUntilStartedAsync(first.RequestId);

        var secondOperation = CollectAsync(service.TranslateAsync(second, CancellationToken.None));
        await provider.WaitUntilStartedAsync(second.RequestId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstOperation);
        await provider.WaitUntilCancelledAsync(first.RequestId);
        await service.CancelAsync(second.RequestId, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondOperation);
    }

    [Fact]
    public async Task TimeoutHasAStableErrorCategory()
    {
        var provider = new ControlledProvider(blockRequests: true);
        var service = CreateService(provider, timeoutSeconds: 1);
        var request = CreateRequest();

        var exception = await Assert.ThrowsAsync<TranslationException>(() =>
            CollectAsync(service.TranslateAsync(request, CancellationToken.None)));

        Assert.Equal(TranslationError.Timeout, exception.Error);
        await provider.WaitUntilCancelledAsync(request.RequestId);
    }

    private static TranslationService CreateService(ControlledProvider provider, int timeoutSeconds) => new(
        new StubSettingsService(CreateSettings(allowNetwork: true, timeoutSeconds)),
        new StubCredentialStore(Guid.NewGuid().ToString("N")),
        provider);

    private static AppSettings CreateSettings(bool allowNetwork, int timeoutSeconds = 30) => new()
    {
        Translation = new TranslationSettings { TimeoutSeconds = timeoutSeconds },
        Privacy = new PrivacySettings { AllowTranslationNetworkAccess = allowNetwork }
    };

    private static TranslationRequest CreateRequest() => new(
        Guid.NewGuid(),
        "Source text",
        "zh-CN",
        TranslationPreset.Fluent);

    private static async Task<List<TranslationChunk>> CollectAsync(
        IAsyncEnumerable<TranslationChunk> chunks)
    {
        var result = new List<TranslationChunk>();
        await foreach (var chunk in chunks)
        {
            result.Add(chunk);
        }

        return result;
    }

    private sealed class StubSettingsService(AppSettings settings) : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);

        public Task SaveAsync(AppSettings settingsToSave, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubCredentialStore(string secret) : ICredentialStore
    {
        public int ReadCount { get; private set; }

        public Task SaveSecretAsync(string key, string secretToSave, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> ReadSecretAsync(string key, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<string?>(secret);
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ControlledProvider(bool blockRequests = false) : ITranslationProvider
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _cancelled = new();

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            ProviderTranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var requestId = request.Translation.RequestId;
            GetSignal(_started, requestId).TrySetResult();
            if (blockRequests)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    GetSignal(_cancelled, requestId).TrySetResult();
                    throw;
                }
            }

            yield return new TranslationChunk(requestId, string.Empty, IsCompleted: true);
        }

        public Task WaitUntilStartedAsync(Guid requestId) =>
            GetSignal(_started, requestId).Task.WaitAsync(TimeSpan.FromSeconds(2));

        public Task WaitUntilCancelledAsync(Guid requestId) =>
            GetSignal(_cancelled, requestId).Task.WaitAsync(TimeSpan.FromSeconds(2));

        private static TaskCompletionSource GetSignal(
            ConcurrentDictionary<Guid, TaskCompletionSource> signals,
            Guid requestId) =>
            signals.GetOrAdd(
                requestId,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
