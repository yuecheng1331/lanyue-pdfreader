using System.Diagnostics;
using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;

namespace LocalPdfReader.PdfWorker;

internal sealed class WorkerSearchManager(
    WorkerDocumentService documents,
    PipeResponseWriter responseWriter) : IAsyncDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, ActiveSearch> _searchesByRequest = [];
    private readonly Dictionary<DocumentId, ActiveSearch> _searchesByDocument = [];
    private bool _disposed;

    public void Start(
        PipeMessageEnvelope requestEnvelope,
        DocumentId documentId,
        SearchRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SearchProtocolLimits.Validate(request);
        var totalPages = documents.GetPageCount(documentId);
        var activeSearch = new ActiveSearch(requestEnvelope, documentId, request);

        lock (_syncRoot)
        {
            if (_searchesByDocument.TryGetValue(documentId, out var previousSearch))
            {
                previousSearch.CancellationSource.Cancel();
            }

            _searchesByRequest[requestEnvelope.RequestId] = activeSearch;
            _searchesByDocument[documentId] = activeSearch;
            activeSearch.Task = Task.Run(() => RunAsync(activeSearch, totalPages));
        }
    }

    public bool Cancel(Guid requestId)
    {
        lock (_syncRoot)
        {
            if (!_searchesByRequest.TryGetValue(requestId, out var activeSearch))
            {
                return false;
            }

            activeSearch.CancellationSource.Cancel();
            return true;
        }
    }

    public async Task CancelDocumentAsync(DocumentId documentId)
    {
        ActiveSearch? activeSearch;
        lock (_syncRoot)
        {
            _searchesByDocument.TryGetValue(documentId, out activeSearch);
            activeSearch?.CancellationSource.Cancel();
        }

        if (activeSearch?.Task is not null)
        {
            await activeSearch.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ActiveSearch[] activeSearches;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            activeSearches = _searchesByRequest.Values.Distinct().ToArray();
            foreach (var activeSearch in activeSearches)
            {
                activeSearch.CancellationSource.Cancel();
            }
        }

        await Task.WhenAll(activeSearches
            .Select(activeSearch => activeSearch.Task)
            .Where(task => task is not null)
            .Cast<Task>());
    }

    private async Task RunAsync(ActiveSearch activeSearch, int totalPages)
    {
        var request = activeSearch.Request;
        var cancellationToken = activeSearch.CancellationSource.Token;
        var pendingResults = new List<SearchResult>(request.BatchSize);
        var totalResults = 0;
        var updateStopwatch = Stopwatch.StartNew();

        try
        {
            await responseWriter.WriteAsync(
                activeSearch.RequestEnvelope,
                PipeMessageTypes.SearchStartedResponse,
                activeSearch.DocumentId,
                new SearchStartedResponse(request.SearchSessionId, totalPages));

            for (var pageIndex = 0; pageIndex < totalPages; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageText = documents.GetPageText(activeSearch.DocumentId, pageIndex);
                var pageResults = PageSearchResultBuilder.Build(
                    pageText,
                    request.SearchSessionId,
                    request.Query,
                    request.MatchCase,
                    request.WholeWord,
                    totalResults);
                totalResults += pageResults.Count;
                pendingResults.AddRange(pageResults);

                while (pendingResults.Count >= request.BatchSize)
                {
                    await SendBatchAsync(activeSearch, pendingResults.Take(request.BatchSize).ToArray());
                    pendingResults.RemoveRange(0, request.BatchSize);
                }

                var isLastPage = pageIndex == totalPages - 1;
                if (pendingResults.Count > 0 && (isLastPage || updateStopwatch.Elapsed >= UpdateInterval))
                {
                    await SendBatchAsync(activeSearch, pendingResults.ToArray());
                    pendingResults.Clear();
                }

                if (isLastPage || updateStopwatch.Elapsed >= UpdateInterval)
                {
                    await responseWriter.WriteAsync(
                        activeSearch.RequestEnvelope,
                        PipeMessageTypes.SearchProgressResponse,
                        activeSearch.DocumentId,
                        new SearchProgressResponse(
                            request.SearchSessionId,
                            pageIndex + 1,
                            totalPages,
                            totalResults));
                    updateStopwatch.Restart();
                }
            }

            await responseWriter.WriteAsync(
                activeSearch.RequestEnvelope,
                PipeMessageTypes.SearchCompletedResponse,
                activeSearch.DocumentId,
                new SearchCompletedResponse(request.SearchSessionId, totalResults));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryWriteTerminalResponseAsync(
                activeSearch,
                PipeMessageTypes.SearchCancelledResponse,
                new SearchCancelledResponse(request.SearchSessionId));
        }
        catch (Exception)
        {
            await TryWriteTerminalResponseAsync(
                activeSearch,
                PipeMessageTypes.SearchFailedResponse,
                new SearchFailedResponse(
                    request.SearchSessionId,
                    "PdfSearchFailed",
                    "The PDF document could not be searched."));
        }
        finally
        {
            lock (_syncRoot)
            {
                _searchesByRequest.Remove(activeSearch.RequestEnvelope.RequestId);
                if (_searchesByDocument.TryGetValue(activeSearch.DocumentId, out var currentSearch) &&
                    ReferenceEquals(currentSearch, activeSearch))
                {
                    _searchesByDocument.Remove(activeSearch.DocumentId);
                }
            }

            activeSearch.CancellationSource.Dispose();
        }
    }

    private Task SendBatchAsync(ActiveSearch activeSearch, IReadOnlyList<SearchResult> results) =>
        responseWriter.WriteAsync(
            activeSearch.RequestEnvelope,
            PipeMessageTypes.SearchResultBatchResponse,
            activeSearch.DocumentId,
            new SearchResultBatchResponse(activeSearch.Request.SearchSessionId, results));

    private async Task TryWriteTerminalResponseAsync<TPayload>(
        ActiveSearch activeSearch,
        string messageType,
        TPayload payload)
    {
        try
        {
            await responseWriter.WriteAsync(
                activeSearch.RequestEnvelope,
                messageType,
                activeSearch.DocumentId,
                payload);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The main process may already have disconnected while the search was being cancelled.
        }
    }

    private sealed class ActiveSearch(
        PipeMessageEnvelope requestEnvelope,
        DocumentId documentId,
        SearchRequest request)
    {
        public PipeMessageEnvelope RequestEnvelope { get; } = requestEnvelope;

        public DocumentId DocumentId { get; } = documentId;

        public SearchRequest Request { get; } = request;

        public CancellationTokenSource CancellationSource { get; } = new();

        public Task? Task { get; set; }
    }
}
