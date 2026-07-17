using System.IO;
using LocalPdfReader.Domain;
using LocalPdfReader.Infrastructure.PdfWorker;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public sealed class PdfAnnotationWorkerIntegrationTests
{
    [Fact]
    public async Task WorkerReadsWritesVerifiesAndDeletesStandardPdfAnnotations()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.PdfAnnotationWorker.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var sourcePath = Path.Combine(directoryPath, "source.pdf");
        var annotatedPath = Path.Combine(directoryPath, "annotated.pdf");
        var deletedPath = Path.Combine(directoryPath, "deleted.pdf");
        File.WriteAllBytes(sourcePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Worker annotation integration")));

        try
        {
            await using var workerClient = CreateWorkerClient();
            await workerClient.StartAsync(CancellationToken.None);
            var document = await workerClient.OpenDocumentAsync(sourcePath, password: null, CancellationToken.None);

            Assert.Empty(await workerClient.GetPdfAnnotationsAsync(document.DocumentId, CancellationToken.None));

            var saveResult = await workerClient.SavePdfAnnotationsAsync(
                document.DocumentId,
                CreateStandardAnnotationOperations(),
                annotatedPath,
                PdfAnnotationSaveMode.Full,
                CancellationToken.None);
            Assert.Equal(Path.GetFullPath(annotatedPath), saveResult.SavedFilePath);
            AssertRoundTrip(saveResult.VerifiedAnnotations);

            var savedDocument = await workerClient.OpenDocumentAsync(annotatedPath, password: null, CancellationToken.None);
            AssertRoundTrip(await workerClient.GetPdfAnnotationsAsync(savedDocument.DocumentId, CancellationToken.None));

            var deleteResult = await workerClient.SavePdfAnnotationsAsync(
                savedDocument.DocumentId,
                [
                    new PdfAnnotationWriteOperation(
                        PdfAnnotationWriteKind.Delete,
                        "lpr-worker-highlight",
                        PdfStandardAnnotationType.Highlight,
                        0,
                        AnnotationColor.Yellow,
                        [],
                        new PdfRect(0, 0, 0, 0),
                        null)
                ],
                deletedPath,
                PdfAnnotationSaveMode.Incremental,
                CancellationToken.None);
            Assert.DoesNotContain(deleteResult.VerifiedAnnotations, annotation => annotation.PdfAnnotationId == "lpr-worker-highlight");
            Assert.Contains(deleteResult.VerifiedAnnotations, annotation => annotation.PdfAnnotationId == "lpr-worker-underline");

            await workerClient.CloseDocumentAsync(savedDocument.DocumentId, CancellationToken.None);
            await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerSavesAnnotationsBackToTheCurrentPdfThroughTemporaryReplacement()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.PdfAnnotationReplace.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var sourcePath = Path.Combine(directoryPath, "replace.pdf");
        File.WriteAllBytes(sourcePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Worker annotation replacement")));

        try
        {
            await using var workerClient = CreateWorkerClient();
            await workerClient.StartAsync(CancellationToken.None);
            var document = await workerClient.OpenDocumentAsync(sourcePath, password: null, CancellationToken.None);
            var result = await workerClient.SavePdfAnnotationsAsync(
                document.DocumentId,
                CreateStandardAnnotationOperations(),
                sourcePath,
                PdfAnnotationSaveMode.Full,
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(sourcePath), result.SavedFilePath);
            AssertRoundTrip(result.VerifiedAnnotations);

            await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
            var reopened = await workerClient.OpenDocumentAsync(sourcePath, password: null, CancellationToken.None);
            AssertRoundTrip(await workerClient.GetPdfAnnotationsAsync(reopened.DocumentId, CancellationToken.None));
            await workerClient.CloseDocumentAsync(reopened.DocumentId, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static IReadOnlyList<PdfAnnotationWriteOperation> CreateStandardAnnotationOperations() =>
    [
        CreateMarkup("lpr-worker-highlight", PdfStandardAnnotationType.Highlight, AnnotationColor.Yellow, CreateQuad(72, 720, 252, 696), "Worker highlight note"),
        CreateMarkup("lpr-worker-underline", PdfStandardAnnotationType.Underline, AnnotationColor.Blue, CreateQuad(72, 684, 252, 660), "Worker underline note"),
        CreateMarkup("lpr-worker-strikeout", PdfStandardAnnotationType.StrikeOut, AnnotationColor.Pink, CreateQuad(72, 648, 252, 624), "Worker strikeout note"),
        new(
            PdfAnnotationWriteKind.CreateOrUpdate,
            "lpr-worker-text",
            PdfStandardAnnotationType.Text,
            0,
            AnnotationColor.Green,
            [],
            new PdfRect(288, 690, 318, 720),
            "Worker text note")
    ];

    private static PdfAnnotationWriteOperation CreateMarkup(
        string id,
        PdfStandardAnnotationType type,
        AnnotationColor color,
        PdfQuad quad,
        string contents) => new(
            PdfAnnotationWriteKind.CreateOrUpdate,
            id,
            type,
            0,
            color,
            [quad],
            RectFromQuad(quad),
            contents);

    private static void AssertRoundTrip(IReadOnlyList<PdfStandardAnnotation> annotations)
    {
        Assert.Contains(annotations, annotation => annotation is
        {
            PdfAnnotationId: "lpr-worker-highlight",
            Type: PdfStandardAnnotationType.Highlight,
            Color: AnnotationColor.Yellow,
            Contents: "Worker highlight note"
        } && annotation.QuadPoints.Count == 1);
        Assert.Contains(annotations, annotation => annotation is
        {
            PdfAnnotationId: "lpr-worker-underline",
            Type: PdfStandardAnnotationType.Underline,
            Color: AnnotationColor.Blue,
            Contents: "Worker underline note"
        } && annotation.QuadPoints.Count == 1);
        Assert.Contains(annotations, annotation => annotation is
        {
            PdfAnnotationId: "lpr-worker-strikeout",
            Type: PdfStandardAnnotationType.StrikeOut,
            Color: AnnotationColor.Pink,
            Contents: "Worker strikeout note"
        } && annotation.QuadPoints.Count == 1);
        Assert.Contains(annotations, annotation => annotation is
        {
            PdfAnnotationId: "lpr-worker-text",
            Type: PdfStandardAnnotationType.Text,
            Color: AnnotationColor.Green,
            Contents: "Worker text note"
        });
    }

    private static PdfQuad CreateQuad(double left, double top, double right, double bottom) => new(
        left,
        top,
        right,
        top,
        left,
        bottom,
        right,
        bottom);

    private static PdfRect RectFromQuad(PdfQuad quad) => new(
        Math.Min(quad.X1, quad.X3),
        Math.Min(quad.Y3, quad.Y4),
        Math.Max(quad.X2, quad.X4),
        Math.Max(quad.Y1, quad.Y2));

    private static PdfWorkerClient CreateWorkerClient() => new(
        Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe"),
        NullLogger<PdfWorkerClient>.Instance);
}
