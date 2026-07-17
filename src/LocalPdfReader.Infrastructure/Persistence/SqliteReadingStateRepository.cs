using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;
using Microsoft.Data.Sqlite;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class SqliteReadingStateRepository(SqliteLocalDatabase database) : IReadingStateRepository
{
    public async Task<ReadingState?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DocumentId, PageIndex, ZoomFactor, ViewMode, Rotation,
                   HorizontalOffset, VerticalOffset, LeftSidebarVisible,
                   LeftSidebarMode, TranslationPanelVisible, UpdatedAt
            FROM ReadingStates
            WHERE DocumentId = $documentId;
            """;
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReadingState(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetDouble(2),
            reader.GetString(3),
            (PageRotation)reader.GetInt32(4),
            reader.GetDouble(5),
            reader.GetDouble(6),
            SqliteValueConverters.ParseBoolean(reader.GetInt64(7)),
            reader.GetString(8),
            SqliteValueConverters.ParseBoolean(reader.GetInt64(9)),
            SqliteValueConverters.ParseDateTime(reader.GetString(10)));
    }

    public async Task SaveAsync(ReadingState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.DocumentId == Guid.Empty || state.PageIndex < 0 || !double.IsFinite(state.ZoomFactor) ||
            state.ZoomFactor <= 0 || !Enum.IsDefined(state.Rotation) ||
            !double.IsFinite(state.HorizontalOffset) || !double.IsFinite(state.VerticalOffset) ||
            string.IsNullOrWhiteSpace(state.ViewMode) || string.IsNullOrWhiteSpace(state.LeftSidebarMode))
        {
            throw new ArgumentException("The reading state contains invalid values.", nameof(state));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ReadingStates
                (DocumentId, PageIndex, ZoomFactor, ViewMode, Rotation,
                 HorizontalOffset, VerticalOffset, LeftSidebarVisible,
                 LeftSidebarMode, TranslationPanelVisible, UpdatedAt)
            VALUES
                ($documentId, $pageIndex, $zoomFactor, $viewMode, $rotation,
                 $horizontalOffset, $verticalOffset, $leftSidebarVisible,
                 $leftSidebarMode, $translationPanelVisible, $updatedAt)
            ON CONFLICT(DocumentId) DO UPDATE SET
                PageIndex = excluded.PageIndex,
                ZoomFactor = excluded.ZoomFactor,
                ViewMode = excluded.ViewMode,
                Rotation = excluded.Rotation,
                HorizontalOffset = excluded.HorizontalOffset,
                VerticalOffset = excluded.VerticalOffset,
                LeftSidebarVisible = excluded.LeftSidebarVisible,
                LeftSidebarMode = excluded.LeftSidebarMode,
                TranslationPanelVisible = excluded.TranslationPanelVisible,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$documentId", state.DocumentId.ToString("D"));
        command.Parameters.AddWithValue("$pageIndex", state.PageIndex);
        command.Parameters.AddWithValue("$zoomFactor", state.ZoomFactor);
        command.Parameters.AddWithValue("$viewMode", state.ViewMode);
        command.Parameters.AddWithValue("$rotation", (int)state.Rotation);
        command.Parameters.AddWithValue("$horizontalOffset", state.HorizontalOffset);
        command.Parameters.AddWithValue("$verticalOffset", state.VerticalOffset);
        command.Parameters.AddWithValue(
            "$leftSidebarVisible",
            SqliteValueConverters.FormatBoolean(state.LeftSidebarVisible));
        command.Parameters.AddWithValue("$leftSidebarMode", state.LeftSidebarMode);
        command.Parameters.AddWithValue(
            "$translationPanelVisible",
            SqliteValueConverters.FormatBoolean(state.TranslationPanelVisible));
        command.Parameters.AddWithValue("$updatedAt", SqliteValueConverters.FormatDateTime(state.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ReadingStates WHERE DocumentId = $documentId;";
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
