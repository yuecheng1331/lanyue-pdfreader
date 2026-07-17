namespace LocalPdfReader.Domain;

public sealed record PdfDocumentInfo(
    DocumentId DocumentId,
    string FileName,
    int PageCount,
    bool IsEncrypted,
    bool HasTextLayer,
    string? Title,
    string? Author);

public sealed record PdfOutlineItem(
    string Title,
    int? PageIndex,
    IReadOnlyList<PdfOutlineItem> Children);

public enum PageRotation
{
    Rotate0 = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}

public enum RenderQuality
{
    Preview,
    Normal,
    High
}

public readonly record struct PdfSize(double Width, double Height);

public sealed record PageRenderRequest(
    Guid RequestId,
    DocumentId DocumentId,
    int PageIndex,
    double ZoomFactor,
    PageRotation Rotation,
    double DpiScaleX,
    double DpiScaleY,
    RenderQuality Quality);

public sealed record RenderedPageDescriptor(
    Guid RequestId,
    DocumentId DocumentId,
    int PageIndex,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    string PixelFormat,
    string MemoryMapName,
    long DataLength,
    PdfSize OriginalPageSize);
