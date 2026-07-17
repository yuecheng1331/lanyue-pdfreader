namespace LocalPdfReader.PdfProtocol;

public static class PdfWorkerProtocol
{
    public const int CurrentVersion = 1;
}

public static class PipeMessageTypes
{
    public const string HandshakeRequest = nameof(HandshakeRequest);
    public const string HandshakeResponse = nameof(HandshakeResponse);
    public const string CancelRequest = nameof(CancelRequest);
    public const string CancelResponse = nameof(CancelResponse);
    public const string PingRequest = nameof(PingRequest);
    public const string PingResponse = nameof(PingResponse);
    public const string OpenDocumentRequest = nameof(OpenDocumentRequest);
    public const string OpenDocumentResponse = nameof(OpenDocumentResponse);
    public const string CloseDocumentRequest = nameof(CloseDocumentRequest);
    public const string CloseDocumentResponse = nameof(CloseDocumentResponse);
    public const string RenderPageRequest = nameof(RenderPageRequest);
    public const string RenderPageResponse = nameof(RenderPageResponse);
    public const string GetPageTextRequest = nameof(GetPageTextRequest);
    public const string GetPageTextResponse = nameof(GetPageTextResponse);
    public const string HitTestTextRequest = nameof(HitTestTextRequest);
    public const string HitTestTextResponse = nameof(HitTestTextResponse);
    public const string GetOutlineRequest = nameof(GetOutlineRequest);
    public const string GetOutlineResponse = nameof(GetOutlineResponse);
    public const string GetPdfAnnotationsRequest = nameof(GetPdfAnnotationsRequest);
    public const string GetPdfAnnotationsResponse = nameof(GetPdfAnnotationsResponse);
    public const string SavePdfAnnotationsRequest = nameof(SavePdfAnnotationsRequest);
    public const string SavePdfAnnotationsResponse = nameof(SavePdfAnnotationsResponse);
    public const string SearchRequest = nameof(SearchRequest);
    public const string SearchStartedResponse = nameof(SearchStartedResponse);
    public const string SearchProgressResponse = nameof(SearchProgressResponse);
    public const string SearchResultBatchResponse = nameof(SearchResultBatchResponse);
    public const string SearchCompletedResponse = nameof(SearchCompletedResponse);
    public const string SearchCancelledResponse = nameof(SearchCancelledResponse);
    public const string SearchFailedResponse = nameof(SearchFailedResponse);
    public const string ReleaseSharedMemoryRequest = nameof(ReleaseSharedMemoryRequest);
    public const string ReleaseSharedMemoryResponse = nameof(ReleaseSharedMemoryResponse);
    public const string ErrorResponse = nameof(ErrorResponse);
}

public sealed record HandshakeRequest;

public sealed record HandshakeResponse(bool IsAccepted, int WorkerProtocolVersion);

public sealed record CancelRequest(Guid TargetRequestId);

public sealed record CancelResponse(Guid TargetRequestId, bool WasCanceled);

public sealed record PingRequest;

public sealed record PingResponse;

public sealed record OpenDocumentRequest(string FilePath, string? Password);

public sealed record OpenDocumentResponse(Domain.PdfDocumentInfo DocumentInfo);

public sealed record CloseDocumentRequest;

public sealed record CloseDocumentResponse;

public sealed record RenderPageRequest(Domain.PageRenderRequest RenderRequest);

public sealed record RenderPageResponse(Domain.RenderedPageDescriptor PageDescriptor);

public sealed record GetPageTextRequest(int PageIndex);

public sealed record GetPageTextResponse(Domain.PageTextData PageText);

public sealed record HitTestTextRequest(int PageIndex, Domain.PdfPoint Point, double Tolerance);

public sealed record HitTestTextResponse(Domain.TextHitTestResult? Hit);

public sealed record GetOutlineRequest;

public sealed record GetOutlineResponse(IReadOnlyList<Domain.PdfOutlineItem> Items);

public sealed record GetPdfAnnotationsRequest;

public sealed record GetPdfAnnotationsResponse(IReadOnlyList<Domain.PdfStandardAnnotation> Annotations);

public sealed record SavePdfAnnotationsRequest(
    IReadOnlyList<Domain.PdfAnnotationWriteOperation> Operations,
    string DestinationFilePath,
    Domain.PdfAnnotationSaveMode SaveMode);

public sealed record SavePdfAnnotationsResponse(Domain.PdfAnnotationSaveResult Result);

public sealed record ReleaseSharedMemoryRequest(string MemoryMapName);

public sealed record ReleaseSharedMemoryResponse(bool WasReleased);

public sealed record ErrorResponse(string ErrorCode, string UserMessage);

public static class PdfWorkerErrorCodes
{
    public const string RequestFailed = nameof(RequestFailed);
    public const string PasswordRequiredOrInvalid = nameof(PasswordRequiredOrInvalid);
}
