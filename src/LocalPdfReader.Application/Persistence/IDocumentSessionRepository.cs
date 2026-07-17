using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public interface IDocumentSessionRepository
{
    Task SaveAsync(DocumentSessionSnapshot snapshot, CancellationToken cancellationToken);

    Task<DocumentSessionSnapshot?> GetAsync(CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
