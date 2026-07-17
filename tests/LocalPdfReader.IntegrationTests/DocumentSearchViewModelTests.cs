using System.Runtime.CompilerServices;
using LocalPdfReader.App;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Domain;

namespace LocalPdfReader.IntegrationTests;

public sealed class DocumentSearchViewModelTests
{
    [Fact]
    public async Task SearchViewModelCollectsProgressResultsAndCompletion()
    {
        var documentId = new DocumentId(Guid.NewGuid());
        var worker = new SearchStubPdfWorkerClient((_, sessionId, query, _, _, _, _) =>
            CompletedSearch(sessionId, query));
        var viewModel = new DocumentSearchViewModel(worker, () => documentId)
        {
            Query = "pdf"
        };

        await viewModel.StartSearchAsync();

        Assert.False(viewModel.IsSearching);
        Assert.Equal(3, viewModel.TotalPages);
        Assert.Equal(3, viewModel.PagesSearched);
        Assert.Equal(1, viewModel.ResultCount);
        var item = Assert.Single(viewModel.Results);
        Assert.Equal(2, item.PageNumber);
        Assert.Equal("contains pdf text", item.ContextText);
        Assert.Equal("搜索完成，共找到 1 处。", viewModel.StatusText);
    }

    [Fact]
    public async Task NewSearchIgnoresResultsThatArriveLateFromThePreviousSession()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var documentId = new DocumentId(Guid.NewGuid());
        var worker = new SearchStubPdfWorkerClient(
            (id, sessionId, query, matchCase, wholeWord, batchSize, token) => query == "first"
                ? DelayedSearch(id, sessionId, "old result", firstStarted, releaseFirst)
                : CompletedSearch(sessionId, query));
        var viewModel = new DocumentSearchViewModel(worker, () => documentId)
        {
            Query = "first"
        };

        var firstTask = viewModel.StartSearchAsync();
        await firstStarted.Task;
        viewModel.Query = "second";
        await viewModel.StartSearchAsync();
        releaseFirst.SetResult();
        await firstTask;

        var item = Assert.Single(viewModel.Results);
        Assert.Equal("contains second text", item.ContextText);
        Assert.Equal(1, viewModel.ResultCount);
    }

    private static async IAsyncEnumerable<SearchUpdate> CompletedSearch(
        Guid sessionId,
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new SearchStartedUpdate(sessionId, 3);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SearchResultsUpdate(sessionId,
        [
            new SearchResult(
                sessionId,
                0,
                1,
                9,
                query.Length,
                query,
                $"contains {query} text",
                [new PdfRect(1, 2, 3, 4)])
        ]);
        yield return new SearchProgressUpdate(sessionId, 3, 3, 1);
        yield return new SearchCompletedUpdate(sessionId, 1);
    }

    private static async IAsyncEnumerable<SearchUpdate> DelayedSearch(
        DocumentId documentId,
        Guid sessionId,
        string context,
        TaskCompletionSource started,
        TaskCompletionSource release)
    {
        yield return new SearchStartedUpdate(sessionId, 1);
        started.TrySetResult();
        await release.Task;
        yield return new SearchResultsUpdate(sessionId,
        [
            new SearchResult(
                sessionId,
                0,
                0,
                0,
                3,
                "old",
                context,
                [new PdfRect(1, 2, 3, 4)])
        ]);
        yield return new SearchCompletedUpdate(sessionId, 1);
    }

    private sealed class SearchStubPdfWorkerClient(
        Func<DocumentId, Guid, string, bool, bool, int, CancellationToken, IAsyncEnumerable<SearchUpdate>> search)
        : IPdfWorkerClient
    {
        public event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public IAsyncEnumerable<SearchUpdate> SearchDocumentAsync(
            DocumentId documentId,
            Guid searchSessionId,
            string query,
            bool matchCase,
            bool wholeWord,
            int batchSize,
            CancellationToken cancellationToken) =>
            search(documentId, searchSessionId, query, matchCase, wholeWord, batchSize, cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CloseDocumentAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RenderedPageDescriptor> RenderPageAsync(PageRenderRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PageTextData> GetPageTextAsync(DocumentId documentId, int pageIndex, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TextHitTestResult?> HitTestTextAsync(
            DocumentId documentId,
            int pageIndex,
            PdfPoint point,
            double tolerance,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseSharedMemoryAsync(string memoryMapName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
