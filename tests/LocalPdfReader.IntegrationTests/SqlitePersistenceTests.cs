using System.IO;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;
using LocalPdfReader.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public sealed class SqlitePersistenceTests
{
    [Fact]
    public async Task InitializationCreatesSchemaOnceAndCanRunRepeatedly()
    {
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "data", "reader.db");
        var database = CreateDatabase(databasePath);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            await database.InitializeAsync(CancellationToken.None);

            Assert.True(database.IsAvailable);
            Assert.True(File.Exists(databasePath));
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT SchemaVersion FROM SchemaInfo WHERE Id = 1;
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('Documents', 'RecentDocuments', 'ReadingStates', 'Annotations', 'DocumentSessions', 'DocumentSessionTabs', 'TranslationCacheSegments');
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7, reader.GetInt32(0));
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task RepositoriesRoundTripDocumentsReadingStateAndAnnotations()
    {
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "reader.db");
        var database = CreateDatabase(databasePath);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var documentRepository = new SqliteDocumentHistoryRepository(database);
            var readingStateRepository = new SqliteReadingStateRepository(database);
            var annotationRepository = new SqliteAnnotationRepository(database);
            var openedAt = new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero);
            var fingerprint = new DocumentFingerprint(
                "sha256-fast-fingerprint",
                123456,
                new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
            var originalDocumentId = Guid.NewGuid();
            var document = new DocumentRecord(
                originalDocumentId,
                fingerprint,
                @"C:\papers\original.pdf",
                "original.pdf",
                openedAt,
                openedAt,
                false);

            var firstPersisted = await documentRepository.UpsertAsync(document, CancellationToken.None);
            Assert.Empty(await documentRepository.GetRecentAsync(20, CancellationToken.None));
            await documentRepository.RecordRecentAsync(
                firstPersisted.DocumentId,
                firstPersisted.LastOpenedAt,
                CancellationToken.None);
            var movedDocument = document with
            {
                DocumentId = Guid.NewGuid(),
                LastKnownPath = @"D:\moved\paper.pdf",
                FileName = "paper.pdf",
                LastOpenedAt = openedAt.AddHours(1)
            };
            var secondPersisted = await documentRepository.UpsertAsync(movedDocument, CancellationToken.None);
            await documentRepository.RecordRecentAsync(
                secondPersisted.DocumentId,
                secondPersisted.LastOpenedAt,
                CancellationToken.None);

            Assert.Equal(originalDocumentId, firstPersisted.DocumentId);
            Assert.Equal(originalDocumentId, secondPersisted.DocumentId);
            Assert.Equal(@"D:\moved\paper.pdf", secondPersisted.LastKnownPath);
            var foundDocument = await documentRepository.FindByFingerprintAsync(fingerprint, CancellationToken.None);
            Assert.Equal(secondPersisted, foundDocument);
            var recent = Assert.Single(await documentRepository.GetRecentAsync(20, CancellationToken.None));
            Assert.Equal(2, recent.OpenCount);
            Assert.Equal(secondPersisted.LastOpenedAt, recent.LastOpenedAt);

            var readingState = new ReadingState(
                originalDocumentId,
                8,
                1.5,
                "ActualZoom",
                PageRotation.Rotate90,
                12.5,
                125.25,
                true,
                "Search",
                false,
                openedAt.AddHours(2));
            await readingStateRepository.SaveAsync(readingState, CancellationToken.None);
            Assert.Equal(readingState, await readingStateRepository.GetAsync(originalDocumentId, CancellationToken.None));

            var sessionRepository = new SqliteDocumentSessionRepository(database);
            var session = new DocumentSessionSnapshot(
                [new DocumentSessionTab(originalDocumentId, secondPersisted.LastKnownPath, IsMissing: false)],
                ActiveTabIndex: 0,
                openedAt.AddHours(2));
            await sessionRepository.SaveAsync(session, CancellationToken.None);
            var restoredSession = await sessionRepository.GetAsync(CancellationToken.None);
            Assert.NotNull(restoredSession);
            Assert.Equal(session.ActiveTabIndex, restoredSession.ActiveTabIndex);
            Assert.Equal(session.UpdatedAt, restoredSession.UpdatedAt);
            Assert.Equal(session.Tabs, restoredSession.Tabs);
            await sessionRepository.ClearAsync(CancellationToken.None);
            Assert.Null(await sessionRepository.GetAsync(CancellationToken.None));

            var annotation = new TextHighlightAnnotation(
                Guid.NewGuid(),
                fingerprint,
                8,
                20,
                12,
                "important text",
                AnnotationColor.Yellow,
                [new PdfRect(10, 20, 100, 40), new PdfRect(10, 45, 80, 65)],
                "first note",
                openedAt.AddHours(3),
                openedAt.AddHours(3));
            await annotationRepository.AddAsync(annotation, CancellationToken.None);
            AssertAnnotationEqual(annotation, Assert.Single(
                await annotationRepository.GetByDocumentAsync(originalDocumentId, CancellationToken.None)));

            var updatedAnnotation = annotation with
            {
                Color = AnnotationColor.Blue,
                Note = "updated note",
                ModifiedAt = openedAt.AddHours(4)
            };
            await annotationRepository.UpdateAsync(updatedAnnotation, CancellationToken.None);
            AssertAnnotationEqual(updatedAnnotation, Assert.Single(
                await annotationRepository.GetByDocumentAsync(originalDocumentId, CancellationToken.None)));

            await annotationRepository.DeleteAsync(annotation.AnnotationId, CancellationToken.None);
            Assert.Empty(await annotationRepository.GetByDocumentAsync(originalDocumentId, CancellationToken.None));
            await readingStateRepository.DeleteAsync(originalDocumentId, CancellationToken.None);
            Assert.Null(await readingStateRepository.GetAsync(originalDocumentId, CancellationToken.None));
            await documentRepository.RemoveFromRecentAsync(originalDocumentId, CancellationToken.None);
            Assert.Empty(await documentRepository.GetRecentAsync(20, CancellationToken.None));
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task RecentDocumentsArePrunedToCapacityAndIncludeLastPage()
    {
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "reader.db");
        var database = CreateDatabase(databasePath);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var documentRepository = new SqliteDocumentHistoryRepository(database);
            var readingStateRepository = new SqliteReadingStateRepository(database);
            var openedAt = new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);
            var persistedDocuments = new List<DocumentRecord>();

            for (var index = 0; index < 3; index++)
            {
                var document = new DocumentRecord(
                    Guid.NewGuid(),
                    new DocumentFingerprint(
                        $"fingerprint-{index}",
                        1000 + index,
                        openedAt.AddDays(-1)),
                    $@"C:\papers\paper-{index}.pdf",
                    $"paper-{index}.pdf",
                    openedAt.AddMinutes(index),
                    openedAt.AddMinutes(index),
                    false);
                var persisted = await documentRepository.UpsertAsync(document, CancellationToken.None);
                await documentRepository.RecordRecentAsync(
                    persisted.DocumentId,
                    persisted.LastOpenedAt,
                    CancellationToken.None);
                persistedDocuments.Add(persisted);
            }

            var newest = persistedDocuments[2];
            await readingStateRepository.SaveAsync(
                new ReadingState(
                    newest.DocumentId,
                    14,
                    1.5,
                    "ActualZoom",
                    PageRotation.Rotate0,
                    0,
                    100,
                    false,
                    "Search",
                    true,
                    openedAt.AddHours(1)),
                CancellationToken.None);

            await documentRepository.PruneRecentAsync(2, CancellationToken.None);

            var recent = await documentRepository.GetRecentAsync(20, CancellationToken.None);
            Assert.Equal(2, recent.Count);
            Assert.Equal(newest.DocumentId, recent[0].Document.DocumentId);
            Assert.Equal(14, recent[0].LastPageIndex);
            Assert.DoesNotContain(recent, item => item.Document.DocumentId == persistedDocuments[0].DocumentId);
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptDatabaseIsPreservedBeforeAReplacementIsCreated()
    {
        const string corruptContents = "this is not a sqlite database";
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "reader.db");
        await File.WriteAllTextAsync(databasePath, corruptContents);
        var database = CreateDatabase(databasePath);

        try
        {
            await database.InitializeAsync(CancellationToken.None);

            Assert.True(database.IsAvailable);
            var backupPath = Assert.Single(Directory.GetFiles(directoryPath, "reader.db.corrupt-*.backup"));
            Assert.Equal(corruptContents, await File.ReadAllTextAsync(backupPath));
            Assert.True(new FileInfo(databasePath).Length > corruptContents.Length);
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task TranslationMemoryRepositoryPersistsCacheHistoryAndGlossary()
    {
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "reader.db");
        var database = CreateDatabase(databasePath);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var repository = new SqliteTranslationMemoryRepository(database);
            var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
            var cache = new TranslationCacheEntry(
                "cache-key",
                "source",
                "translated",
                "zh-CN",
                TranslationPreset.Academic,
                null,
                "term => 术语",
                now,
                now,
                1);

            await repository.SaveCacheAsync(cache, CancellationToken.None);
            var loadedCache = await repository.FindCacheAsync("cache-key", CancellationToken.None);
            Assert.NotNull(loadedCache);
            Assert.Equal("translated", loadedCache.TranslatedText);

            var cacheSegments = new[]
            {
                new TranslationCacheSegment(
                    "cache-key",
                    0,
                    "First sentence.",
                    "first sentence",
                    "第一句。",
                    "zh-CN",
                    TranslationPreset.Academic,
                    null,
                    "term => 术语",
                    now,
                    now,
                    1),
                new TranslationCacheSegment(
                    "cache-key",
                    1,
                    "Second sentence.",
                    "second sentence",
                    "第二句。",
                    "zh-CN",
                    TranslationPreset.Academic,
                    null,
                    "term => 术语",
                    now,
                    now,
                    1)
            };
            await repository.SaveCacheSegmentsAsync(cacheSegments, CancellationToken.None);
            var loadedSegments = await repository.GetReusableCacheSegmentsAsync(
                "zh-CN",
                TranslationPreset.Academic,
                null,
                "term => 术语",
                10,
                CancellationToken.None);
            Assert.Equal(2, loadedSegments.Count);
            Assert.Equal("第一句。", loadedSegments[0].TranslatedText);

            var history = new TranslationHistoryEntry(
                Guid.NewGuid(),
                "source",
                "source",
                "translated",
                "zh-CN",
                TranslationPreset.Academic,
                TranslationSourceKind.CurrentPage,
                "第 1 页",
                now,
                6,
                1,
                false,
                TranslationSampleKind.None);
            await repository.AddHistoryAsync(history, CancellationToken.None);
            var recent = Assert.Single(await repository.GetRecentHistoryAsync(10, CancellationToken.None));
            Assert.Equal(history.HistoryId, recent.HistoryId);
            await repository.UpdateHistorySampleKindAsync(
                history.HistoryId,
                TranslationSampleKind.Preferred,
                CancellationToken.None);
            recent = Assert.Single(await repository.GetRecentHistoryAsync(10, CancellationToken.None));
            Assert.Equal(TranslationSampleKind.Preferred, recent.SampleKind);

            await repository.DeleteHistoryAndRelatedCacheAsync(recent, CancellationToken.None);
            Assert.Empty(await repository.GetRecentHistoryAsync(10, CancellationToken.None));
            Assert.Null(await repository.FindCacheAsync("cache-key", CancellationToken.None));
            Assert.Empty(await repository.GetReusableCacheSegmentsAsync(
                "zh-CN",
                TranslationPreset.Academic,
                null,
                "term => 术语",
                10,
                CancellationToken.None));

            var glossary = await repository.UpsertGlossaryEntryAsync(
                "Transformer",
                "Transformer 模型",
                CancellationToken.None);
            Assert.Equal(
                glossary.EntryId,
                Assert.Single(await repository.GetGlossaryAsync(CancellationToken.None)).EntryId);
            await repository.DeleteGlossaryEntryAsync(glossary.EntryId, CancellationToken.None);
            Assert.Empty(await repository.GetGlossaryAsync(CancellationToken.None));
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingSchemaReopensWithoutDataLossAndUpdatesApplicationVersion()
    {
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "reader.db");
        var originalDatabase = new SqliteLocalDatabase(
            databasePath,
            "0.4.0",
            NullLogger<SqliteLocalDatabase>.Instance);
        var document = new DocumentRecord(
            Guid.NewGuid(),
            new DocumentFingerprint("migration-fingerprint", 42, DateTimeOffset.UtcNow.AddDays(-1)),
            @"C:\papers\migration.pdf",
            "migration.pdf",
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            false);

        try
        {
            await originalDatabase.InitializeAsync(CancellationToken.None);
            await new SqliteDocumentHistoryRepository(originalDatabase).UpsertAsync(
                document,
                CancellationToken.None);
            await originalDatabase.DisposeAsync();

            await using (var downgradeConnection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await downgradeConnection.OpenAsync();
                await using var downgradeCommand = downgradeConnection.CreateCommand();
                downgradeCommand.CommandText = """
                    DROP TABLE DocumentSessionTabs;
                    DROP TABLE DocumentSessions;
                    UPDATE SchemaInfo SET SchemaVersion = 1;
                    """;
                await downgradeCommand.ExecuteNonQueryAsync();
            }

            await using var upgradedDatabase = new SqliteLocalDatabase(
                databasePath,
                "1.0.0",
                NullLogger<SqliteLocalDatabase>.Instance);
            await upgradedDatabase.InitializeAsync(CancellationToken.None);

            var loaded = await new SqliteDocumentHistoryRepository(upgradedDatabase)
                .FindByFingerprintAsync(document.Fingerprint, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(document.DocumentId, loaded.DocumentId);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SchemaVersion, ApplicationVersion FROM SchemaInfo WHERE Id = 1;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0));
            Assert.Equal("1.0.0", reader.GetString(1));
        }
        finally
        {
            await originalDatabase.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task V05DatabaseUpgradesToV10WithoutLosingReadingStateOrLocalAnnotations()
    {
        var directoryPath = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directoryPath, "reader.db");
        var v05Database = new SqliteLocalDatabase(
            databasePath,
            "0.5.0",
            NullLogger<SqliteLocalDatabase>.Instance);
        var openedAt = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var fingerprint = new DocumentFingerprint("v05-upgrade", 65536, openedAt.AddDays(-1));
        var document = new DocumentRecord(
            Guid.NewGuid(),
            fingerprint,
            @"C:\papers\v05.pdf",
            "v05.pdf",
            openedAt,
            openedAt,
            false);
        var annotation = new TextHighlightAnnotation(
            Guid.NewGuid(),
            fingerprint,
            3,
            42,
            8,
            "v0.5 text",
            AnnotationColor.Green,
            [new PdfRect(10, 20, 90, 35)],
            "v0.5 note",
            openedAt.AddMinutes(1),
            openedAt.AddMinutes(1));
        var readingState = new ReadingState(
            document.DocumentId,
            3,
            1.25,
            "ActualZoom",
            PageRotation.Rotate90,
            12,
            80,
            true,
            "Annotations",
            true,
            openedAt.AddMinutes(2));

        try
        {
            await v05Database.InitializeAsync(CancellationToken.None);
            var documentRepository = new SqliteDocumentHistoryRepository(v05Database);
            var persisted = await documentRepository.UpsertAsync(document, CancellationToken.None);
            await documentRepository.RecordRecentAsync(persisted.DocumentId, openedAt, CancellationToken.None);
            await new SqliteReadingStateRepository(v05Database).SaveAsync(readingState, CancellationToken.None);
            await new SqliteAnnotationRepository(v05Database).AddAsync(annotation, CancellationToken.None);
            await v05Database.DisposeAsync();

            await using (var downgradeConnection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await downgradeConnection.OpenAsync();
                await using var downgradeCommand = downgradeConnection.CreateCommand();
                downgradeCommand.CommandText = """
                    DROP TABLE IF EXISTS TranslationCacheSegments;
                    DROP TABLE IF EXISTS TranslationGlossary;
                    DROP TABLE IF EXISTS TranslationHistory;
                    DROP TABLE IF EXISTS TranslationCache;
                    UPDATE SchemaInfo SET SchemaVersion = 2, ApplicationVersion = '0.5.0';
                    """;
                await downgradeCommand.ExecuteNonQueryAsync();
            }

            await using var upgradedDatabase = new SqliteLocalDatabase(
                databasePath,
                "1.0.0",
                NullLogger<SqliteLocalDatabase>.Instance);
            await upgradedDatabase.InitializeAsync(CancellationToken.None);

            var upgradedDocument = await new SqliteDocumentHistoryRepository(upgradedDatabase)
                .FindByFingerprintAsync(fingerprint, CancellationToken.None);
            Assert.NotNull(upgradedDocument);
            Assert.Equal(document.DocumentId, upgradedDocument.DocumentId);
            Assert.Equal(
                readingState,
                await new SqliteReadingStateRepository(upgradedDatabase)
                    .GetAsync(document.DocumentId, CancellationToken.None));
            AssertAnnotationEqual(
                annotation,
                Assert.Single(await new SqliteAnnotationRepository(upgradedDatabase)
                    .GetByDocumentAsync(document.DocumentId, CancellationToken.None)));

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT SchemaVersion, ApplicationVersion FROM SchemaInfo WHERE Id = 1;
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('TranslationCache', 'TranslationHistory', 'TranslationGlossary', 'TranslationCacheSegments');
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(5, reader.GetInt32(0));
            Assert.Equal("1.0.0", reader.GetString(1));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(4, reader.GetInt32(0));
        }
        finally
        {
            await v05Database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task UnavailableDatabaseDoesNotPretendRepositoriesCanPersistData()
    {
        var directoryPath = CreateTemporaryDirectory();
        var pathBlockingDirectory = Path.Combine(directoryPath, "not-a-directory");
        await File.WriteAllTextAsync(pathBlockingDirectory, "blocking file");
        var databasePath = Path.Combine(pathBlockingDirectory, "reader.db");
        var database = CreateDatabase(databasePath);
        var repository = new SqliteDocumentHistoryRepository(database);

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                database.InitializeAsync(CancellationToken.None));
            Assert.False(database.IsAvailable);
            await Assert.ThrowsAsync<LocalDataStoreUnavailableException>(() =>
                repository.GetRecentAsync(20, CancellationToken.None));
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static SqliteLocalDatabase CreateDatabase(string databasePath) => new(
        databasePath,
        "1.0.0",
        NullLogger<SqliteLocalDatabase>.Instance);

    private static void AssertAnnotationEqual(
        TextHighlightAnnotation expected,
        TextHighlightAnnotation actual)
    {
        Assert.Equal(expected.AnnotationId, actual.AnnotationId);
        Assert.Equal(expected.DocumentFingerprint, actual.DocumentFingerprint);
        Assert.Equal(expected.PageIndex, actual.PageIndex);
        Assert.Equal(expected.CharacterStart, actual.CharacterStart);
        Assert.Equal(expected.CharacterCount, actual.CharacterCount);
        Assert.Equal(expected.SelectedTextPreview, actual.SelectedTextPreview);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Rectangles, actual.Rectangles);
        Assert.Equal(expected.Note, actual.Note);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.ModifiedAt, actual.ModifiedAt);
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Sqlite.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }
}
