using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Annotations;

public sealed class PdfAnnotationSyncService : IPdfAnnotationSyncService
{
    public Task<IReadOnlyList<PdfStandardAnnotation>> ReadPdfAnnotationsAsync(
        IPdfWorkerClient workerClient,
        DocumentId documentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workerClient);
        return workerClient.GetPdfAnnotationsAsync(documentId, cancellationToken);
    }

    public async Task<PdfAnnotationSaveResult> SaveLocalHighlightsAsync(
        IPdfWorkerClient workerClient,
        DocumentId documentId,
        IReadOnlyList<TextHighlightAnnotation> annotations,
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workerClient);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var localAnnotationIds = annotations
            .Select(annotation => ToPdfAnnotationId(annotation.AnnotationId))
            .ToHashSet(StringComparer.Ordinal);
        var existingAnnotations = await workerClient.GetPdfAnnotationsAsync(documentId, cancellationToken);
        var deleteOperations = existingAnnotations
            .Where(annotation =>
                annotation.PdfAnnotationId.StartsWith("LocalPdfReader:", StringComparison.Ordinal) &&
                !localAnnotationIds.Contains(annotation.PdfAnnotationId))
            .Select(annotation => new PdfAnnotationWriteOperation(
                PdfAnnotationWriteKind.Delete,
                annotation.PdfAnnotationId,
                annotation.Type == PdfStandardAnnotationType.Unsupported
                    ? PdfStandardAnnotationType.Highlight
                    : annotation.Type,
                annotation.PageIndex,
                annotation.Color ?? AnnotationColor.Yellow,
                [],
                annotation.Rect,
                null));
        var createOrUpdateOperations = annotations
            .Select(ToCreateOrUpdateOperation)
            .ToArray();
        var operations = deleteOperations.Concat(createOrUpdateOperations).ToArray();

        return await workerClient.SavePdfAnnotationsAsync(
            documentId,
            operations,
            destinationFilePath,
            saveMode,
            cancellationToken);
    }

    public static PdfAnnotationWriteOperation ToCreateOrUpdateOperation(TextHighlightAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.AnnotationId == Guid.Empty ||
            annotation.PageIndex < 0 ||
            annotation.Rectangles.Count == 0)
        {
            throw new ArgumentException("The local annotation cannot be written to a PDF.", nameof(annotation));
        }

        var quads = annotation.Rectangles.Select(ToQuad).ToArray();
        return new PdfAnnotationWriteOperation(
            PdfAnnotationWriteKind.CreateOrUpdate,
            ToPdfAnnotationId(annotation.AnnotationId),
            PdfStandardAnnotationType.Highlight,
            annotation.PageIndex,
            annotation.Color,
            quads,
            Union(annotation.Rectangles),
            annotation.Note);
    }

    public static string ToPdfAnnotationId(Guid annotationId)
    {
        if (annotationId == Guid.Empty)
        {
            throw new ArgumentException("The annotation identity is required.", nameof(annotationId));
        }

        return $"LocalPdfReader:{annotationId:D}";
    }

    private static PdfQuad ToQuad(PdfRect rect) => new(
        rect.Left,
        rect.Top,
        rect.Right,
        rect.Top,
        rect.Left,
        rect.Bottom,
        rect.Right,
        rect.Bottom);

    private static PdfRect Union(IReadOnlyList<PdfRect> rectangles)
    {
        return new PdfRect(
            rectangles.Min(rectangle => rectangle.Left),
            rectangles.Min(rectangle => rectangle.Bottom),
            rectangles.Max(rectangle => rectangle.Right),
            rectangles.Max(rectangle => rectangle.Top));
    }
}
