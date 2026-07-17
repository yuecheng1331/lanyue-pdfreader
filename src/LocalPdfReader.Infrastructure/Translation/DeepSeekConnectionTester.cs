using System.Net;
using System.Net.Http.Headers;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Translation;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.Infrastructure.Translation;

public sealed class DeepSeekConnectionTester(HttpClient httpClient, ILogger<DeepSeekConnectionTester> logger)
    : ITranslationConnectionTester
{
    public async Task<ProviderConnectionResult> TestAsync(
        TranslationSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (!DeepSeekApiAddress.TryNormalizeOpenAiBaseUri(settings.BaseUrl, out var baseUri))
        {
            return new ProviderConnectionResult(false, ProviderConnectionError.InvalidSettings, "API 地址必须是有效的 HTTPS 地址。");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 300)));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            DeepSeekApiAddress.CreateOpenAiEndpoint(baseUri.AbsoluteUri, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(new EventId(4000, "DeepSeekConnectionSucceeded"), "DeepSeek connection test succeeded.");
                return new ProviderConnectionResult(true, ProviderConnectionError.None, "连接成功，API 密钥和服务地址可用。");
            }

            var result = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new ProviderConnectionResult(false, ProviderConnectionError.AuthenticationFailed, "身份验证失败，请检查 API 密钥。"),
                HttpStatusCode.TooManyRequests =>
                    new ProviderConnectionResult(false, ProviderConnectionError.RateLimited, "请求受到限流，请稍后重试。"),
                _ => new ProviderConnectionResult(false, ProviderConnectionError.ProviderError,
                    $"服务返回错误状态码 {(int)response.StatusCode}。")
            };
            logger.LogWarning(new EventId(4001, "DeepSeekConnectionRejected"),
                "DeepSeek connection test failed with status code {StatusCode} and category {Category}.",
                (int)response.StatusCode,
                result.Error);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderConnectionResult(false, ProviderConnectionError.Timeout, "连接测试超时。");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(new EventId(4002, "DeepSeekConnectionNetworkFailed"), exception,
                "DeepSeek connection test failed because the network was unavailable.");
            return new ProviderConnectionResult(false, ProviderConnectionError.NetworkUnavailable, "无法连接服务，请检查网络和 API 地址。");
        }
    }
}
