namespace LocalPdfReader.Domain;

public sealed record DocumentFingerprint(
    string FastFingerprint,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc);

public sealed record DocumentRecord(
    Guid DocumentId,
    DocumentFingerprint Fingerprint,
    string LastKnownPath,
    string FileName,
    DateTimeOffset FirstOpenedAt,
    DateTimeOffset LastOpenedAt,
    bool IsMissing);

public sealed record RecentDocument(
    DocumentRecord Document,
    DateTimeOffset LastOpenedAt,
    bool IsPinned,
    int OpenCount,
    int? LastPageIndex);

public sealed record ReadingState(
    Guid DocumentId,
    int PageIndex,
    double ZoomFactor,
    string ViewMode,
    PageRotation Rotation,
    double HorizontalOffset,
    double VerticalOffset,
    bool LeftSidebarVisible,
    string LeftSidebarMode,
    bool TranslationPanelVisible,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A persisted, manually restorable document session. The PDF path is stored with
/// the snapshot so files moved or removed since shutdown can be skipped safely.
/// </summary>
public sealed record DocumentSessionSnapshot(
    IReadOnlyList<DocumentSessionTab> Tabs,
    int ActiveTabIndex,
    DateTimeOffset UpdatedAt);

public sealed record DocumentSessionTab(
    Guid DocumentId,
    string FilePath,
    bool IsMissing);

public enum AnnotationColor
{
    Yellow,
    Green,
    Blue,
    Pink
}

public sealed record TextHighlightAnnotation(
    Guid AnnotationId,
    DocumentFingerprint DocumentFingerprint,
    int PageIndex,
    int CharacterStart,
    int CharacterCount,
    string SelectedTextPreview,
    AnnotationColor Color,
    IReadOnlyList<PdfRect> Rectangles,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt);
