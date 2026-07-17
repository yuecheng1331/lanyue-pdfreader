using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;
using Microsoft.Data.Sqlite;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class SqliteDocumentSessionRepository(SqliteLocalDatabase database) : IDocumentSessionRepository
{
    public async Task SaveAsync(DocumentSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, "DELETE FROM DocumentSessionTabs;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM DocumentSessions;", cancellationToken);

        if (snapshot.Tabs.Count > 0)
        {
            await using (var sessionCommand = connection.CreateCommand())
            {
                sessionCommand.Transaction = transaction;
                sessionCommand.CommandText = """
                    INSERT INTO DocumentSessions (Id, ActiveTabIndex, UpdatedAt)
                    VALUES (1, $activeTabIndex, $updatedAt);
                    """;
                sessionCommand.Parameters.AddWithValue("$activeTabIndex", snapshot.ActiveTabIndex);
                sessionCommand.Parameters.AddWithValue("$updatedAt", SqliteValueConverters.FormatDateTime(snapshot.UpdatedAt));
                await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var position = 0; position < snapshot.Tabs.Count; position++)
            {
                await using var tabCommand = connection.CreateCommand();
                tabCommand.Transaction = transaction;
                tabCommand.CommandText = """
                    INSERT INTO DocumentSessionTabs (Position, DocumentId)
                    VALUES ($position, $documentId);
                    """;
                tabCommand.Parameters.AddWithValue("$position", position);
                tabCommand.Parameters.AddWithValue("$documentId", snapshot.Tabs[position].DocumentId.ToString("D"));
                await tabCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DocumentSessionSnapshot?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var sessionCommand = connection.CreateCommand();
        sessionCommand.CommandText = """
            SELECT ActiveTabIndex, UpdatedAt
            FROM DocumentSessions
            WHERE Id = 1;
            """;
        await using var sessionReader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await sessionReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var activeTabIndex = sessionReader.GetInt32(0);
        var updatedAt = SqliteValueConverters.ParseDateTime(sessionReader.GetString(1));

        await using var tabCommand = connection.CreateCommand();
        tabCommand.CommandText = """
            SELECT t.DocumentId, d.LastKnownPath, d.IsMissing
            FROM DocumentSessionTabs t
            INNER JOIN Documents d ON d.DocumentId = t.DocumentId
            ORDER BY t.Position;
            """;
        var tabs = new List<DocumentSessionTab>();
        await using var tabReader = await tabCommand.ExecuteReaderAsync(cancellationToken);
        while (await tabReader.ReadAsync(cancellationToken))
        {
            tabs.Add(new DocumentSessionTab(
                Guid.Parse(tabReader.GetString(0)),
                tabReader.GetString(1),
                SqliteValueConverters.ParseBoolean(tabReader.GetInt64(2))));
        }

        return tabs.Count == 0
            ? null
            : new DocumentSessionSnapshot(tabs, Math.Clamp(activeTabIndex, 0, tabs.Count - 1), updatedAt);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM DocumentSessionTabs;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM DocumentSessions;", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    private static void ValidateSnapshot(DocumentSessionSnapshot snapshot)
    {
        if (snapshot.Tabs.Count == 0)
        {
            return;
        }

        if (snapshot.ActiveTabIndex is < 0 or >= 100 || snapshot.ActiveTabIndex >= snapshot.Tabs.Count ||
            snapshot.Tabs.Count > 100 || snapshot.Tabs.Any(tab => tab.DocumentId == Guid.Empty))
        {
            throw new ArgumentException("The document-session snapshot is invalid.", nameof(snapshot));
        }
    }
}
