using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public sealed class DocumentHistoryService(
    ILocalDataStore localDataStore,
    IDocumentFingerprintService fingerprintService,
    IDocumentHistoryRepository documentRepository,
    IReadingStateRepository readingStateRepository,
    ISettingsService settingsService)
{
    public async Task<DocumentPersistenceContext?> RegisterSuccessfulOpenAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!localDataStore.IsAvailable)
        {
            return null;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        var normalizedPath = fingerprintService.NormalizePath(filePath);
        var fingerprint = await fingerprintService.ComputeAsync(normalizedPath, cancellationToken);
        var existing = await documentRepository.FindByFingerprintAsync(fingerprint, cancellationToken);
        var openedAt = DateTimeOffset.UtcNow;
        var candidate = new DocumentRecord(
            existing?.DocumentId ?? Guid.NewGuid(),
            fingerprint,
            normalizedPath,
            Path.GetFileName(normalizedPath),
            existing?.FirstOpenedAt ?? openedAt,
            openedAt,
            false);
        var persisted = await documentRepository.UpsertAsync(candidate, cancellationToken);

        if (settings.History.RecordRecentDocuments)
        {
            await documentRepository.RecordRecentAsync(persisted.DocumentId, openedAt, cancellationToken);
            var maximumRecent = Math.Clamp(settings.History.MaximumRecentDocuments, 1, 100);
            await documentRepository.PruneRecentAsync(maximumRecent, cancellationToken);
        }

        var readingState = settings.Session.SaveReadingPosition
            ? await readingStateRepository.GetAsync(persisted.DocumentId, cancellationToken)
            : null;
        return new DocumentPersistenceContext(persisted, readingState);
    }

    public async Task<IReadOnlyList<RecentDocument>> GetRecentAsync(CancellationToken cancellationToken)
    {
        if (!localDataStore.IsAvailable)
        {
            return [];
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        var maximumRecent = Math.Clamp(settings.History.MaximumRecentDocuments, 1, 100);
        var recent = await documentRepository.GetRecentAsync(maximumRecent, cancellationToken);
        var results = new List<RecentDocument>(recent.Count);
        foreach (var item in recent)
        {
            var isMissing = !fingerprintService.FileExists(item.Document.LastKnownPath);
            if (isMissing != item.Document.IsMissing)
            {
                await documentRepository.SetMissingAsync(item.Document.DocumentId, isMissing, cancellationToken);
            }

            results.Add(item with { Document = item.Document with { IsMissing = isMissing } });
        }

        return results;
    }

    public async Task SaveReadingStateAsync(
        Guid documentId,
        ReaderViewState viewState,
        double horizontalOffset,
        double verticalOffset,
        CancellationToken cancellationToken)
    {
        if (!localDataStore.IsAvailable)
        {
            return;
        }

        var settings = await settingsService.LoadAsync(cancellationToken);
        if (!settings.Session.SaveReadingPosition)
        {
            return;
        }

        await readingStateRepository.SaveAsync(new ReadingState(
            documentId,
            viewState.PageIndex,
            viewState.ZoomFactor,
            viewState.ZoomMode.ToString(),
            viewState.Rotation,
            horizontalOffset,
            verticalOffset,
            LeftSidebarVisible: false,
            LeftSidebarMode: "Search",
            TranslationPanelVisible: true,
            DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task RemoveRecentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (localDataStore.IsAvailable)
        {
            await documentRepository.RemoveFromRecentAsync(documentId, cancellationToken);
        }
    }

    /// <summary>
    /// A missing path is not a damaged PDF. Keep its document identity for local
    /// annotations and future fingerprint matching, but remove the unusable
    /// shortcut from recent history.
    /// </summary>
    public async Task MarkMissingAndRemoveFromRecentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!localDataStore.IsAvailable)
        {
            return;
        }

        await documentRepository.SetMissingAsync(documentId, isMissing: true, cancellationToken);
        await documentRepository.RemoveFromRecentAsync(documentId, cancellationToken);
    }

    public async Task ClearRecentAsync(CancellationToken cancellationToken)
    {
        if (localDataStore.IsAvailable)
        {
            await documentRepository.ClearRecentAsync(cancellationToken);
        }
    }

    public static ReaderViewState CreateReaderViewState(ReadingState state) => new(
        state.PageIndex,
        state.ZoomFactor,
        state.Rotation,
        Enum.TryParse<ReaderZoomMode>(state.ViewMode, out var mode) ? mode : ReaderZoomMode.ActualZoom,
        PageSize: null);
}

public sealed record DocumentPersistenceContext(
    DocumentRecord Document,
    ReadingState? ReadingState);
