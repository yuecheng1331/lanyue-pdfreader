namespace LocalPdfReader.Application.PdfWorker;

public interface IPdfWorkerClient : IAsyncDisposable
{
    event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected;

    Task StartAsync(CancellationToken cancellationToken);

    Task<Domain.PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password, CancellationToken cancellationToken);

    Task CloseDocumentAsync(Domain.DocumentId documentId, CancellationToken cancellationToken);

    Task<Domain.RenderedPageDescriptor> RenderPageAsync(Domain.PageRenderRequest request, CancellationToken cancellationToken);

    Task<Domain.PageTextData> GetPageTextAsync(Domain.DocumentId documentId, int pageIndex, CancellationToken cancellationToken);

    Task<Domain.TextHitTestResult?> HitTestTextAsync(
        Domain.DocumentId documentId,
        int pageIndex,
        Domain.PdfPoint point,
        double tolerance,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Domain.PdfOutlineItem>> GetOutlineAsync(
        Domain.DocumentId documentId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This PDF worker client does not support document outlines.");

    Task<IReadOnlyList<Domain.PdfStandardAnnotation>> GetPdfAnnotationsAsync(
        Domain.DocumentId documentId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This PDF worker client does not support PDF annotations.");

    Task<Domain.PdfAnnotationSaveResult> SavePdfAnnotationsAsync(
        Domain.DocumentId documentId,
        IReadOnlyList<Domain.PdfAnnotationWriteOperation> operations,
        string destinationFilePath,
        Domain.PdfAnnotationSaveMode saveMode,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This PDF worker client does not support PDF annotation saves.");

    IAsyncEnumerable<Domain.SearchUpdate> SearchDocumentAsync(
        Domain.DocumentId documentId,
        Guid searchSessionId,
        string query,
        bool matchCase,
        bool wholeWord,
        int batchSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This PDF worker client does not support document search.");

    Task ReleaseSharedMemoryAsync(string memoryMapName, CancellationToken cancellationToken);

    Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public enum PdfWorkerDisconnectReason
{
    ProcessExited,
    PipeDisconnected
}

public sealed class PdfWorkerDisconnectedEventArgs(
    PdfWorkerDisconnectReason reason,
    int? exitCode,
    Exception? exception) : EventArgs
{
    public PdfWorkerDisconnectReason Reason { get; } = reason;

    public int? ExitCode { get; } = exitCode;

    public Exception? Exception { get; } = exception;
}
