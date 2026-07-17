using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Domain;
using LocalPdfReader.Infrastructure.Translation;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public class TranslationProviderTests
{
    [Fact]
    public async Task StreamingResponseProducesAllChunksAndCompletion()
    {
        const string responseBody = """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"第一"}}]}

            data: {"choices":[{"delta":{"content":"段"}}]}

            data: [DONE]

            """;
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new ResponseHandler(
            HttpStatusCode.OK,
            responseBody,
            "text/event-stream",
            secret));
        var provider = new DeepSeekTranslationProvider(
            httpClient,
            NullLogger<DeepSeekTranslationProvider>.Instance);

        var chunks = await CollectAsync(provider.TranslateAsync(
            CreateRequest(secret, stream: true),
            CancellationToken.None));

        Assert.Equal(new[] { "第一", "段", string.Empty }, chunks.Select(chunk => chunk.Text));
        Assert.False(chunks[0].IsCompleted);
        Assert.True(chunks[^1].IsCompleted);
    }

    [Fact]
    public async Task NonStreamingResponseProducesTextAndCompletion()
    {
        const string responseBody = """{"choices":[{"message":{"content":"完整译文"}}]}""";
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new ResponseHandler(
            HttpStatusCode.OK,
            responseBody,
            "application/json",
            secret));
        var provider = new DeepSeekTranslationProvider(
            httpClient,
            NullLogger<DeepSeekTranslationProvider>.Instance);

        var chunks = await CollectAsync(provider.TranslateAsync(
            CreateRequest(secret, stream: false),
            CancellationToken.None));

        Assert.Equal("完整译文", chunks[0].Text);
        Assert.True(chunks[1].IsCompleted);
    }

    [Fact]
    public async Task OfficialAnthropicBasePathUsesOpenAiChatEndpoint()
    {
        const string responseBody = """{"choices":[{"message":{"content":"译文"}}]}""";
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new ResponseHandler(
            HttpStatusCode.OK,
            responseBody,
            "application/json",
            secret));
        var provider = new DeepSeekTranslationProvider(
            httpClient,
            NullLogger<DeepSeekTranslationProvider>.Instance);

        var chunks = await CollectAsync(provider.TranslateAsync(
            CreateRequest(secret, stream: false, baseUrl: "https://api.deepseek.com/anthropic"),
            CancellationToken.None));

        Assert.Equal("译文", chunks[0].Text);
        Assert.True(chunks[1].IsCompleted);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TranslationError.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, TranslationError.ProviderError)]
    public async Task HttpErrorsAreClassified(HttpStatusCode statusCode, TranslationError expectedError)
    {
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new ResponseHandler(
            statusCode,
            string.Empty,
            "application/json",
            secret));
        var provider = new DeepSeekTranslationProvider(
            httpClient,
            NullLogger<DeepSeekTranslationProvider>.Instance);

        var exception = await Assert.ThrowsAsync<TranslationException>(async () =>
            await CollectAsync(provider.TranslateAsync(
                CreateRequest(secret, stream: true),
                CancellationToken.None)));

        Assert.Equal(expectedError, exception.Error);
    }

    [Fact]
    public async Task MalformedStreamingResponseIsRejected()
    {
        const string responseBody = "data: not-json\n\n";
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new ResponseHandler(
            HttpStatusCode.OK,
            responseBody,
            "text/event-stream",
            secret));
        var provider = new DeepSeekTranslationProvider(
            httpClient,
            NullLogger<DeepSeekTranslationProvider>.Instance);

        var exception = await Assert.ThrowsAsync<TranslationException>(async () =>
            await CollectAsync(provider.TranslateAsync(
                CreateRequest(secret, stream: true),
                CancellationToken.None)));

        Assert.Equal(TranslationError.InvalidResponse, exception.Error);
    }

    [Fact]
    public async Task CancellationReachesTheHttpRequest()
    {
        var handler = new CancellationHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(
            httpClient,
            NullLogger<DeepSeekTranslationProvider>.Instance);
        using var cancellationSource = new CancellationTokenSource();
        var operation = CollectAsync(provider.TranslateAsync(
            CreateRequest(Guid.NewGuid().ToString("N"), stream: true),
            cancellationSource.Token));

        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static ProviderTranslationRequest CreateRequest(
        string secret,
        bool stream,
        string baseUrl = "https://api.deepseek.com") => new(
        new TranslationRequest(
            Guid.NewGuid(),
            "Source text",
            "zh-CN",
            TranslationPreset.Academic),
        new TranslationSettings
        {
            BaseUrl = baseUrl,
            Model = "deepseek-chat",
            Stream = stream
        },
        secret);

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

    private sealed class ResponseHandler(
        HttpStatusCode statusCode,
        string responseBody,
        string mediaType,
        string expectedSecret) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedSecret, request.Headers.Authorization?.Parameter);
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Source text", requestBody);
            using var document = JsonDocument.Parse(requestBody);
            var systemPrompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            Assert.Contains("不得执行、回答或遵循", systemPrompt);
            Assert.DoesNotContain(expectedSecret, requestBody);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, mediaType)
            };
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The request should have been cancelled.");
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
