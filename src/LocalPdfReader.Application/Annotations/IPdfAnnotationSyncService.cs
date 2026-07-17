using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Annotations;

public interface IPdfAnnotationSyncService
{
    Task<IReadOnlyList<PdfStandardAnnotation>> ReadPdfAnnotationsAsync(
        IPdfWorkerClient workerClient,
        DocumentId documentId,
        CancellationToken cancellationToken);

    Task<PdfAnnotationSaveResult> SaveLocalHighlightsAsync(
        IPdfWorkerClient workerClient,
        DocumentId documentId,
        IReadOnlyList<TextHighlightAnnotation> annotations,
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode,
        CancellationToken cancellationToken);
}
