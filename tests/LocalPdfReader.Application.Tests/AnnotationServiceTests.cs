using LocalPdfReader.Application.Annotations;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Tests;

public sealed class AnnotationServiceTests
{
    [Theory]
    [InlineData(AnnotationColor.Yellow)]
    [InlineData(AnnotationColor.Green)]
    [InlineData(AnnotationColor.Blue)]
    [InlineData(AnnotationColor.Pink)]
    public async Task EverySupportedHighlightColorIsPreserved(AnnotationColor color)
    {
        var repository = new RecordingAnnotationRepository();
        var service = new AnnotationService(repository, TimeProvider.System);
        var selection = new TextSelection(
            new DocumentId(Guid.NewGuid()),
            0,
            0,
            0,
            "x",
            "x",
            [new PdfRect(1, 2, 3, 4)]);

        var annotation = await service.CreateHighlightAsync(
            CreateDocument(),
            selection,
            color,
            note: null,
            CancellationToken.None);

        Assert.Equal(color, annotation.Color);
    }

    [Fact]
    public async Task CreateHighlightPersistsSelectionAgainstStableDocumentIdentity()
    {
        var repository = new RecordingAnnotationRepository();
        var now = new DateTimeOffset(2026, 7, 15, 9, 30, 0, TimeSpan.Zero);
        var service = new AnnotationService(repository, new FixedTimeProvider(now));
        var document = CreateDocument();
        var workerDocumentId = new DocumentId(Guid.NewGuid());
        var selection = new TextSelection(
            workerDocumentId,
            4,
            12,
            23,
            "important text",
            "important text",
            [new PdfRect(10, 20, 90, 38), new PdfRect(10, 42, 55, 60)]);

        var result = await service.CreateHighlightAsync(
            document,
            selection,
            AnnotationColor.Green,
            "review this",
            CancellationToken.None);

        Assert.Same(result, repository.AddedAnnotation);
        Assert.NotEqual(Guid.Empty, result.AnnotationId);
        Assert.Equal(document.Fingerprint, result.DocumentFingerprint);
        Assert.Equal(4, result.PageIndex);
        Assert.Equal(12, result.CharacterStart);
        Assert.Equal(12, result.CharacterCount);
        Assert.Equal("important text", result.SelectedTextPreview);
        Assert.Equal(AnnotationColor.Green, result.Color);
        Assert.Equal(selection.HighlightRectangles, result.Rectangles);
        Assert.NotSame(selection.HighlightRectangles, result.Rectangles);
        Assert.Equal("review this", result.Note);
        Assert.Equal(now, result.CreatedAt);
        Assert.Equal(now, result.ModifiedAt);
    }

    [Fact]
    public async Task CreateHighlightLimitsPreviewWithoutDiscardingSelectionRange()
    {
        var repository = new RecordingAnnotationRepository();
        var service = new AnnotationService(repository, TimeProvider.System);
        var longText = new string('a', 350);
        var selection = new TextSelection(
            new DocumentId(Guid.NewGuid()),
            0,
            5,
            354,
            longText,
            longText,
            [new PdfRect(1, 2, 3, 4)]);

        var result = await service.CreateHighlightAsync(
            CreateDocument(),
            selection,
            AnnotationColor.Yellow,
            note: null,
            CancellationToken.None);

        Assert.Equal(300, result.SelectedTextPreview.Length);
        Assert.Equal(350, result.CharacterCount);
    }

    [Fact]
    public async Task InvalidSelectionIsRejectedBeforeWriting()
    {
        var repository = new RecordingAnnotationRepository();
        var service = new AnnotationService(repository, TimeProvider.System);
        var selection = new TextSelection(
            new DocumentId(Guid.NewGuid()),
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            []);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateHighlightAsync(
            CreateDocument(),
            selection,
            AnnotationColor.Yellow,
            note: null,
            CancellationToken.None));

        Assert.Null(repository.AddedAnnotation);
    }

    [Fact]
    public async Task LoadedAnnotationCanChangeColorAndNoteAndThenBeDeleted()
    {
        var repository = new RecordingAnnotationRepository();
        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var service = new AnnotationService(repository, new FixedTimeProvider(now));
        var original = new TextHighlightAnnotation(
            Guid.NewGuid(),
            CreateDocument().Fingerprint,
            1,
            3,
            5,
            "text",
            AnnotationColor.Yellow,
            [new PdfRect(1, 2, 3, 4)],
            null,
            now.AddHours(-1),
            now.AddHours(-1));
        repository.StoredAnnotations.Add(original);

        var loaded = Assert.Single(await service.GetByDocumentAsync(
            CreateDocument().DocumentId,
            CancellationToken.None));
        var recolored = await service.UpdateColorAsync(
            loaded,
            AnnotationColor.Pink,
            CancellationToken.None);
        var noted = await service.UpdateNoteAsync(
            recolored,
            "updated note",
            CancellationToken.None);
        await service.DeleteAsync(noted.AnnotationId, CancellationToken.None);

        Assert.Equal(AnnotationColor.Pink, noted.Color);
        Assert.Equal("updated note", noted.Note);
        Assert.Equal(now, noted.ModifiedAt);
        Assert.Equal(2, repository.UpdatedAnnotations.Count);
        Assert.Equal(noted.AnnotationId, repository.DeletedAnnotationId);
    }

    private static DocumentRecord CreateDocument()
    {
        var openedAt = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        return new DocumentRecord(
            Guid.NewGuid(),
            new DocumentFingerprint("stable-fingerprint", 4096, openedAt.AddDays(-1)),
            @"C:\papers\paper.pdf",
            "paper.pdf",
            openedAt,
            openedAt,
            false);
    }

    private sealed class RecordingAnnotationRepository : IAnnotationRepository
    {
        public TextHighlightAnnotation? AddedAnnotation { get; private set; }

        public List<TextHighlightAnnotation> StoredAnnotations { get; } = [];

        public List<TextHighlightAnnotation> UpdatedAnnotations { get; } = [];

        public Guid? DeletedAnnotationId { get; private set; }

        public Task<IReadOnlyList<TextHighlightAnnotation>> GetByDocumentAsync(
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TextHighlightAnnotation>>(StoredAnnotations.ToArray());

        public Task AddAsync(TextHighlightAnnotation annotation, CancellationToken cancellationToken)
        {
            AddedAnnotation = annotation;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(TextHighlightAnnotation annotation, CancellationToken cancellationToken)
        {
            UpdatedAnnotations.Add(annotation);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken)
        {
            DeletedAnnotationId = annotationId;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
