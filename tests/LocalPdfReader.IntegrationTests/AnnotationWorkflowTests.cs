using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using LocalPdfReader.App;
using LocalPdfReader.Application.Annotations;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Domain;
using LocalPdfReader.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public sealed class AnnotationWorkflowTests
{
    [Fact]
    public void AnnotationPanelSupportsPageAndModifiedTimeSorting()
    {
        var now = DateTimeOffset.UtcNow;
        var fingerprint = new DocumentFingerprint("sort", 1, now);
        var pageTwoNewer = new TextHighlightAnnotation(
            Guid.NewGuid(), fingerprint, 1, 20, 2, "newer", AnnotationColor.Yellow,
            [new PdfRect(1, 2, 3, 4)], null, now.AddHours(-1), now.AddHours(-1));
        var pageOneOlder = new TextHighlightAnnotation(
            Guid.NewGuid(), fingerprint, 0, 10, 2, "older", AnnotationColor.Blue,
            [new PdfRect(1, 2, 3, 4)], null, now.AddHours(-2), now.AddHours(-2));
        var panel = new DocumentAnnotationViewModel(
            isAvailable: true,
            "available",
            [pageTwoNewer, pageOneOlder]);

        Assert.Equal(pageOneOlder.AnnotationId, panel.Items[0].Annotation.AnnotationId);

        panel.SortByModifiedTime = true;

        Assert.Equal(pageTwoNewer.AnnotationId, panel.Items[0].Annotation.AnnotationId);
        Assert.Equal(pageOneOlder.AnnotationId, panel.Items[1].Annotation.AnnotationId);
    }

    [Fact]
    public async Task ReplacingContentAtTheSamePathDoesNotReuseOldAnnotations()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.AnnotationIdentity.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(directoryPath, "replaced.pdf");
        await File.WriteAllTextAsync(filePath, "original content");
        var database = new SqliteLocalDatabase(
            Path.Combine(directoryPath, "reader.db"),
            "0.5.0-test",
            NullLogger<SqliteLocalDatabase>.Instance);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var settings = new StaticSettingsService();
            var documentRepository = new SqliteDocumentHistoryRepository(database);
            var history = new DocumentHistoryService(
                database,
                new Sha256DocumentFingerprintService(),
                documentRepository,
                new SqliteReadingStateRepository(database),
                settings);
            var annotationService = new AnnotationService(
                new SqliteAnnotationRepository(database),
                TimeProvider.System);
            var first = Assert.IsType<DocumentPersistenceContext>(
                await history.RegisterSuccessfulOpenAsync(filePath, CancellationToken.None));
            await annotationService.CreateHighlightAsync(
                first.Document,
                new TextSelection(
                    new DocumentId(Guid.NewGuid()),
                    0,
                    0,
                    3,
                    "text",
                    "text",
                    [new PdfRect(1, 2, 3, 4)]),
                AnnotationColor.Yellow,
                note: null,
                CancellationToken.None);

            await File.WriteAllTextAsync(filePath, "completely different replacement content");
            var second = Assert.IsType<DocumentPersistenceContext>(
                await history.RegisterSuccessfulOpenAsync(filePath, CancellationToken.None));

            Assert.NotEqual(first.Document.DocumentId, second.Document.DocumentId);
            Assert.Single(await annotationService.GetByDocumentAsync(
                first.Document.DocumentId,
                CancellationToken.None));
            Assert.Empty(await annotationService.GetByDocumentAsync(
                second.Document.DocumentId,
                CancellationToken.None));
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderMarksLocalAnnotationsUnsavedUntilTheyAreWrittenToPdf()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.AnnotationPdfSave.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(directoryPath, "annotated.pdf");
        var savePath = Path.Combine(directoryPath, "annotated-copy.pdf");
        await File.WriteAllTextAsync(filePath, "document with local annotations");
        var database = new SqliteLocalDatabase(
            Path.Combine(directoryPath, "reader.db"),
            "1.0.0-test",
            NullLogger<SqliteLocalDatabase>.Instance);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var settings = new StaticSettingsService();
            var documentRepository = new SqliteDocumentHistoryRepository(database);
            var history = new DocumentHistoryService(
                database,
                new Sha256DocumentFingerprintService(),
                documentRepository,
                new SqliteReadingStateRepository(database),
                settings);
            var annotationService = new AnnotationService(
                new SqliteAnnotationRepository(database),
                TimeProvider.System);
            var pdfSync = new RecordingPdfAnnotationSyncService();
            await using var worker = new AnnotationWorkerClient();
            var reader = CreateReader(worker, history, annotationService, settings, pdfSync);

            await reader.OpenDocumentAsync(filePath, CancellationToken.None);
            await SelectHelloAsync(reader);
            await reader.CreateHighlightAsync(
                AnnotationColor.Yellow,
                openNoteEditor: false,
                CancellationToken.None);

            var tab = Assert.Single(reader.DocumentTabs);
            Assert.True(tab.HasUnsavedAnnotations);

            var result = await reader.SaveActiveAnnotationsToPdfAsync(
                savePath,
                PdfAnnotationSaveMode.Full,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.False(tab.HasUnsavedAnnotations);
            Assert.Equal(savePath, pdfSync.DestinationFilePath);
            Assert.Equal(PdfAnnotationSaveMode.Full, pdfSync.SaveMode);
            var savedAnnotation = Assert.Single(pdfSync.Annotations);
            Assert.Equal(AnnotationColor.Yellow, savedAnnotation.Color);
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderCanSavePendingAnnotationEditsBeforeWritingPdfAnnotations()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.AnnotationPendingSave.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(directoryPath, "pending.pdf");
        var savePath = Path.Combine(directoryPath, "pending-copy.pdf");
        await File.WriteAllTextAsync(filePath, "document with pending annotation edits");
        var database = new SqliteLocalDatabase(
            Path.Combine(directoryPath, "reader.db"),
            "1.0.0-test",
            NullLogger<SqliteLocalDatabase>.Instance);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var settings = new StaticSettingsService();
            var documentRepository = new SqliteDocumentHistoryRepository(database);
            var history = new DocumentHistoryService(
                database,
                new Sha256DocumentFingerprintService(),
                documentRepository,
                new SqliteReadingStateRepository(database),
                settings);
            var annotationRepository = new SqliteAnnotationRepository(database);
            var annotationService = new AnnotationService(annotationRepository, TimeProvider.System);
            var pdfSync = new RecordingPdfAnnotationSyncService();
            await using var worker = new AnnotationWorkerClient();
            var reader = CreateReader(worker, history, annotationService, settings, pdfSync);

            await reader.OpenDocumentAsync(filePath, CancellationToken.None);
            await SelectHelloAsync(reader);
            await reader.CreateHighlightAsync(
                AnnotationColor.Yellow,
                openNoteEditor: false,
                CancellationToken.None);

            var tab = Assert.Single(reader.DocumentTabs);
            var item = Assert.Single(tab.Annotations.Items);
            item.SelectedColor = AnnotationColor.Green;
            item.DraftNote = "saved before pdf";

            Assert.True(reader.HasPendingLocalAnnotationChanges);
            Assert.True(reader.TabHasPendingLocalAnnotationChanges(tab));

            Assert.True(await reader.SavePendingLocalAnnotationChangesAsync(tab, CancellationToken.None));
            Assert.False(reader.HasPendingLocalAnnotationChanges);

            var result = await reader.SaveActiveAnnotationsToPdfAsync(
                savePath,
                PdfAnnotationSaveMode.Full,
                CancellationToken.None);

            Assert.NotNull(result);
            var savedAnnotation = Assert.Single(pdfSync.Annotations);
            Assert.Equal(AnnotationColor.Green, savedAnnotation.Color);
            Assert.Equal("saved before pdf", savedAnnotation.Note);
            Assert.False(tab.HasUnsavedAnnotations);
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task AnnotationWorkflowPersistsAcrossTabsRestartAndWorkerRecovery()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Annotation.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var firstPath = Path.Combine(directoryPath, "first.pdf");
        var secondPath = Path.Combine(directoryPath, "second.pdf");
        await File.WriteAllTextAsync(firstPath, "first local document identity");
        await File.WriteAllTextAsync(secondPath, "second local document identity");
        var originalBytes = await File.ReadAllBytesAsync(firstPath);
        var originalWriteTime = File.GetLastWriteTimeUtc(firstPath);
        File.SetAttributes(firstPath, File.GetAttributes(firstPath) | FileAttributes.ReadOnly);
        var database = new SqliteLocalDatabase(
            Path.Combine(directoryPath, "reader.db"),
            "0.5.0-test",
            NullLogger<SqliteLocalDatabase>.Instance);

        try
        {
            await database.InitializeAsync(CancellationToken.None);
            var settings = new StaticSettingsService();
            var documentRepository = new SqliteDocumentHistoryRepository(database);
            var readingStateRepository = new SqliteReadingStateRepository(database);
            var annotationRepository = new SqliteAnnotationRepository(database);
            var history = new DocumentHistoryService(
                database,
                new Sha256DocumentFingerprintService(),
                documentRepository,
                readingStateRepository,
                settings);
            var annotationService = new AnnotationService(annotationRepository, TimeProvider.System);

            await using (var firstWorker = new AnnotationWorkerClient())
            {
                var firstReader = CreateReader(firstWorker, history, annotationService, settings);
                await firstReader.OpenDocumentAsync(firstPath, CancellationToken.None);
                await SelectHelloAsync(firstReader);

                await firstReader.CreateHighlightAsync(
                    AnnotationColor.Blue,
                    openNoteEditor: true,
                    CancellationToken.None);

                var firstTab = Assert.Single(firstReader.DocumentTabs);
                var createdItem = Assert.Single(firstTab.Annotations.Items);
                Assert.Equal(3, firstReader.LeftSidebarIndex);
                Assert.Equal(AnnotationColor.Blue, createdItem.Annotation.Color);
                Assert.NotEmpty(firstReader.AnnotationHighlightRectangles);
                Assert.Null(firstReader.CurrentSelection);

                var initialWidth = firstReader.AnnotationHighlightRectangles[0].Width;
                var previousRenderCount = firstWorker.RenderCount;
                firstReader.ZoomInCommand.Execute(null);
                await WaitUntilAsync(() =>
                    firstWorker.RenderCount > previousRenderCount && !firstReader.IsRendering);
                Assert.True(firstReader.AnnotationHighlightRectangles[0].Width > initialWidth);

                previousRenderCount = firstWorker.RenderCount;
                firstReader.RotateClockwiseCommand.Execute(null);
                await WaitUntilAsync(() =>
                    firstWorker.RenderCount > previousRenderCount && !firstReader.IsRendering);
                Assert.NotEmpty(firstReader.AnnotationHighlightRectangles);

                createdItem.SelectedColor = AnnotationColor.Green;
                createdItem.DraftNote = "important local note";
                await firstReader.SaveAnnotationChangesAsync(createdItem, CancellationToken.None);

                var persistedDocument = Assert.IsType<DocumentRecord>(firstTab.PersistenceRecord);
                var persisted = Assert.Single(await annotationRepository.GetByDocumentAsync(
                    persistedDocument.DocumentId,
                    CancellationToken.None));
                Assert.Equal(AnnotationColor.Green, persisted.Color);
                Assert.Equal("important local note", persisted.Note);
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(firstPath));
                Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(firstPath));

                // A later-page fixture verifies that list navigation changes page before highlighting.
                persisted = persisted with { PageIndex = 2, ModifiedAt = DateTimeOffset.UtcNow };
                await annotationRepository.UpdateAsync(persisted, CancellationToken.None);
            }

            await using (var recoveredWorker = new AnnotationWorkerClient())
            {
                var reader = CreateReader(recoveredWorker, history, annotationService, settings);
                await reader.OpenDocumentAsync(firstPath, CancellationToken.None);
                var firstTab = Assert.Single(reader.DocumentTabs);
                var loadedItem = Assert.Single(firstTab.Annotations.Items);
                Assert.Equal(2, loadedItem.Annotation.PageIndex);
                Assert.Empty(reader.AnnotationHighlightRectangles);

                await reader.NavigateToAnnotationAsync(loadedItem, CancellationToken.None);
                Assert.Equal(2, firstTab.ReaderState.CurrentPageIndex);
                Assert.NotEmpty(reader.AnnotationHighlightRectangles);

                await reader.OpenDocumentAsync(secondPath, CancellationToken.None);
                var secondTab = Assert.Single(reader.DocumentTabs, tab => tab.FullPath == secondPath);
                Assert.Empty(secondTab.Annotations.Items);
                Assert.Empty(reader.AnnotationHighlightRectangles);

                await reader.ActivateDocumentTabAsync(firstTab, CancellationToken.None);
                Assert.Single(firstTab.Annotations.Items);
                Assert.NotEmpty(reader.AnnotationHighlightRectangles);

                firstTab.Annotations.ReplaceAll([]);
                reader.HandleWorkerDisconnected(new PdfWorkerDisconnectedEventArgs(
                    PdfWorkerDisconnectReason.ProcessExited,
                    exitCode: 1,
                    exception: null));
                await reader.RecoverDocumentAfterWorkerRestartAsync(CancellationToken.None);

                Assert.Single(firstTab.Annotations.Items);
                Assert.NotEmpty(reader.AnnotationHighlightRectangles);
                Assert.Equal(4, recoveredWorker.OpenedPaths.Count);

                var reloadedItem = Assert.Single(firstTab.Annotations.Items);
                await reader.DeleteAnnotationAsync(reloadedItem, CancellationToken.None);
                Assert.Empty(firstTab.Annotations.Items);
                Assert.Empty(reader.AnnotationHighlightRectangles);
                Assert.Empty(await annotationRepository.GetByDocumentAsync(
                    firstTab.PersistenceRecord!.DocumentId,
                    CancellationToken.None));
            }

            await using (var failingWorker = new AnnotationWorkerClient())
            {
                var reader = CreateReader(
                    failingWorker,
                    history,
                    new FailingAnnotationService(),
                    settings);
                await reader.OpenDocumentAsync(firstPath, CancellationToken.None);
                await SelectHelloAsync(reader);

                await reader.CreateHighlightAsync(
                    AnnotationColor.Yellow,
                    openNoteEditor: false,
                    CancellationToken.None);

                Assert.NotNull(reader.CurrentSelection);
                Assert.Empty(reader.ActiveDocumentTab!.Annotations.Items);
                Assert.Empty(reader.AnnotationHighlightRectangles);
                Assert.Contains("ANNOTATION_WRITE_FAILED", reader.StatusText, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(firstPath))
            {
                File.SetAttributes(firstPath, FileAttributes.Normal);
            }

            await database.DisposeAsync();
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The reader operation did not complete in time.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task SelectHelloAsync(ReaderViewModel reader)
    {
        await reader.BeginSelectionAsync(new ViewPoint(14, 45), CancellationToken.None);
        reader.UpdateSelection(new ViewPoint(70, 45));
        reader.CompleteSelection();
        Assert.Equal("HELLO", Assert.IsType<TextSelection>(reader.CurrentSelection).NormalizedText);
    }

    private static ReaderViewModel CreateReader(
        AnnotationWorkerClient worker,
        DocumentHistoryService history,
        IAnnotationService annotationService,
        ISettingsService settings,
        IPdfAnnotationSyncService? pdfAnnotationSyncService = null)
    {
        var userErrors = new UserErrorService(NullLogger<UserErrorService>.Instance);
        var translation = new TranslationPanelViewModel(
            new NoOpTranslationService(),
            settings,
            new NoOpClipboardService(),
            userErrors);
        return new ReaderViewModel(
            worker,
            new ReaderState(),
            new CoordinateTransformer(),
            new TextSelectionService(),
            translation,
            userErrors,
            history,
            NullLogger<ReaderViewModel>.Instance,
            annotationService,
            settings,
            pdfAnnotationSyncService: pdfAnnotationSyncService);
    }

    private sealed class StaticSettingsService : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpClipboardService : IClipboardService
    {
        public Task SetTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpTranslationService : ITranslationService
    {
        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingAnnotationService : IAnnotationService
    {
        public Task<IReadOnlyList<TextHighlightAnnotation>> GetByDocumentAsync(
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TextHighlightAnnotation>>([]);

        public Task<TextHighlightAnnotation> CreateHighlightAsync(
            DocumentRecord document,
            TextSelection selection,
            AnnotationColor color,
            string? note,
            CancellationToken cancellationToken) =>
            Task.FromException<TextHighlightAnnotation>(new IOException("Simulated database write failure."));

        public Task<TextHighlightAnnotation> UpdateNoteAsync(
            TextHighlightAnnotation annotation,
            string? note,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TextHighlightAnnotation> UpdateColorAsync(
            TextHighlightAnnotation annotation,
            AnnotationColor color,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TextHighlightAnnotation> UpdateAsync(
            TextHighlightAnnotation annotation,
            AnnotationColor color,
            string? note,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPdfAnnotationSyncService : IPdfAnnotationSyncService
    {
        public IReadOnlyList<TextHighlightAnnotation> Annotations { get; private set; } = [];

        public string? DestinationFilePath { get; private set; }

        public PdfAnnotationSaveMode? SaveMode { get; private set; }

        public Task<IReadOnlyList<PdfStandardAnnotation>> ReadPdfAnnotationsAsync(
            IPdfWorkerClient workerClient,
            DocumentId documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PdfStandardAnnotation>>([]);

        public Task<PdfAnnotationSaveResult> SaveLocalHighlightsAsync(
            IPdfWorkerClient workerClient,
            DocumentId documentId,
            IReadOnlyList<TextHighlightAnnotation> annotations,
            string destinationFilePath,
            PdfAnnotationSaveMode saveMode,
            CancellationToken cancellationToken)
        {
            Annotations = annotations.ToArray();
            DestinationFilePath = destinationFilePath;
            SaveMode = saveMode;
            return Task.FromResult(new PdfAnnotationSaveResult(destinationFilePath, []));
        }
    }

    private sealed class AnnotationWorkerClient : IPdfWorkerClient
    {
        private const double PageWidth = 100;
        private const double PageHeight = 120;
        private readonly Dictionary<string, MemoryMappedFile> _memoryMaps = [];

        public event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public List<string> OpenedPaths { get; } = [];

        public int RenderCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PdfDocumentInfo> OpenDocumentAsync(
            string filePath,
            string? password,
            CancellationToken cancellationToken)
        {
            OpenedPaths.Add(filePath);
            return Task.FromResult(new PdfDocumentInfo(
                new DocumentId(Guid.NewGuid()),
                Path.GetFileName(filePath),
                3,
                false,
                true,
                null,
                null));
        }

        public Task CloseDocumentAsync(DocumentId documentId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<RenderedPageDescriptor> RenderPageAsync(
            PageRenderRequest request,
            CancellationToken cancellationToken)
        {
            RenderCount++;
            var quarterTurn = request.Rotation is PageRotation.Rotate90 or PageRotation.Rotate270;
            var rotatedWidth = quarterTurn ? PageHeight : PageWidth;
            var rotatedHeight = quarterTurn ? PageWidth : PageHeight;
            var pixelWidth = Math.Max(1, (int)Math.Round(rotatedWidth * request.ZoomFactor * 96d / 72d));
            var pixelHeight = Math.Max(1, (int)Math.Round(rotatedHeight * request.ZoomFactor * 96d / 72d));
            var stride = pixelWidth * 4;
            var length = stride * pixelHeight;
            var memoryMapName = $"LocalPdfReader.AnnotationTest.{Guid.NewGuid():N}";
            var memoryMap = MemoryMappedFile.CreateNew(memoryMapName, length);
            using (var accessor = memoryMap.CreateViewAccessor())
            {
                accessor.WriteArray(0, new byte[length], 0, length);
            }

            _memoryMaps[memoryMapName] = memoryMap;
            return Task.FromResult(new RenderedPageDescriptor(
                request.RequestId,
                request.DocumentId,
                request.PageIndex,
                pixelWidth,
                pixelHeight,
                stride,
                "BGRA32",
                memoryMapName,
                length,
                new PdfSize(PageWidth, PageHeight)));
        }

        public Task<PageTextData> GetPageTextAsync(
            DocumentId documentId,
            int pageIndex,
            CancellationToken cancellationToken)
        {
            var glyphs = new[]
            {
                new TextGlyph(0, "H", new PdfRect(10, 80, 18, 90), 0, 0),
                new TextGlyph(1, "E", new PdfRect(20, 80, 28, 90), 0, 0),
                new TextGlyph(2, "L", new PdfRect(30, 80, 38, 90), 0, 0),
                new TextGlyph(3, "L", new PdfRect(40, 80, 48, 90), 0, 0),
                new TextGlyph(4, "O", new PdfRect(50, 80, 58, 90), 0, 0)
            };
            return Task.FromResult(new PageTextData(documentId, pageIndex, "HELLO", glyphs));
        }

        public Task<TextHitTestResult?> HitTestTextAsync(
            DocumentId documentId,
            int pageIndex,
            PdfPoint point,
            double tolerance,
            CancellationToken cancellationToken) => Task.FromResult<TextHitTestResult?>(null);

        public async IAsyncEnumerable<SearchUpdate> SearchDocumentAsync(
            DocumentId documentId,
            Guid searchSessionId,
            string query,
            bool matchCase,
            bool wholeWord,
            int batchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public Task ReleaseSharedMemoryAsync(string memoryMapName, CancellationToken cancellationToken)
        {
            if (_memoryMaps.Remove(memoryMapName, out var memoryMap))
            {
                memoryMap.Dispose();
            }

            return Task.CompletedTask;
        }

        public Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            foreach (var memoryMap in _memoryMaps.Values)
            {
                memoryMap.Dispose();
            }

            _memoryMaps.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
