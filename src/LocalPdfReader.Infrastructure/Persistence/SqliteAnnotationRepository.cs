using System.Text.Json;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;
using Microsoft.Data.Sqlite;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class SqliteAnnotationRepository(SqliteLocalDatabase database) : IAnnotationRepository
{
    private static readonly JsonSerializerOptions RectangleSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<IReadOnlyList<TextHighlightAnnotation>> GetByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.AnnotationId, d.FastFingerprint, d.FileSize, d.LastWriteTimeUtc,
                   a.PageIndex, a.CharacterStart, a.CharacterCount, a.SelectedTextPreview,
                   a.Color, a.RectanglesJson, a.Note, a.CreatedAt, a.ModifiedAt
            FROM Annotations a
            INNER JOIN Documents d ON d.DocumentId = a.DocumentId
            WHERE a.DocumentId = $documentId
            ORDER BY a.PageIndex, a.CharacterStart;
            """;
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        var results = new List<TextHighlightAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rectangles = JsonSerializer.Deserialize<PdfRect[]>(
                reader.GetString(9),
                RectangleSerializerOptions) ?? [];
            results.Add(new TextHighlightAnnotation(
                Guid.Parse(reader.GetString(0)),
                new DocumentFingerprint(
                    reader.GetString(1),
                    reader.GetInt64(2),
                    SqliteValueConverters.ParseDateTime(reader.GetString(3))),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                (AnnotationColor)reader.GetInt32(8),
                rectangles,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                SqliteValueConverters.ParseDateTime(reader.GetString(11)),
                SqliteValueConverters.ParseDateTime(reader.GetString(12))));
        }

        return results;
    }

    public Task AddAsync(TextHighlightAnnotation annotation, CancellationToken cancellationToken) =>
        SaveAsync(annotation, insertOnly: true, cancellationToken);

    public Task UpdateAsync(TextHighlightAnnotation annotation, CancellationToken cancellationToken) =>
        SaveAsync(annotation, insertOnly: false, cancellationToken);

    public async Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Annotations WHERE AnnotationId = $annotationId;";
        command.Parameters.AddWithValue("$annotationId", annotationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveAsync(
        TextHighlightAnnotation annotation,
        bool insertOnly,
        CancellationToken cancellationToken)
    {
        ValidateAnnotation(annotation);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var documentId = await FindDocumentIdAsync(
            connection,
            transaction,
            annotation.DocumentFingerprint.FastFingerprint,
            cancellationToken) ?? throw new InvalidOperationException(
                "An annotation cannot be saved before its document record exists.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insertOnly
            ? """
                INSERT INTO Annotations
                    (AnnotationId, DocumentId, PageIndex, CharacterStart, CharacterCount,
                     SelectedTextPreview, Color, Note, RectanglesJson, CreatedAt, ModifiedAt)
                VALUES
                    ($annotationId, $documentId, $pageIndex, $characterStart, $characterCount,
                     $preview, $color, $note, $rectangles, $createdAt, $modifiedAt);
                """
            : """
                UPDATE Annotations SET
                    DocumentId = $documentId,
                    PageIndex = $pageIndex,
                    CharacterStart = $characterStart,
                    CharacterCount = $characterCount,
                    SelectedTextPreview = $preview,
                    Color = $color,
                    Note = $note,
                    RectanglesJson = $rectangles,
                    ModifiedAt = $modifiedAt
                WHERE AnnotationId = $annotationId;
                """;
        command.Parameters.AddWithValue("$annotationId", annotation.AnnotationId.ToString("D"));
        command.Parameters.AddWithValue("$documentId", documentId.ToString("D"));
        command.Parameters.AddWithValue("$pageIndex", annotation.PageIndex);
        command.Parameters.AddWithValue("$characterStart", annotation.CharacterStart);
        command.Parameters.AddWithValue("$characterCount", annotation.CharacterCount);
        command.Parameters.AddWithValue("$preview", annotation.SelectedTextPreview);
        command.Parameters.AddWithValue("$color", (int)annotation.Color);
        command.Parameters.AddWithValue("$note", (object?)annotation.Note ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$rectangles",
            JsonSerializer.Serialize(annotation.Rectangles, RectangleSerializerOptions));
        command.Parameters.AddWithValue("$createdAt", SqliteValueConverters.FormatDateTime(annotation.CreatedAt));
        command.Parameters.AddWithValue("$modifiedAt", SqliteValueConverters.FormatDateTime(annotation.ModifiedAt));
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (!insertOnly && affectedRows == 0)
        {
            throw new KeyNotFoundException($"Annotation {annotation.AnnotationId:D} does not exist.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Guid?> FindDocumentIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DocumentId FROM Documents WHERE FastFingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? Guid.Parse(text) : null;
    }

    private static void ValidateAnnotation(TextHighlightAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.AnnotationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(annotation.DocumentFingerprint.FastFingerprint) ||
            annotation.PageIndex < 0 || annotation.CharacterStart < 0 || annotation.CharacterCount <= 0 ||
            string.IsNullOrWhiteSpace(annotation.SelectedTextPreview) ||
            annotation.SelectedTextPreview.Length > 300 ||
            !Enum.IsDefined(annotation.Color) || annotation.Rectangles.Count == 0 ||
            annotation.Rectangles.Any(rectangle =>
                !double.IsFinite(rectangle.Left) || !double.IsFinite(rectangle.Bottom) ||
                !double.IsFinite(rectangle.Right) || !double.IsFinite(rectangle.Top) ||
                rectangle.Right < rectangle.Left || rectangle.Top < rectangle.Bottom))
        {
            throw new ArgumentException("The text highlight annotation contains invalid values.", nameof(annotation));
        }
    }
}
