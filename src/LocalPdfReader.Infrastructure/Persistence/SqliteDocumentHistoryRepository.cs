using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;
using Microsoft.Data.Sqlite;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class SqliteDocumentHistoryRepository(SqliteLocalDatabase database)
    : IDocumentHistoryRepository
{
    public async Task<DocumentRecord?> FindByFingerprintAsync(
        DocumentFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        ValidateFingerprint(fingerprint);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DocumentId, FastFingerprint, LastKnownPath, FileName, FileSize,
                   LastWriteTimeUtc, FirstOpenedAt, LastOpenedAt, IsMissing
            FROM Documents
            WHERE FastFingerprint = $fingerprint;
            """;
        command.Parameters.AddWithValue("$fingerprint", fingerprint.FastFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    public async Task<DocumentRecord> UpsertAsync(
        DocumentRecord document,
        CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var upsertDocument = connection.CreateCommand())
        {
            upsertDocument.Transaction = transaction;
            upsertDocument.CommandText = """
                INSERT INTO Documents
                    (DocumentId, FastFingerprint, LastKnownPath, FileName, FileSize,
                     LastWriteTimeUtc, FirstOpenedAt, LastOpenedAt, IsMissing)
                VALUES
                    ($documentId, $fingerprint, $path, $fileName, $fileSize,
                     $lastWriteTime, $firstOpenedAt, $lastOpenedAt, $isMissing)
                ON CONFLICT(FastFingerprint) DO UPDATE SET
                    LastKnownPath = excluded.LastKnownPath,
                    FileName = excluded.FileName,
                    FileSize = excluded.FileSize,
                    LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                    LastOpenedAt = excluded.LastOpenedAt,
                    IsMissing = excluded.IsMissing;
                """;
            AddDocumentParameters(upsertDocument, document);
            await upsertDocument.ExecuteNonQueryAsync(cancellationToken);
        }

        var persisted = await FindWithinTransactionAsync(
            connection,
            transaction,
            document.Fingerprint.FastFingerprint,
            cancellationToken) ?? throw new InvalidOperationException("The document record was not persisted.");

        await transaction.CommitAsync(cancellationToken);
        return persisted;
    }

    public async Task RecordRecentAsync(
        Guid documentId,
        DateTimeOffset openedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RecentDocuments (DocumentId, LastOpenedAt, IsPinned, OpenCount)
            VALUES ($documentId, $lastOpenedAt, 0, 1)
            ON CONFLICT(DocumentId) DO UPDATE SET
                LastOpenedAt = excluded.LastOpenedAt,
                OpenCount = RecentDocuments.OpenCount + 1;
            """;
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        command.Parameters.AddWithValue("$lastOpenedAt", SqliteValueConverters.FormatDateTime(openedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecentDocument>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The recent-document limit must be between 1 and 1000.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.DocumentId, d.FastFingerprint, d.LastKnownPath, d.FileName, d.FileSize,
                   d.LastWriteTimeUtc, d.FirstOpenedAt, d.LastOpenedAt, d.IsMissing,
                   r.LastOpenedAt, r.IsPinned, r.OpenCount, s.PageIndex
            FROM RecentDocuments r
            INNER JOIN Documents d ON d.DocumentId = r.DocumentId
            LEFT JOIN ReadingStates s ON s.DocumentId = r.DocumentId
            ORDER BY r.IsPinned DESC, r.LastOpenedAt DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<RecentDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RecentDocument(
                ReadDocument(reader),
                SqliteValueConverters.ParseDateTime(reader.GetString(9)),
                SqliteValueConverters.ParseBoolean(reader.GetInt64(10)),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }

        return results;
    }

    public async Task RemoveFromRecentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await ExecuteDeleteAsync(
            "DELETE FROM RecentDocuments WHERE DocumentId = $documentId;",
            documentId,
            cancellationToken);
    }

    public async Task ClearRecentAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM RecentDocuments;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PruneRecentAsync(
        int maximumUnpinnedCount,
        CancellationToken cancellationToken)
    {
        if (maximumUnpinnedCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUnpinnedCount));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM RecentDocuments
            WHERE IsPinned = 0
              AND DocumentId IN (
                  SELECT DocumentId
                  FROM RecentDocuments
                  WHERE IsPinned = 0
                  ORDER BY LastOpenedAt DESC
                  LIMIT -1 OFFSET $maximumCount
              );
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumUnpinnedCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetMissingAsync(
        Guid documentId,
        bool isMissing,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Documents SET IsMissing = $isMissing WHERE DocumentId = $documentId;";
        command.Parameters.AddWithValue("$isMissing", SqliteValueConverters.FormatBoolean(isMissing));
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteDeleteAsync(
        string commandText,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<DocumentRecord?> FindWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DocumentId, FastFingerprint, LastKnownPath, FileName, FileSize,
                   LastWriteTimeUtc, FirstOpenedAt, LastOpenedAt, IsMissing
            FROM Documents
            WHERE FastFingerprint = $fingerprint;
            """;
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocument(reader) : null;
    }

    private static DocumentRecord ReadDocument(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        new DocumentFingerprint(
            reader.GetString(1),
            reader.GetInt64(4),
            SqliteValueConverters.ParseDateTime(reader.GetString(5))),
        reader.GetString(2),
        reader.GetString(3),
        SqliteValueConverters.ParseDateTime(reader.GetString(6)),
        SqliteValueConverters.ParseDateTime(reader.GetString(7)),
        SqliteValueConverters.ParseBoolean(reader.GetInt64(8)));

    private static void AddDocumentParameters(SqliteCommand command, DocumentRecord document)
    {
        command.Parameters.AddWithValue("$documentId", document.DocumentId.ToString("D"));
        command.Parameters.AddWithValue("$fingerprint", document.Fingerprint.FastFingerprint);
        command.Parameters.AddWithValue("$path", document.LastKnownPath);
        command.Parameters.AddWithValue("$fileName", document.FileName);
        command.Parameters.AddWithValue("$fileSize", document.Fingerprint.FileSize);
        command.Parameters.AddWithValue(
            "$lastWriteTime",
            SqliteValueConverters.FormatDateTime(document.Fingerprint.LastWriteTimeUtc));
        command.Parameters.AddWithValue(
            "$firstOpenedAt",
            SqliteValueConverters.FormatDateTime(document.FirstOpenedAt));
        command.Parameters.AddWithValue(
            "$lastOpenedAt",
            SqliteValueConverters.FormatDateTime(document.LastOpenedAt));
        command.Parameters.AddWithValue("$isMissing", SqliteValueConverters.FormatBoolean(document.IsMissing));
    }

    private static void ValidateDocument(DocumentRecord document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateFingerprint(document.Fingerprint);
        if (document.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(document.LastKnownPath) ||
            string.IsNullOrWhiteSpace(document.FileName))
        {
            throw new ArgumentException("Document identity, path, and file name are required.", nameof(document));
        }
    }

    private static void ValidateFingerprint(DocumentFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (string.IsNullOrWhiteSpace(fingerprint.FastFingerprint) || fingerprint.FileSize < 0)
        {
            throw new ArgumentException("A valid document fingerprint is required.", nameof(fingerprint));
        }
    }
}
