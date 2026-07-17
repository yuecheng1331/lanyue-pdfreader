using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Tests;

public sealed class DocumentHistoryServiceTests
{
    [Fact]
    public async Task SuccessfulOpenAfterRenameReusesIdentityAndReadingState()
    {
        var oldPath = Path.GetFullPath("paper.pdf");
        var movedPath = Path.GetFullPath(Path.Combine("archive", "renamed.pdf"));
        var fingerprint = CreateFingerprint("same-document");
        var fingerprintService = new FakeFingerprintService(
            new Dictionary<string, DocumentFingerprint>(StringComparer.OrdinalIgnoreCase)
            {
                [oldPath] = fingerprint,
                [movedPath] = fingerprint
            });
        var documentRepository = new FakeDocumentHistoryRepository();
        var readingStateRepository = new FakeReadingStateRepository();
        var service = CreateService(
            new AppSettings(),
            fingerprintService,
            documentRepository,
            readingStateRepository);

        var firstOpen = Assert.IsType<DocumentPersistenceContext>(
            await service.RegisterSuccessfulOpenAsync(oldPath, CancellationToken.None));
        var savedState = new ReadingState(
            firstOpen.Document.DocumentId,
            7,
            1.75,
            nameof(ReaderZoomMode.ActualZoom),
            PageRotation.Rotate90,
            12,
            240,
            false,
            "Search",
            true,
            DateTimeOffset.UtcNow);
        await readingStateRepository.SaveAsync(savedState, CancellationToken.None);

        var secondOpen = Assert.IsType<DocumentPersistenceContext>(
            await service.RegisterSuccessfulOpenAsync(movedPath, CancellationToken.None));

        Assert.Equal(firstOpen.Document.DocumentId, secondOpen.Document.DocumentId);
        Assert.Equal(movedPath, secondOpen.Document.LastKnownPath);
        Assert.Equal(savedState, secondOpen.ReadingState);
        var recent = Assert.Single(await service.GetRecentAsync(CancellationToken.None));
        Assert.Equal(2, recent.OpenCount);
    }

    [Fact]
    public async Task DisabledRecentHistoryDoesNotAddOrDeleteExistingRecentItems()
    {
        var firstPath = Path.GetFullPath("first.pdf");
        var secondPath = Path.GetFullPath("second.pdf");
        var fingerprintService = new FakeFingerprintService(
            new Dictionary<string, DocumentFingerprint>(StringComparer.OrdinalIgnoreCase)
            {
                [firstPath] = CreateFingerprint("first"),
                [secondPath] = CreateFingerprint("second")
            });
        var documentRepository = new FakeDocumentHistoryRepository();
        var readingStateRepository = new FakeReadingStateRepository();
        var settings = new MutableSettingsService(new AppSettings());
        var service = CreateService(
            settings,
            fingerprintService,
            documentRepository,
            readingStateRepository);

        await service.RegisterSuccessfulOpenAsync(firstPath, CancellationToken.None);
        settings.Settings = settings.Settings with
        {
            History = settings.Settings.History with { RecordRecentDocuments = false }
        };
        var secondOpen = await service.RegisterSuccessfulOpenAsync(secondPath, CancellationToken.None);

        Assert.NotNull(secondOpen);
        var recent = Assert.Single(await service.GetRecentAsync(CancellationToken.None));
        Assert.Equal(Path.GetFileName(firstPath), recent.Document.FileName);
    }

    [Fact]
    public async Task MissingRecentDocumentIsMarkedWhenTheHomePageRefreshes()
    {
        var path = Path.GetFullPath("missing-later.pdf");
        var fingerprintService = new FakeFingerprintService(
            new Dictionary<string, DocumentFingerprint>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = CreateFingerprint("missing")
            });
        var documentRepository = new FakeDocumentHistoryRepository();
        var service = CreateService(
            new AppSettings(),
            fingerprintService,
            documentRepository,
            new FakeReadingStateRepository());
        var opened = Assert.IsType<DocumentPersistenceContext>(
            await service.RegisterSuccessfulOpenAsync(path, CancellationToken.None));
        fingerprintService.ExistingPaths.Clear();

        var recent = Assert.Single(await service.GetRecentAsync(CancellationToken.None));

