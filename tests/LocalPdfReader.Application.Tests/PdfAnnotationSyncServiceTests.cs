using LocalPdfReader.Application.Annotations;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Tests;

public sealed class PdfAnnotationSyncServiceTests
{
    [Fact]
    public void LocalHighlightConvertsToStableStandardPdfOperation()
    {
        var annotationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var annotation = new TextHighlightAnnotation(
            annotationId,
            new DocumentFingerprint("fingerprint", 4096, DateTimeOffset.UtcNow),
            2,
            10,
            5,
            "text",
            AnnotationColor.Blue,
            [
                new PdfRect(10, 20, 30, 40),
                new PdfRect(12, 50, 32, 70)
            ],
            "note",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var operation = PdfAnnotationSyncService.ToCreateOrUpdateOperation(annotation);

        Assert.Equal(PdfAnnotationWriteKind.CreateOrUpdate, operation.Kind);
        Assert.Equal("LocalPdfReader:11111111-2222-3333-4444-555555555555", operation.PdfAnnotationId);
        Assert.Equal(PdfStandardAnnotationType.Highlight, operation.Type);
        Assert.Equal(2, operation.PageIndex);
        Assert.Equal(AnnotationColor.Blue, operation.Color);
        Assert.Equal("note", operation.Contents);
        Assert.Equal(new PdfRect(10, 20, 32, 70), operation.Rect);
        Assert.Equal(2, operation.QuadPoints.Count);
        Assert.Equal(new PdfQuad(10, 40, 30, 40, 10, 20, 30, 20), operation.QuadPoints[0]);
        Assert.Equal(new PdfQuad(12, 70, 32, 70, 12, 50, 32, 50), operation.QuadPoints[1]);
    }

    [Fact]
    public void EmptyAnnotationIdentityIsRejectedBeforePdfWrite()
    {
        Assert.Throws<ArgumentException>(() => PdfAnnotationSyncService.ToPdfAnnotationId(Guid.Empty));
    }

    [Fact]
    public async Task SaveLocalHighlightsDeletesStaleLocalPdfAnnotations()
    {
        var worker = new RecordingWorkerClient
        {
            ExistingAnnotations =
            [
                new PdfStandardAnnotation(
                    "LocalPdfReader:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    PdfStandardAnnotationType.Highlight,
                    0,
                    AnnotationColor.Yellow,
                    [],
                    new PdfRect(1, 2, 3, 4),
                    null,
                    true)
            ]
        };
        var service = new PdfAnnotationSyncService();

        await service.SaveLocalHighlightsAsync(
            worker,
            new DocumentId(Guid.NewGuid()),
            [],
            @"C:\tmp\saved.pdf",
            PdfAnnotationSaveMode.Full,
            CancellationToken.None);

        var delete = Assert.Single(worker.SavedOperations);
        Assert.Equal(PdfAnnotationWriteKind.Delete, delete.Kind);
        Assert.Equal("LocalPdfReader:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", delete.PdfAnnotationId);
    }

    [Fact]
    public async Task AnnotationSavePressureBuildsOneOperationPerLocalAnnotationAndDeletesStaleOnes()
    {
        var now = new DateTimeOffset(2026, 7, 17, 1, 0, 0, TimeSpan.Zero);
        var fingerprint = new DocumentFingerprint("pressure", 1024 * 1024, now);
        var annotations = Enumerable.Range(0, 250)
            .Select(index => new TextHighlightAnnotation(
                Guid.NewGuid(),
                fingerprint,
                index % 25,
                index * 3,
                3,
                $"text {index}",
                (AnnotationColor)(index % 4),
                [new PdfRect(10, 20 + index, 120, 32 + index)],
                index % 5 == 0 ? $"note {index}" : null,
                now.AddSeconds(index),
                now.AddSeconds(index)))
            .ToArray();
        var staleAnnotations = Enumerable.Range(0, 12)
            .Select(index => new PdfStandardAnnotation(
                $"LocalPdfReader:{Guid.NewGuid():D}",
                PdfStandardAnnotationType.Highlight,
                index,
                AnnotationColor.Yellow,
                [],
                new PdfRect(1, 2, 3, 4),
                null,
                true))
            .ToArray();
        var worker = new RecordingWorkerClient { ExistingAnnotations = staleAnnotations };
        var service = new PdfAnnotationSyncService();

        await service.SaveLocalHighlightsAsync(
            worker,
            new DocumentId(Guid.NewGuid()),
            annotations,
            @"C:\tmp\pressure.pdf",
            PdfAnnotationSaveMode.Full,
            CancellationToken.None);

        Assert.Equal(annotations.Length + staleAnnotations.Length, worker.SavedOperations.Count);
        Assert.Equal(staleAnnotations.Length, worker.SavedOperations.Count(operation => operation.Kind == PdfAnnotationWriteKind.Delete));
        Assert.Equal(annotations.Length, worker.SavedOperations.Count(operation => operation.Kind == PdfAnnotationWriteKind.CreateOrUpdate));
        Assert.Equal(
            annotations.Select(annotation => PdfAnnotationSyncService.ToPdfAnnotationId(annotation.AnnotationId)).Distinct().Count(),
            worker.SavedOperations
                .Where(operation => operation.Kind == PdfAnnotationWriteKind.CreateOrUpdate)
                .Select(operation => operation.PdfAnnotationId)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    private sealed class RecordingWorkerClient : IPdfWorkerClient
    {
        public event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public IReadOnlyList<PdfStandardAnnotation> ExistingAnnotations { get; init; } = [];

        public IReadOnlyList<PdfAnnotationWriteOperation> SavedOperations { get; private set; } = [];

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

        public Task<IReadOnlyList<PdfStandardAnnotation>> GetPdfAnnotationsAsync(
            DocumentId documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ExistingAnnotations);

        public Task<PdfAnnotationSaveResult> SavePdfAnnotationsAsync(
            DocumentId documentId,
            IReadOnlyList<PdfAnnotationWriteOperation> operations,
            string destinationFilePath,
            PdfAnnotationSaveMode saveMode,
            CancellationToken cancellationToken)
        {
            SavedOperations = operations.ToArray();
            return Task.FromResult(new PdfAnnotationSaveResult(destinationFilePath, []));
        }

        public Task ReleaseSharedMemoryAsync(string memoryMapName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
