using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public interface IReadingStateRepository
{
    Task<ReadingState?> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task SaveAsync(ReadingState state, CancellationToken cancellationToken);

    Task DeleteAsync(Guid documentId, CancellationToken cancellationToken);
}
