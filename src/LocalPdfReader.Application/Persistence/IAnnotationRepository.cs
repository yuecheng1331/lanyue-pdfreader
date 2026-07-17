using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public interface IAnnotationRepository
{
    Task<IReadOnlyList<TextHighlightAnnotation>> GetByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task AddAsync(TextHighlightAnnotation annotation, CancellationToken cancellationToken);

    Task UpdateAsync(TextHighlightAnnotation annotation, CancellationToken cancellationToken);

    Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken);
}
