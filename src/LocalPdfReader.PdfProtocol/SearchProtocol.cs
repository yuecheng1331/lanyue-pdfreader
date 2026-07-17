namespace LocalPdfReader.PdfProtocol;

public static class SearchProtocolLimits
{
    public const int DefaultBatchSize = 20;
    public const int MaximumBatchSize = 200;

    public static void Validate(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SearchSessionId == Guid.Empty)
        {
            throw new ArgumentException("A search session identifier is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("A search query is required.", nameof(request));
        }

        if (request.BatchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.BatchSize,
                $"Search batch size must be between 1 and {MaximumBatchSize}.");
        }
    }
}

public sealed record SearchRequest(
    Guid SearchSessionId,
    string Query,
    bool MatchCase,
    bool WholeWord,
    int BatchSize = SearchProtocolLimits.DefaultBatchSize);

public sealed record SearchStartedResponse(
    Guid SearchSessionId,
    int TotalPages);

public sealed record SearchProgressResponse(
    Guid SearchSessionId,
    int PagesSearched,
    int TotalPages,
    int ResultsFound);

public sealed record SearchResultBatchResponse(
    Guid SearchSessionId,
    IReadOnlyList<Domain.SearchResult> Results);

public sealed record SearchCompletedResponse(
    Guid SearchSessionId,
    int TotalResults);

public sealed record SearchCancelledResponse(Guid SearchSessionId);

public sealed record SearchFailedResponse(
    Guid SearchSessionId,
    string ErrorCode,
    string UserMessage);
