using LocalPdfReader.Application.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class SqliteLocalDatabase(
    string databasePath,
    string applicationVersion,
    ILogger<SqliteLocalDatabase> logger) : ILocalDataStore, IAsyncDisposable, IDisposable
{
    internal const int CurrentSchemaVersion = 5;

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _disposed;

    public bool IsAvailable { get; private set; }

    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            IsAvailable = false;
            try
            {
                await InitializeCoreAsync(cancellationToken);
            }
            catch (SqliteException exception) when (IsDatabaseCorruption(exception))
            {
                var backupPath = BackupCorruptDatabase();
                logger.LogWarning(
                    new EventId(3002, "DatabaseRebuiltAfterCorruption"),
                    exception,
                    "Local database was corrupt and was preserved at {BackupPath} before rebuilding.",
                    backupPath);
                await InitializeCoreAsync(cancellationToken);
            }

            IsAvailable = true;
            logger.LogInformation(
                new EventId(3000, "DatabaseInitialized"),
                "Local database schema {SchemaVersion} is ready.",
                CurrentSchemaVersion);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable)
        {
            throw new LocalDataStoreUnavailableException("The local database is not available.");
        }

        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsAvailable = false;
        SqliteConnection.ClearAllPools();
        _initializationLock.Dispose();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The database path must include a directory.");
        Directory.CreateDirectory(directoryPath);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                SchemaVersion INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastMigratedAt TEXT NOT NULL,
                ApplicationVersion TEXT NOT NULL
            );
            """, cancellationToken);

        var now = SqliteValueConverters.FormatDateTime(DateTimeOffset.UtcNow);
        await using (var insertSchema = connection.CreateCommand())
        {
            insertSchema.Transaction = transaction;
            insertSchema.CommandText = """
                INSERT OR IGNORE INTO SchemaInfo
                    (Id, SchemaVersion, CreatedAt, LastMigratedAt, ApplicationVersion)
                VALUES (1, $schemaVersion, $createdAt, $migratedAt, $applicationVersion);
                """;
            insertSchema.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion);
            insertSchema.Parameters.AddWithValue("$createdAt", now);
            insertSchema.Parameters.AddWithValue("$migratedAt", now);
            insertSchema.Parameters.AddWithValue("$applicationVersion", applicationVersion);
            await insertSchema.ExecuteNonQueryAsync(cancellationToken);
        }

        var schemaVersion = await ReadSchemaVersionAsync(connection, transaction, cancellationToken);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Database schema {schemaVersion} is newer than supported schema {CurrentSchemaVersion}.");
        }

        await CreateSchemaVersion1Async(connection, transaction, cancellationToken);
        await CreateSchemaVersion2Async(connection, transaction, cancellationToken);
        await CreateSchemaVersion3Async(connection, transaction, cancellationToken);
        if (schemaVersion == 3)
        {
            await CreateSchemaVersion4Async(connection, transaction, cancellationToken);
        }
        await CreateSchemaVersion5Async(connection, transaction, cancellationToken);

        if (schemaVersion < 1)
        {
            throw new InvalidDataException($"Database schema {schemaVersion} is not supported.");
        }

        if (schemaVersion < CurrentSchemaVersion)
        {
            await using var migrateSchema = connection.CreateCommand();
            migrateSchema.Transaction = transaction;
            migrateSchema.CommandText = """
                UPDATE SchemaInfo
                SET SchemaVersion = $schemaVersion,
                    LastMigratedAt = $migratedAt
                WHERE Id = 1;
                """;
            migrateSchema.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion);
            migrateSchema.Parameters.AddWithValue("$migratedAt", now);
            await migrateSchema.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateApplicationVersion = connection.CreateCommand())
        {
            updateApplicationVersion.Transaction = transaction;
            updateApplicationVersion.CommandText = """
                UPDATE SchemaInfo
                SET ApplicationVersion = $applicationVersion
                WHERE Id = 1;
                """;
            updateApplicationVersion.Parameters.AddWithValue("$applicationVersion", applicationVersion);
            await updateApplicationVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CreateSchemaVersion1Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Documents (
                DocumentId TEXT NOT NULL PRIMARY KEY,
                FastFingerprint TEXT NOT NULL UNIQUE,
                LastKnownPath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                FileSize INTEGER NOT NULL CHECK (FileSize >= 0),
                LastWriteTimeUtc TEXT NOT NULL,
                FirstOpenedAt TEXT NOT NULL,
                LastOpenedAt TEXT NOT NULL,
                IsMissing INTEGER NOT NULL DEFAULT 0 CHECK (IsMissing IN (0, 1))
            );

            CREATE TABLE IF NOT EXISTS RecentDocuments (
                DocumentId TEXT NOT NULL PRIMARY KEY,
                LastOpenedAt TEXT NOT NULL,
                IsPinned INTEGER NOT NULL DEFAULT 0 CHECK (IsPinned IN (0, 1)),
                OpenCount INTEGER NOT NULL DEFAULT 1 CHECK (OpenCount >= 1),
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ReadingStates (
                DocumentId TEXT NOT NULL PRIMARY KEY,
                PageIndex INTEGER NOT NULL CHECK (PageIndex >= 0),
                ZoomFactor REAL NOT NULL CHECK (ZoomFactor > 0),
                ViewMode TEXT NOT NULL,
                Rotation INTEGER NOT NULL CHECK (Rotation IN (0, 90, 180, 270)),
                HorizontalOffset REAL NOT NULL,
                VerticalOffset REAL NOT NULL,
                LeftSidebarVisible INTEGER NOT NULL CHECK (LeftSidebarVisible IN (0, 1)),
                LeftSidebarMode TEXT NOT NULL,
                TranslationPanelVisible INTEGER NOT NULL CHECK (TranslationPanelVisible IN (0, 1)),
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Annotations (
                AnnotationId TEXT NOT NULL PRIMARY KEY,
                DocumentId TEXT NOT NULL,
                PageIndex INTEGER NOT NULL CHECK (PageIndex >= 0),
                CharacterStart INTEGER NOT NULL CHECK (CharacterStart >= 0),
                CharacterCount INTEGER NOT NULL CHECK (CharacterCount > 0),
                SelectedTextPreview TEXT NOT NULL CHECK (length(SelectedTextPreview) <= 300),
                Color INTEGER NOT NULL CHECK (Color BETWEEN 0 AND 3),
                Note TEXT NULL,
                RectanglesJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL,
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_RecentDocuments_LastOpenedAt
                ON RecentDocuments(IsPinned DESC, LastOpenedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Annotations_DocumentId_PageIndex
                ON Annotations(DocumentId, PageIndex);
            """, cancellationToken);
    }

    private static Task CreateSchemaVersion2Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS DocumentSessions (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                ActiveTabIndex INTEGER NOT NULL CHECK (ActiveTabIndex >= 0),
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DocumentSessionTabs (
                Position INTEGER NOT NULL PRIMARY KEY CHECK (Position >= 0),
                DocumentId TEXT NOT NULL,
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId) ON DELETE CASCADE
            );
            """, cancellationToken);

    private static Task CreateSchemaVersion3Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS TranslationCache (
                CacheKey TEXT NOT NULL PRIMARY KEY,
                SourceText TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                Preset TEXT NOT NULL,
                CustomInstruction TEXT NULL,
                GlossaryInstruction TEXT NULL,
                CreatedAt TEXT NOT NULL,
                LastUsedAt TEXT NOT NULL,
                UseCount INTEGER NOT NULL CHECK (UseCount >= 0)
            );

            CREATE TABLE IF NOT EXISTS TranslationHistory (
                HistoryId TEXT NOT NULL PRIMARY KEY,
                SourcePreview TEXT NOT NULL,
                SourceText TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                Preset TEXT NOT NULL,
                SourceKind TEXT NOT NULL,
                SourceScope TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CharacterCount INTEGER NOT NULL CHECK (CharacterCount >= 0),
                SegmentCount INTEGER NOT NULL CHECK (SegmentCount >= 1),
                UsedCache INTEGER NOT NULL CHECK (UsedCache IN (0, 1)),
                SampleKind TEXT NOT NULL DEFAULT 'None'
                    CHECK (SampleKind IN ('None', 'Preferred', 'Rejected'))
            );

            CREATE TABLE IF NOT EXISTS TranslationGlossary (
                EntryId TEXT NOT NULL PRIMARY KEY,
                SourceTerm TEXT NOT NULL UNIQUE,
                TargetTerm TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_TranslationHistory_CreatedAt
                ON TranslationHistory(CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_TranslationGlossary_SourceTerm
                ON TranslationGlossary(SourceTerm);
            """, cancellationToken);

    private static Task CreateSchemaVersion4Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            ALTER TABLE TranslationHistory
                ADD COLUMN SampleKind TEXT NOT NULL DEFAULT 'None'
                    CHECK (SampleKind IN ('None', 'Preferred', 'Rejected'));
            """, cancellationToken);

    private static Task CreateSchemaVersion5Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS TranslationCacheSegments (
                CacheKey TEXT NOT NULL,
                SegmentIndex INTEGER NOT NULL CHECK (SegmentIndex >= 0),
                SourceText TEXT NOT NULL,
                NormalizedSourceText TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                Preset TEXT NOT NULL,
                CustomInstruction TEXT NULL,
                GlossaryInstruction TEXT NULL,
                CreatedAt TEXT NOT NULL,
                LastUsedAt TEXT NOT NULL,
                UseCount INTEGER NOT NULL CHECK (UseCount >= 0),
                PRIMARY KEY (CacheKey, SegmentIndex)
            );

            CREATE INDEX IF NOT EXISTS IX_TranslationCacheSegments_Query
                ON TranslationCacheSegments(TargetLanguage, Preset, LastUsedAt DESC, UseCount DESC);
            CREATE INDEX IF NOT EXISTS IX_TranslationCacheSegments_NormalizedSource
                ON TranslationCacheSegments(NormalizedSourceText);
            """, cancellationToken);

    private SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SchemaVersion FROM SchemaInfo WHERE Id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BackupCorruptDatabase()
    {
        SqliteConnection.ClearAllPools();
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var backupPath = $"{DatabasePath}.corrupt-{timestamp}.backup";
        File.Move(DatabasePath, backupPath);
        MoveSidecarIfPresent("-wal", backupPath + ".wal");
        MoveSidecarIfPresent("-shm", backupPath + ".shm");
        return backupPath;
    }

    private void MoveSidecarIfPresent(string suffix, string backupPath)
    {
        var sourcePath = DatabasePath + suffix;
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, backupPath);
        }
    }

    private static bool IsDatabaseCorruption(SqliteException exception) =>
        exception.SqliteErrorCode is 11 or 26;
}