        Assert.True(recent.Document.IsMissing);
        Assert.True(documentRepository.Documents[opened.Document.DocumentId].IsMissing);
    }

    [Fact]
    public async Task MissingDocumentCanBeRemovedFromRecentHistoryWithoutDeletingItsIdentity()
    {
        var path = Path.GetFullPath("removed-from-history.pdf");
        var fingerprintService = new FakeFingerprintService(
            new Dictionary<string, DocumentFingerprint>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = CreateFingerprint("removed-history")
            });
        var documentRepository = new FakeDocumentHistoryRepository();
        var service = CreateService(
            new AppSettings(),
            fingerprintService,
            documentRepository,
            new FakeReadingStateRepository());
        var opened = Assert.IsType<DocumentPersistenceContext>(
            await service.RegisterSuccessfulOpenAsync(path, CancellationToken.None));

        await service.MarkMissingAndRemoveFromRecentAsync(opened.Document.DocumentId, CancellationToken.None);

        Assert.True(documentRepository.Documents[opened.Document.DocumentId].IsMissing);
        Assert.Empty(await service.GetRecentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadingStateRoundTripsAllReaderAndScrollValues()
    {
        var path = Path.GetFullPath("state.pdf");
        var fingerprintService = new FakeFingerprintService(
            new Dictionary<string, DocumentFingerprint>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = CreateFingerprint("state")
            });
        var readingStateRepository = new FakeReadingStateRepository();
        var service = CreateService(
            new AppSettings(),
            fingerprintService,
            new FakeDocumentHistoryRepository(),
            readingStateRepository);
        var opened = Assert.IsType<DocumentPersistenceContext>(
            await service.RegisterSuccessfulOpenAsync(path, CancellationToken.None));
        var viewState = new ReaderViewState(
            11,
            2.25,
            PageRotation.Rotate270,
            ReaderZoomMode.FitWidth,
            new PdfSize(612, 792));

        await service.SaveReadingStateAsync(
            opened.Document.DocumentId,
            viewState,
            45.5,
            680.25,
            CancellationToken.None);

        var saved = Assert.IsType<ReadingState>(
            await readingStateRepository.GetAsync(opened.Document.DocumentId, CancellationToken.None));
        Assert.Equal(11, saved.PageIndex);
        Assert.Equal(2.25, saved.ZoomFactor);
        Assert.Equal(nameof(ReaderZoomMode.FitWidth), saved.ViewMode);
        Assert.Equal(PageRotation.Rotate270, saved.Rotation);
        Assert.Equal(45.5, saved.HorizontalOffset);
        Assert.Equal(680.25, saved.VerticalOffset);
        Assert.Equal(viewState with { PageSize = null }, DocumentHistoryService.CreateReaderViewState(saved));
    }

    [Fact]
    public async Task DisabledHistoryAndReadingPositionStillCreateIdentityForAnnotations()
    {
        var path = Path.GetFullPath("no-restore.pdf");
        var settings = new AppSettings
        {
            Session = new SessionSettings
            {
                RestoreLastDocument = true,
                SaveReadingPosition = false
            },
            History = new HistorySettings { RecordRecentDocuments = false }
        };
        var fingerprintService = new FakeFingerprintService(
            new Dictionary<string, DocumentFingerprint>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = CreateFingerprint("no-restore")
            });
        var documentRepository = new FakeDocumentHistoryRepository();
        var service = CreateService(
            settings,
            fingerprintService,
            documentRepository,
            new FakeReadingStateRepository());

        var opened = Assert.IsType<DocumentPersistenceContext>(
            await service.RegisterSuccessfulOpenAsync(path, CancellationToken.None));
        Assert.Equal(path, opened.Document.LastKnownPath);
        Assert.Null(opened.ReadingState);
        Assert.Single(documentRepository.Documents);
        Assert.Empty(await service.GetRecentAsync(CancellationToken.None));
    }

    private static DocumentHistoryService CreateService(
        AppSettings settings,
        FakeFingerprintService fingerprintService,
        FakeDocumentHistoryRepository documentRepository,
        FakeReadingStateRepository readingStateRepository) =>
        CreateService(
            new MutableSettingsService(settings),
            fingerprintService,
            documentRepository,
            readingStateRepository);

    private static DocumentHistoryService CreateService(
        ISettingsService settingsService,
        FakeFingerprintService fingerprintService,
        FakeDocumentHistoryRepository documentRepository,
        FakeReadingStateRepository readingStateRepository) => new(
            new AvailableLocalDataStore(),
            fingerprintService,
            documentRepository,
            readingStateRepository,
            settingsService);

    private static DocumentFingerprint CreateFingerprint(string value) => new(
        value,
        1024,
        new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));

    private sealed class AvailableLocalDataStore : ILocalDataStore
    {
        public bool IsAvailable => true;

        public string DatabasePath => "in-memory";

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MutableSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Settings { get; set; } = settings;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFingerprintService(
        IReadOnlyDictionary<string, DocumentFingerprint> fingerprints) : IDocumentFingerprintService
    {
        public HashSet<string> ExistingPaths { get; } = new(
            fingerprints.Keys.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

        public Task<DocumentFingerprint> ComputeAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(fingerprints[NormalizePath(filePath)]);

        public string NormalizePath(string filePath) => Path.GetFullPath(filePath);

        public bool FileExists(string filePath) => ExistingPaths.Contains(NormalizePath(filePath));
    }

    private sealed class FakeReadingStateRepository : IReadingStateRepository
    {
        private readonly Dictionary<Guid, ReadingState> _states = [];

        public Task<ReadingState?> GetAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult(_states.GetValueOrDefault(documentId));

        public Task SaveAsync(ReadingState state, CancellationToken cancellationToken)
        {
            _states[state.DocumentId] = state;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid documentId, CancellationToken cancellationToken)
        {
            _states.Remove(documentId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDocumentHistoryRepository : IDocumentHistoryRepository
    {
        private readonly Dictionary<string, Guid> _documentIdsByFingerprint = [];
        private readonly Dictionary<Guid, RecentEntry> _recent = [];

        public Dictionary<Guid, DocumentRecord> Documents { get; } = [];

        public Task<DocumentRecord?> FindByFingerprintAsync(
            DocumentFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            var found = _documentIdsByFingerprint.TryGetValue(fingerprint.FastFingerprint, out var documentId)
                ? Documents[documentId]
                : null;
            return Task.FromResult(found);
        }

        public Task<DocumentRecord> UpsertAsync(DocumentRecord document, CancellationToken cancellationToken)
        {
            if (_documentIdsByFingerprint.TryGetValue(document.Fingerprint.FastFingerprint, out var existingId))
            {
                document = document with
                {
                    DocumentId = existingId,
                    FirstOpenedAt = Documents[existingId].FirstOpenedAt
                };
            }

            _documentIdsByFingerprint[document.Fingerprint.FastFingerprint] = document.DocumentId;
            Documents[document.DocumentId] = document;
            return Task.FromResult(document);
        }

        public Task RecordRecentAsync(Guid documentId, DateTimeOffset openedAt, CancellationToken cancellationToken)
        {
            var existing = _recent.GetValueOrDefault(documentId);
            _recent[documentId] = new RecentEntry(openedAt, (existing?.OpenCount ?? 0) + 1);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RecentDocument>> GetRecentAsync(int limit, CancellationToken cancellationToken)
        {
            IReadOnlyList<RecentDocument> result = _recent
                .OrderByDescending(item => item.Value.LastOpenedAt)
                .Take(limit)
                .Select(item => new RecentDocument(
                    Documents[item.Key],
                    item.Value.LastOpenedAt,
                    false,
                    item.Value.OpenCount,
                    null))
                .ToArray();
            return Task.FromResult(result);
        }

        public Task RemoveFromRecentAsync(Guid documentId, CancellationToken cancellationToken)
        {
            _recent.Remove(documentId);
            return Task.CompletedTask;
        }

        public Task ClearRecentAsync(CancellationToken cancellationToken)
        {
            _recent.Clear();
            return Task.CompletedTask;
        }

        public Task PruneRecentAsync(int maximumUnpinnedCount, CancellationToken cancellationToken)
        {
            foreach (var documentId in _recent
                         .OrderByDescending(item => item.Value.LastOpenedAt)
                         .Skip(maximumUnpinnedCount)
                         .Select(item => item.Key)
                         .ToArray())
            {
                _recent.Remove(documentId);
            }

            return Task.CompletedTask;
        }

        public Task SetMissingAsync(Guid documentId, bool isMissing, CancellationToken cancellationToken)
        {
            Documents[documentId] = Documents[documentId] with { IsMissing = isMissing };
            return Task.CompletedTask;
        }

        private sealed record RecentEntry(DateTimeOffset LastOpenedAt, int OpenCount);
    }
}
