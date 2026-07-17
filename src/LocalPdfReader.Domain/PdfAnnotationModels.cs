namespace LocalPdfReader.Domain;

public enum PdfStandardAnnotationType
{
    Highlight,
    Underline,
    StrikeOut,
    Text,
    Unsupported
}

public enum PdfAnnotationWriteKind
{
    CreateOrUpdate,
    Delete
}

public enum PdfAnnotationSaveMode
{
    Incremental,
    Full
}

public sealed record PdfQuad(
    double X1,
    double Y1,
    double X2,
    double Y2,
    double X3,
    double Y3,
    double X4,
    double Y4);

public sealed record PdfStandardAnnotation(
    string PdfAnnotationId,
    PdfStandardAnnotationType Type,
    int PageIndex,
    AnnotationColor? Color,
    IReadOnlyList<PdfQuad> QuadPoints,
    PdfRect Rect,
    string? Contents,
    bool IsSupported);

public sealed record PdfAnnotationWriteOperation(
    PdfAnnotationWriteKind Kind,
    string PdfAnnotationId,
    PdfStandardAnnotationType Type,
    int PageIndex,
    AnnotationColor Color,
    IReadOnlyList<PdfQuad> QuadPoints,
    PdfRect Rect,
    string? Contents);

public sealed record PdfAnnotationSaveResult(
    string SavedFilePath,
    IReadOnlyList<PdfStandardAnnotation> VerifiedAnnotations);
