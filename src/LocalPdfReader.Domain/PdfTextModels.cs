namespace LocalPdfReader.Domain;

public readonly record struct PdfPoint(double X, double Y);

public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top);

public readonly record struct ViewPoint(double X, double Y);

public readonly record struct ViewRect(double X, double Y, double Width, double Height);

public sealed record TextGlyph(
    int CharacterIndex,
    string Text,
    PdfRect Bounds,
    int LineIndex,
    int BlockIndex);

public sealed record PageTextData(
    DocumentId DocumentId,
    int PageIndex,
    string RawText,
    IReadOnlyList<TextGlyph> Glyphs);

public sealed record TextHitTestResult(int CharacterIndex, string Text, PdfRect Bounds);

public sealed record TextSelection(
    DocumentId DocumentId,
    int PageIndex,
    int StartCharacterIndex,
    int EndCharacterIndex,
    string RawText,
    string NormalizedText,
    IReadOnlyList<PdfRect> HighlightRectangles);

public sealed record PageTransformContext(
    PdfSize PdfPageSize,
    double ZoomFactor,
    PageRotation Rotation,
    double DpiScaleX,
    double DpiScaleY,
    double PageOffsetX,
    double PageOffsetY);
