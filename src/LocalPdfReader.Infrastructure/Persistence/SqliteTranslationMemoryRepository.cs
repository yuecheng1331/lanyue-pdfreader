using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;
using Microsoft.Data.Sqlite;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class SqliteTranslationMemoryRepository(SqliteLocalDatabase database)
    : ITranslationMemoryRepository
{
    public async Task<TranslationCacheEntry?> FindCacheAsync(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        TranslationCacheEntry? entry;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT CacheKey, SourceText, TranslatedText, TargetLanguage, Preset,
                       CustomInstruction, GlossaryInstruction, CreatedAt, LastUsedAt, UseCount
                FROM TranslationCache
                WHERE CacheKey = $cacheKey;
                """;
            command.Parameters.AddWithValue("$cacheKey", cacheKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            entry = await reader.ReadAsync(cancellationToken) ? ReadCacheEntry(reader) : null;
        }

        if (entry is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE TranslationCache
                SET LastUsedAt = $lastUsedAt,
                    UseCount = UseCount + 1
                WHERE CacheKey = $cacheKey;
                """;
            update.Parameters.AddWithValue("$cacheKey", cacheKey);
            update.Parameters.AddWithValue(
                "$lastUsedAt",
                SqliteValueConverters.FormatDateTime(DateTimeOffset.UtcNow));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task SaveCacheAsync(
        TranslationCacheEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateCacheEntry(entry);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TranslationCache
                (CacheKey, SourceText, TranslatedText, TargetLanguage, Preset,
                 CustomInstruction, GlossaryInstruction, CreatedAt, LastUsedAt, UseCount)
            VALUES
                ($cacheKey, $sourceText, $translatedText, $targetLanguage, $preset,
                 $customInstruction, $glossaryInstruction, $createdAt, $lastUsedAt, $useCount)
            ON CONFLICT(CacheKey) DO UPDATE SET
                SourceText = excluded.SourceText,
                TranslatedText = excluded.TranslatedText,
                TargetLanguage = excluded.TargetLanguage,
                Preset = excluded.Preset,
                CustomInstruction = excluded.CustomInstruction,
                GlossaryInstruction = excluded.GlossaryInstruction,
                LastUsedAt = excluded.LastUsedAt,
                UseCount = TranslationCache.UseCount + 1;
            """;
        AddCacheParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveCacheSegmentsAsync(
        IReadOnlyList<TranslationCacheSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return;
        }

        foreach (var segment in segments)
        {
            ValidateCacheSegment(segment);
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var segment in segments)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO TranslationCacheSegments
                    (CacheKey, SegmentIndex, SourceText, NormalizedSourceText, TranslatedText,
                     TargetLanguage, Preset, CustomInstruction, GlossaryInstruction,
                     CreatedAt, LastUsedAt, UseCount)
                VALUES
                    ($cacheKey, $segmentIndex, $sourceText, $normalizedSourceText, $translatedText,
                     $targetLanguage, $preset, $customInstruction, $glossaryInstruction,
                     $createdAt, $lastUsedAt, $useCount)
                ON CONFLICT(CacheKey, SegmentIndex) DO UPDATE SET
                    SourceText = excluded.SourceText,
                    NormalizedSourceText = excluded.NormalizedSourceText,
                    TranslatedText = excluded.TranslatedText,
                    TargetLanguage = excluded.TargetLanguage,
                    Preset = excluded.Preset,
                    CustomInstruction = excluded.CustomInstruction,
                    GlossaryInstruction = excluded.GlossaryInstruction,
                    LastUsedAt = excluded.LastUsedAt,
                    UseCount = TranslationCacheSegments.UseCount + 1;
                """;
            AddCacheSegmentParameters(command, segment);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationCacheEntry>> GetReusableCacheCandidatesAsync(
        string targetLanguage,
        TranslationPreset preset,
        string? customInstruction,
        string? glossaryInstruction,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage) || !Enum.IsDefined(preset) || limit is < 1 or > 200)
        {
            throw new ArgumentException("The reusable cache query is invalid.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CacheKey, SourceText, TranslatedText, TargetLanguage, Preset,
                   CustomInstruction, GlossaryInstruction, CreatedAt, LastUsedAt, UseCount
            FROM TranslationCache
            WHERE TargetLanguage = $targetLanguage
              AND Preset = $preset
              AND COALESCE(CustomInstruction, '') = COALESCE($customInstruction, '')
              AND COALESCE(GlossaryInstruction, '') = COALESCE($glossaryInstruction, '')
            ORDER BY LastUsedAt DESC, UseCount DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$targetLanguage", targetLanguage);
        command.Parameters.AddWithValue("$preset", preset.ToString());
        command.Parameters.AddWithValue("$customInstruction", (object?)customInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$glossaryInstruction", (object?)glossaryInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<TranslationCacheEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCacheEntry(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<TranslationCacheSegment>> GetReusableCacheSegmentsAsync(
        string targetLanguage,
        TranslationPreset preset,
        string? customInstruction,
        string? glossaryInstruction,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage) || !Enum.IsDefined(preset) || limit is < 1 or > 1000)
        {
            throw new ArgumentException("The reusable cache segment query is invalid.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CacheKey, SegmentIndex, SourceText, NormalizedSourceText, TranslatedText,
                   TargetLanguage, Preset, CustomInstruction, GlossaryInstruction,
                   CreatedAt, LastUsedAt, UseCount
            FROM TranslationCacheSegments
            WHERE TargetLanguage = $targetLanguage
              AND Preset = $preset
              AND COALESCE(CustomInstruction, '') = COALESCE($customInstruction, '')
              AND COALESCE(GlossaryInstruction, '') = COALESCE($glossaryInstruction, '')
            ORDER BY LastUsedAt DESC, UseCount DESC, CacheKey, SegmentIndex
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$targetLanguage", targetLanguage);
        command.Parameters.AddWithValue("$preset", preset.ToString());
        command.Parameters.AddWithValue("$customInstruction", (object?)customInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$glossaryInstruction", (object?)glossaryInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<TranslationCacheSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCacheSegment(reader));
        }

        return results;
    }

    public async Task PruneCacheAsync(
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        if (maximumEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = maximumEntries == 0
            ? "DELETE FROM TranslationCache;"
            : """
              DELETE FROM TranslationCache
              WHERE CacheKey NOT IN (
                  SELECT CacheKey
                  FROM TranslationCache
                  ORDER BY LastUsedAt DESC, UseCount DESC
                  LIMIT $maximumEntries
              );
              """;
        if (maximumEntries > 0)
        {
            command.Parameters.AddWithValue("$maximumEntries", maximumEntries);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddHistoryAsync(
        TranslationHistoryEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateHistoryEntry(entry);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TranslationHistory
                (HistoryId, SourcePreview, SourceText, TranslatedText, TargetLanguage, Preset,
                 SourceKind, SourceScope, CreatedAt, CharacterCount, SegmentCount, UsedCache, SampleKind)
            VALUES
                ($historyId, $sourcePreview, $sourceText, $translatedText, $targetLanguage, $preset,
                 $sourceKind, $sourceScope, $createdAt, $characterCount, $segmentCount, $usedCache, $sampleKind);
            """;
        command.Parameters.AddWithValue("$historyId", entry.HistoryId.ToString("D"));
        command.Parameters.AddWithValue("$sourcePreview", entry.SourcePreview);
        command.Parameters.AddWithValue("$sourceText", entry.SourceText);
        command.Parameters.AddWithValue("$translatedText", entry.TranslatedText);
        command.Parameters.AddWithValue("$targetLanguage", entry.TargetLanguage);
        command.Parameters.AddWithValue("$preset", entry.Preset.ToString());
        command.Parameters.AddWithValue("$sourceKind", entry.SourceKind.ToString());
        command.Parameters.AddWithValue("$sourceScope", (object?)entry.SourceScope ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteValueConverters.FormatDateTime(entry.CreatedAt));
        command.Parameters.AddWithValue("$characterCount", entry.CharacterCount);
        command.Parameters.AddWithValue("$segmentCount", entry.SegmentCount);
        command.Parameters.AddWithValue("$usedCache", SqliteValueConverters.FormatBoolean(entry.UsedCache));
        command.Parameters.AddWithValue("$sampleKind", entry.SampleKind.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationHistoryEntry>> GetRecentHistoryAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT HistoryId, SourcePreview, SourceText, TranslatedText, TargetLanguage,
                   Preset, SourceKind, SourceScope, CreatedAt, CharacterCount, SegmentCount, UsedCache, SampleKind
            FROM TranslationHistory
            ORDER BY CreatedAt DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<TranslationHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadHistoryEntry(reader));
        }

        return results;
    }

    public async Task UpdateHistorySampleKindAsync(
        Guid historyId,
        TranslationSampleKind sampleKind,
        CancellationToken cancellationToken)
    {
        if (historyId == Guid.Empty || !Enum.IsDefined(sampleKind))
        {
            throw new ArgumentException("A valid history id and sample kind are required.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE TranslationHistory
            SET SampleKind = $sampleKind
            WHERE HistoryId = $historyId;
            """;
        command.Parameters.AddWithValue("$historyId", historyId.ToString("D"));
        command.Parameters.AddWithValue("$sampleKind", sampleKind.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteHistoryAndRelatedCacheAsync(
        TranslationHistoryEntry entry,
        CancellationToken cancellationToken)
    {
        ValidateHistoryEntry(entry);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteSegments = connection.CreateCommand())
        {
            deleteSegments.Transaction = transaction;
            deleteSegments.CommandText = """
                DELETE FROM TranslationCacheSegments
                WHERE CacheKey IN (
                    SELECT CacheKey
                    FROM TranslationCache
                    WHERE SourceText = $sourceText
                      AND TranslatedText = $translatedText
                      AND TargetLanguage = $targetLanguage
                      AND Preset = $preset
                )
                   OR (
                    SourceText = $sourceText
                    AND TranslatedText = $translatedText
                    AND TargetLanguage = $targetLanguage
                    AND Preset = $preset
                );
                """;
            AddHistoryCacheMatchParameters(deleteSegments, entry);
            await deleteSegments.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCache = connection.CreateCommand())
        {
            deleteCache.Transaction = transaction;
            deleteCache.CommandText = """
                DELETE FROM TranslationCache
                WHERE SourceText = $sourceText
                  AND TranslatedText = $translatedText
                  AND TargetLanguage = $targetLanguage
                  AND Preset = $preset;
                """;
            AddHistoryCacheMatchParameters(deleteCache, entry);
            await deleteCache.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteHistory = connection.CreateCommand())
        {
            deleteHistory.Transaction = transaction;
            deleteHistory.CommandText = "DELETE FROM TranslationHistory WHERE HistoryId = $historyId;";
            deleteHistory.Parameters.AddWithValue("$historyId", entry.HistoryId.ToString("D"));
            await deleteHistory.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TranslationGlossaryEntry>> GetGlossaryAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EntryId, SourceTerm, TargetTerm, CreatedAt, ModifiedAt
            FROM TranslationGlossary
            ORDER BY SourceTerm COLLATE NOCASE;
            """;
        var results = new List<TranslationGlossaryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadGlossaryEntry(reader));
        }

        return results;
    }

    public async Task<TranslationGlossaryEntry> UpsertGlossaryEntryAsync(
        string sourceTerm,
        string targetTerm,
        CancellationToken cancellationToken)
    {
        sourceTerm = ValidateTerm(sourceTerm, nameof(sourceTerm));
        targetTerm = ValidateTerm(targetTerm, nameof(targetTerm));
        var now = DateTimeOffset.UtcNow;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO TranslationGlossary
                    (EntryId, SourceTerm, TargetTerm, CreatedAt, ModifiedAt)
                VALUES
                    ($entryId, $sourceTerm, $targetTerm, $createdAt, $modifiedAt)
                ON CONFLICT(SourceTerm) DO UPDATE SET
                    TargetTerm = excluded.TargetTerm,
                    ModifiedAt = excluded.ModifiedAt;
                """;
            command.Parameters.AddWithValue("$entryId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$sourceTerm", sourceTerm);
            command.Parameters.AddWithValue("$targetTerm", targetTerm);
            command.Parameters.AddWithValue("$createdAt", SqliteValueConverters.FormatDateTime(now));
            command.Parameters.AddWithValue("$modifiedAt", SqliteValueConverters.FormatDateTime(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT EntryId, SourceTerm, TargetTerm, CreatedAt, ModifiedAt
            FROM TranslationGlossary
            WHERE SourceTerm = $sourceTerm;
            """;
        select.Parameters.AddWithValue("$sourceTerm", sourceTerm);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var entry = await reader.ReadAsync(cancellationToken)
            ? ReadGlossaryEntry(reader)
            : throw new InvalidOperationException("The glossary entry was not persisted.");
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task DeleteGlossaryEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken)
    {
        if (entryId == Guid.Empty)
        {
            throw new ArgumentException("A glossary entry id is required.", nameof(entryId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM TranslationGlossary WHERE EntryId = $entryId;";
        command.Parameters.AddWithValue("$entryId", entryId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static TranslationCacheEntry ReadCacheEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        Enum.Parse<TranslationPreset>(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        SqliteValueConverters.ParseDateTime(reader.GetString(7)),
        SqliteValueConverters.ParseDateTime(reader.GetString(8)),
        reader.GetInt32(9));

    private static TranslationCacheSegment ReadCacheSegment(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        Enum.Parse<TranslationPreset>(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        SqliteValueConverters.ParseDateTime(reader.GetString(9)),
        SqliteValueConverters.ParseDateTime(reader.GetString(10)),
        reader.GetInt32(11));

    private static TranslationHistoryEntry ReadHistoryEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        Enum.Parse<TranslationPreset>(reader.GetString(5)),
        Enum.Parse<TranslationSourceKind>(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        SqliteValueConverters.ParseDateTime(reader.GetString(8)),
        reader.GetInt32(9),
        reader.GetInt32(10),
        SqliteValueConverters.ParseBoolean(reader.GetInt64(11)),
        Enum.Parse<TranslationSampleKind>(reader.GetString(12)));

    private static TranslationGlossaryEntry ReadGlossaryEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        SqliteValueConverters.ParseDateTime(reader.GetString(3)),
        SqliteValueConverters.ParseDateTime(reader.GetString(4)));

    private static void AddCacheParameters(SqliteCommand command, TranslationCacheEntry entry)
    {
        command.Parameters.AddWithValue("$cacheKey", entry.CacheKey);
        command.Parameters.AddWithValue("$sourceText", entry.SourceText);
        command.Parameters.AddWithValue("$translatedText", entry.TranslatedText);
        command.Parameters.AddWithValue("$targetLanguage", entry.TargetLanguage);
        command.Parameters.AddWithValue("$preset", entry.Preset.ToString());
        command.Parameters.AddWithValue("$customInstruction", (object?)entry.CustomInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$glossaryInstruction", (object?)entry.GlossaryInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteValueConverters.FormatDateTime(entry.CreatedAt));
        command.Parameters.AddWithValue("$lastUsedAt", SqliteValueConverters.FormatDateTime(entry.LastUsedAt));
        command.Parameters.AddWithValue("$useCount", entry.UseCount);
    }

    private static void AddCacheSegmentParameters(SqliteCommand command, TranslationCacheSegment segment)
    {
        command.Parameters.AddWithValue("$cacheKey", segment.CacheKey);
        command.Parameters.AddWithValue("$segmentIndex", segment.SegmentIndex);
        command.Parameters.AddWithValue("$sourceText", segment.SourceText);
        command.Parameters.AddWithValue("$normalizedSourceText", segment.NormalizedSourceText);
        command.Parameters.AddWithValue("$translatedText", segment.TranslatedText);
        command.Parameters.AddWithValue("$targetLanguage", segment.TargetLanguage);
        command.Parameters.AddWithValue("$preset", segment.Preset.ToString());
        command.Parameters.AddWithValue("$customInstruction", (object?)segment.CustomInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$glossaryInstruction", (object?)segment.GlossaryInstruction ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", SqliteValueConverters.FormatDateTime(segment.CreatedAt));
        command.Parameters.AddWithValue("$lastUsedAt", SqliteValueConverters.FormatDateTime(segment.LastUsedAt));
        command.Parameters.AddWithValue("$useCount", segment.UseCount);
    }

    private static void AddHistoryCacheMatchParameters(SqliteCommand command, TranslationHistoryEntry entry)
    {
        command.Parameters.AddWithValue("$sourceText", entry.SourceText);
        command.Parameters.AddWithValue("$translatedText", entry.TranslatedText);
        command.Parameters.AddWithValue("$targetLanguage", entry.TargetLanguage);
        command.Parameters.AddWithValue("$preset", entry.Preset.ToString());
    }

    private static void ValidateCacheEntry(TranslationCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.CacheKey) ||
            string.IsNullOrWhiteSpace(entry.SourceText) ||
            string.IsNullOrWhiteSpace(entry.TranslatedText) ||
            string.IsNullOrWhiteSpace(entry.TargetLanguage) ||
            !Enum.IsDefined(entry.Preset) ||
            entry.UseCount < 0)
        {
            throw new ArgumentException("The translation cache entry is invalid.", nameof(entry));
        }
    }

    private static void ValidateCacheSegment(TranslationCacheSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (string.IsNullOrWhiteSpace(segment.CacheKey) ||
            segment.SegmentIndex < 0 ||
            string.IsNullOrWhiteSpace(segment.SourceText) ||
            string.IsNullOrWhiteSpace(segment.NormalizedSourceText) ||
            string.IsNullOrWhiteSpace(segment.TranslatedText) ||
            string.IsNullOrWhiteSpace(segment.TargetLanguage) ||
            !Enum.IsDefined(segment.Preset) ||
            segment.UseCount < 0)
        {
            throw new ArgumentException("The translation cache segment is invalid.", nameof(segment));
        }
    }

    private static void ValidateHistoryEntry(TranslationHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.HistoryId == Guid.Empty ||
            string.IsNullOrWhiteSpace(entry.SourceText) ||
            string.IsNullOrWhiteSpace(entry.TranslatedText) ||
            string.IsNullOrWhiteSpace(entry.TargetLanguage) ||
            !Enum.IsDefined(entry.Preset) ||
            !Enum.IsDefined(entry.SourceKind) ||
            !Enum.IsDefined(entry.SampleKind) ||
            entry.CharacterCount < 0 ||
            entry.SegmentCount < 1)
        {
            throw new ArgumentException("The translation history entry is invalid.", nameof(entry));
        }
    }

    private static string ValidateTerm(string value, string parameterName)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 120)
        {
            throw new ArgumentException("A glossary term must be between 1 and 120 characters.", parameterName);
        }

        return value;
    }
}
