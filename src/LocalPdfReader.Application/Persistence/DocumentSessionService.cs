using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

/// <summary>
/// Provides the small persistence boundary for manually restored tab sessions.
/// A database outage must never prevent normal PDF reading.
/// </summary>
public sealed class DocumentSessionService(
    ILocalDataStore localDataStore,
    IDocumentSessionRepository sessionRepository)
{
    public Task SaveAsync(DocumentSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return localDataStore.IsAvailable
            ? sessionRepository.SaveAsync(snapshot, cancellationToken)
            : Task.CompletedTask;
    }

    public Task<DocumentSessionSnapshot?> GetAsync(CancellationToken cancellationToken) =>
        localDataStore.IsAvailable
            ? sessionRepository.GetAsync(cancellationToken)
            : Task.FromResult<DocumentSessionSnapshot?>(null);

    public Task ClearAsync(CancellationToken cancellationToken) =>
        localDataStore.IsAvailable
            ? sessionRepository.ClearAsync(cancellationToken)
            : Task.CompletedTask;
}
