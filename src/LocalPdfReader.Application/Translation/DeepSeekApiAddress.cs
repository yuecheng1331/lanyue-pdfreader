namespace LocalPdfReader.Application.Translation;

public static class DeepSeekApiAddress
{
    private const string OfficialHost = "api.deepseek.com";

    public static bool TryNormalizeOpenAiBaseUri(string? baseUrl, out Uri normalizedBaseUri)
    {
        normalizedBaseUri = null!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (baseUri.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase)
            && (path.Equals("/anthropic", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/anthropic/", StringComparison.OrdinalIgnoreCase)))
        {
            var builder = new UriBuilder(baseUri)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty
            };
            normalizedBaseUri = builder.Uri;
            return true;
        }

        normalizedBaseUri = baseUri;
        return true;
    }

    public static Uri CreateOpenAiEndpoint(string baseUrl, string relativePath)
    {
        if (!TryNormalizeOpenAiBaseUri(baseUrl, out var baseUri))
        {
            throw new TranslationException(
                TranslationError.InvalidRequest,
                "API 地址必须是有效的 HTTPS 地址。");
        }

        var normalized = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");
        return new Uri(normalized, relativePath.TrimStart('/'));
    }
}
