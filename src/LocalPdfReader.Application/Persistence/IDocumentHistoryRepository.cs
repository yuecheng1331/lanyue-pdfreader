using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public interface IDocumentHistoryRepository
{
    Task<DocumentRecord?> FindByFingerprintAsync(
        DocumentFingerprint fingerprint,
        CancellationToken cancellationToken);

    Task<DocumentRecord> UpsertAsync(
        DocumentRecord document,
        CancellationToken cancellationToken);

    Task RecordRecentAsync(
        Guid documentId,
        DateTimeOffset openedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecentDocument>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken);

    Task RemoveFromRecentAsync(Guid documentId, CancellationToken cancellationToken);

    Task ClearRecentAsync(CancellationToken cancellationToken);

    Task PruneRecentAsync(int maximumUnpinnedCount, CancellationToken cancellationToken);

    Task SetMissingAsync(Guid documentId, bool isMissing, CancellationToken cancellationToken);
}
