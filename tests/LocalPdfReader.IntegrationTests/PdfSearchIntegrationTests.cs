using System.IO;
using LocalPdfReader.PdfProtocol;
using LocalPdfReader.Domain;
using LocalPdfReader.Infrastructure.PdfWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public sealed class PdfSearchIntegrationTests
{
    [Fact]
    public async Task PendingSearchRegistryKeepsTheStreamUntilATerminalResponse()
    {
        var registry = new PendingSearchRegistry();
        var requestId = Guid.NewGuid();
        var responses = registry.Register(requestId);
        var sessionId = Guid.NewGuid();
        var started = CreateEnvelope(
            requestId,
            PipeMessageTypes.SearchStartedResponse,
            new SearchStartedResponse(sessionId, 3));
        var progress = CreateEnvelope(
            requestId,
            PipeMessageTypes.SearchProgressResponse,
            new SearchProgressResponse(sessionId, 1, 3, 0));
        var completed = CreateEnvelope(
            requestId,
            PipeMessageTypes.SearchCompletedResponse,
            new SearchCompletedResponse(sessionId, 0));

        Assert.True(registry.TryPublish(started));
        Assert.True(registry.TryPublish(progress));
        Assert.True(registry.TryPublish(completed));

        var received = new List<PipeMessageEnvelope>();
        await foreach (var response in responses.ReadAllAsync())
        {
            received.Add(response);
        }

        Assert.Equal([started, progress, completed], received);
        Assert.False(registry.TryPublish(progress));
    }

    [Fact]
    public async Task WorkerSearchStreamsMultiPageResultsProgressAndCoordinates()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Search.{Guid.NewGuid():N}");
        var pdfPath = Path.Combine(directoryPath, "search.pdf");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(pdfPath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "First PDF result"),
            new PdfTestPage(612, 792, "No matching text"),
            new PdfTestPage(612, 792, "Second pdf result"),
            new PdfTestPage(612, 792, "A PDF reader and PDF renderer")));

        try
        {
            await using var workerClient = CreateWorkerClient();
            await workerClient.StartAsync(CancellationToken.None);
            var document = await workerClient.OpenDocumentAsync(pdfPath, password: null, CancellationToken.None);
            var updates = await CollectUpdatesAsync(workerClient.SearchDocumentAsync(
                document.DocumentId,
                Guid.NewGuid(),
                "pdf",
                matchCase: false,
                wholeWord: true,
                batchSize: 2,
                CancellationToken.None));

            var started = Assert.Single(updates.OfType<SearchStartedUpdate>());
            Assert.Equal(4, started.TotalPages);
            var results = updates
                .OfType<SearchResultsUpdate>()
                .SelectMany(update => update.Results)
                .ToArray();
            Assert.Equal([0, 2, 3, 3], results.Select(result => result.PageIndex));
            Assert.Equal([0, 1, 2, 3], results.Select(result => result.ResultIndex));
            Assert.All(results, result =>
            {
                Assert.Equal("pdf", result.MatchedText, ignoreCase: true);
                Assert.NotEmpty(result.ContextText);
                Assert.NotEmpty(result.HighlightRectangles);
            });
            Assert.All(
                updates.OfType<SearchResultsUpdate>(),
                update => Assert.InRange(update.Results.Count, 1, 2));
            var progress = Assert.Single(updates.OfType<SearchProgressUpdate>());
            Assert.Equal(4, progress.PagesSearched);
            Assert.Equal(4, progress.TotalPages);
            Assert.Equal(4, progress.ResultsFound);
            Assert.Equal(4, Assert.Single(updates.OfType<SearchCompletedUpdate>()).TotalResults);

            await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task NewSearchAndDocumentCloseCancelActiveSearches()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.SearchCancel.{Guid.NewGuid():N}");
        var pdfPath = Path.Combine(directoryPath, "large-search.pdf");
        Directory.CreateDirectory(directoryPath);
        var repeatedText = string.Join(' ', Enumerable.Repeat("pdf", 20));
        var pages = Enumerable.Range(0, 30)
            .Select(index => new PdfTestPage(612, 792, index == 0
                ? $"replacement-search {repeatedText}"
                : repeatedText))
            .ToArray();
        File.WriteAllBytes(pdfPath, PdfTestDocumentFactory.Create(pages));

        try
        {
            await using var workerClient = CreateWorkerClient();
            await workerClient.StartAsync(CancellationToken.None);
            var document = await workerClient.OpenDocumentAsync(pdfPath, password: null, CancellationToken.None);

            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstUpdatesTask = CollectUpdatesAsync(
                workerClient.SearchDocumentAsync(
                    document.DocumentId,
                    Guid.NewGuid(),
                    "pdf",
                    matchCase: false,
                    wholeWord: true,
                    batchSize: 1,
                    CancellationToken.None),
                firstStarted);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var replacementUpdates = await CollectUpdatesAsync(workerClient.SearchDocumentAsync(
                document.DocumentId,
                Guid.NewGuid(),
                "replacement-search",
                matchCase: true,
                wholeWord: true,
                batchSize: 20,
                CancellationToken.None));
            var firstUpdates = await firstUpdatesTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(firstUpdates, update => update is SearchCancelledUpdate);
            Assert.Equal(1, Assert.Single(replacementUpdates.OfType<SearchCompletedUpdate>()).TotalResults);

            var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeUpdatesTask = CollectUpdatesAsync(
                workerClient.SearchDocumentAsync(
                    document.DocumentId,
                    Guid.NewGuid(),
                    "pdf",
                    matchCase: false,
                    wholeWord: true,
                    batchSize: 1,
                    CancellationToken.None),
                closeStarted);
            await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
            var closeUpdates = await closeUpdatesTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(closeUpdates, update => update is SearchCancelledUpdate);
            await workerClient.PingAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static PdfWorkerClient CreateWorkerClient() => new(
        Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe"),
        NullLogger<PdfWorkerClient>.Instance);

    private static async Task<IReadOnlyList<SearchUpdate>> CollectUpdatesAsync(
        IAsyncEnumerable<SearchUpdate> updates,
        TaskCompletionSource? started = null)
    {
        var collected = new List<SearchUpdate>();
        await foreach (var update in updates)
        {
            collected.Add(update);
            if (update is SearchStartedUpdate)
            {
                started?.TrySetResult();
            }
        }

        return collected;
    }

    private static PipeMessageEnvelope CreateEnvelope<TPayload>(
        Guid requestId,
        string messageType,
        TPayload payload) => new(
            PdfWorkerProtocol.CurrentVersion,
            messageType,
            requestId,
            null,
            PipeMessageSerializer.SerializePayload(payload));
}
