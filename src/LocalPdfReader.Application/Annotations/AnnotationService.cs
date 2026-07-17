using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Annotations;

public sealed class AnnotationService(
    IAnnotationRepository annotationRepository,
    TimeProvider timeProvider) : IAnnotationService
{
    private const int MaximumPreviewLength = 300;

    public Task<IReadOnlyList<TextHighlightAnnotation>> GetByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("The persisted document identity is required.", nameof(documentId));
        }

        return annotationRepository.GetByDocumentAsync(documentId, cancellationToken);
    }

    public async Task<TextHighlightAnnotation> CreateHighlightAsync(
        DocumentRecord document,
        TextSelection selection,
        AnnotationColor color,
        string? note,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);

        if (document.DocumentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(document.Fingerprint.FastFingerprint))
        {
            throw new ArgumentException("The annotation document has not been persisted.", nameof(document));
        }

        if (selection.PageIndex < 0 ||
            selection.StartCharacterIndex < 0 ||
            selection.EndCharacterIndex < selection.StartCharacterIndex ||
            string.IsNullOrWhiteSpace(selection.NormalizedText) ||
            selection.HighlightRectangles.Count == 0)
        {
            throw new ArgumentException("The text selection cannot be converted into a highlight.", nameof(selection));
        }

        if (!Enum.IsDefined(color))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        var now = timeProvider.GetUtcNow();
        var preview = selection.NormalizedText.Length <= MaximumPreviewLength
            ? selection.NormalizedText
            : selection.NormalizedText[..MaximumPreviewLength];
        var annotation = new TextHighlightAnnotation(
            Guid.NewGuid(),
            document.Fingerprint,
            selection.PageIndex,
            selection.StartCharacterIndex,
            checked(selection.EndCharacterIndex - selection.StartCharacterIndex + 1),
            preview,
            color,
            selection.HighlightRectangles.ToArray(),
            note,
            now,
            now);

        await annotationRepository.AddAsync(annotation, cancellationToken);
        return annotation;
    }

    public async Task<TextHighlightAnnotation> UpdateNoteAsync(
        TextHighlightAnnotation annotation,
        string? note,
        CancellationToken cancellationToken)
    {
        return await UpdateAsync(annotation, annotation.Color, note, cancellationToken);
    }

    public async Task<TextHighlightAnnotation> UpdateColorAsync(
        TextHighlightAnnotation annotation,
        AnnotationColor color,
        CancellationToken cancellationToken)
    {
        return await UpdateAsync(annotation, color, annotation.Note, cancellationToken);
    }

    public async Task<TextHighlightAnnotation> UpdateAsync(
        TextHighlightAnnotation annotation,
        AnnotationColor color,
        string? note,
        CancellationToken cancellationToken)
    {
        ValidateExistingAnnotation(annotation);
        if (!Enum.IsDefined(color))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        var updated = annotation with
        {
            Color = color,
            Note = note,
            ModifiedAt = timeProvider.GetUtcNow()
        };
        await annotationRepository.UpdateAsync(updated, cancellationToken);
        return updated;
    }

    public Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken)
    {
        if (annotationId == Guid.Empty)
        {
            throw new ArgumentException("The annotation identity is required.", nameof(annotationId));
        }

        return annotationRepository.DeleteAsync(annotationId, cancellationToken);
    }

    private static void ValidateExistingAnnotation(TextHighlightAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.AnnotationId == Guid.Empty)
        {
            throw new ArgumentException("The annotation identity is required.", nameof(annotation));
        }
    }
}
