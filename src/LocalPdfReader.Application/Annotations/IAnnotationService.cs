using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Annotations;

public interface IAnnotationService
{
    Task<IReadOnlyList<TextHighlightAnnotation>> GetByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<TextHighlightAnnotation> CreateHighlightAsync(
        DocumentRecord document,
        TextSelection selection,
        AnnotationColor color,
        string? note,
        CancellationToken cancellationToken);

    Task<TextHighlightAnnotation> UpdateNoteAsync(
        TextHighlightAnnotation annotation,
        string? note,
        CancellationToken cancellationToken);

    Task<TextHighlightAnnotation> UpdateColorAsync(
        TextHighlightAnnotation annotation,
        AnnotationColor color,
        CancellationToken cancellationToken);

    Task<TextHighlightAnnotation> UpdateAsync(
        TextHighlightAnnotation annotation,
        AnnotationColor color,
        string? note,
        CancellationToken cancellationToken);

    Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken);
}
