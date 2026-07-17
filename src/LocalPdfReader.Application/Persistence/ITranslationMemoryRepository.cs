using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public interface ITranslationMemoryRepository
{
    Task<TranslationCacheEntry?> FindCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken);

    Task SaveCacheAsync(
        TranslationCacheEntry entry,
        CancellationToken cancellationToken);

    Task SaveCacheSegmentsAsync(
        IReadOnlyList<TranslationCacheSegment> segments,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationCacheEntry>> GetReusableCacheCandidatesAsync(
        string targetLanguage,
        TranslationPreset preset,
        string? customInstruction,
        string? glossaryInstruction,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationCacheSegment>> GetReusableCacheSegmentsAsync(
        string targetLanguage,
        TranslationPreset preset,
        string? customInstruction,
        string? glossaryInstruction,
        int limit,
        CancellationToken cancellationToken);

    Task PruneCacheAsync(
        int maximumEntries,
        CancellationToken cancellationToken);

    Task AddHistoryAsync(
        TranslationHistoryEntry entry,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationHistoryEntry>> GetRecentHistoryAsync(
        int limit,
        CancellationToken cancellationToken);

    Task UpdateHistorySampleKindAsync(
        Guid historyId,
        TranslationSampleKind sampleKind,
        CancellationToken cancellationToken);

    Task DeleteHistoryAndRelatedCacheAsync(
        TranslationHistoryEntry entry,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslationGlossaryEntry>> GetGlossaryAsync(
        CancellationToken cancellationToken);

    Task<TranslationGlossaryEntry> UpsertGlossaryEntryAsync(
        string sourceTerm,
        string targetTerm,
        CancellationToken cancellationToken);

    Task DeleteGlossaryEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken);
}
