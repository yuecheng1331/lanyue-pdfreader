using LocalPdfReader.App;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Infrastructure.Configuration;
using LocalPdfReader.Infrastructure.PdfWorker;
using LocalPdfReader.Infrastructure.Translation;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using LocalPdfReader.Infrastructure.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Diagnostics;

namespace LocalPdfReader.IntegrationTests;

public class ProjectSmokeTests
{
    [Fact]
    public async Task SettingsCanBeSavedAndLoaded()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Tests.{Guid.NewGuid():N}");
        var settingsFilePath = Path.Combine(directoryPath, "settings.json");
        var settingsService = new JsonSettingsService(settingsFilePath, NullLogger<JsonSettingsService>.Instance);
        var settings = new AppSettings
        {
            Reader = new ReaderSettings { RememberLastFile = true }
        };

        try
        {
            await settingsService.SaveAsync(settings, CancellationToken.None);
            var loadedSettings = await settingsService.LoadAsync(CancellationToken.None);

            Assert.True(loadedSettings.Reader.RememberLastFile);
            Assert.True(File.Exists(settingsFilePath));
            Assert.DoesNotContain("api-key", await File.ReadAllTextAsync(settingsFilePath));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public void UserErrorKeepsTechnicalDetailOnlyInLocalLog()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.LogTests.{Guid.NewGuid():N}");
        const string technicalDetail = "internal-diagnostic-marker";

        try
        {
            using var loggerProvider = new LocalFileLoggerProvider(directoryPath);
            var service = new UserErrorService(new TypedLogger<UserErrorService>(
                loggerProvider.CreateLogger(typeof(UserErrorService).FullName!)));

            var error = service.Report(
                UserErrorCode.DocumentOpenFailed,
                new InvalidOperationException(technicalDetail));
            var logFile = Assert.Single(Directory.GetFiles(directoryPath, "*.log"));
            var logText = File.ReadAllText(logFile);

            Assert.DoesNotContain(technicalDetail, error.DialogText);
            Assert.Contains("PDF_OPEN_FAILED", error.DialogText);
            Assert.Contains(technicalDetail, logText);
            Assert.Contains(nameof(InvalidOperationException), logText);
            Assert.Contains("+08:00", logText);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WindowsCredentialStoreCanSaveReadAndDeleteASecret()
    {
        var key = $"LocalPdfReader.Tests.{Guid.NewGuid():N}";
        const string secret = "test-api-key-not-real";
        var store = new WindowsCredentialStore();

        try
        {
            await store.SaveSecretAsync(key, secret, CancellationToken.None);

            Assert.Equal(secret, await store.ReadSecretAsync(key, CancellationToken.None));
        }
        finally
        {
            await store.DeleteSecretAsync(key, CancellationToken.None);
        }

        Assert.Null(await store.ReadSecretAsync(key, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true, ProviderConnectionError.None)]
    [InlineData(HttpStatusCode.Unauthorized, false, ProviderConnectionError.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, false, ProviderConnectionError.RateLimited)]
    public async Task DeepSeekConnectionTesterClassifiesHttpResponses(
        HttpStatusCode statusCode,
        bool expectedSuccess,
        ProviderConnectionError expectedError)
    {
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new StubHttpMessageHandler(statusCode, secret));
        var tester = new DeepSeekConnectionTester(httpClient, NullLogger<DeepSeekConnectionTester>.Instance);

        var result = await tester.TestAsync(
            new TranslationSettings
            {
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-chat",
                TimeoutSeconds = 10
            },
            secret,
            CancellationToken.None);

        Assert.Equal(expectedSuccess, result.IsSuccess);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public async Task DeepSeekConnectionTesterNormalizesOfficialAnthropicBasePath()
    {
        var secret = Guid.NewGuid().ToString("N");
        using var httpClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, secret));
        var tester = new DeepSeekConnectionTester(httpClient, NullLogger<DeepSeekConnectionTester>.Instance);

        var result = await tester.TestAsync(
            new TranslationSettings
            {
                BaseUrl = "https://api.deepseek.com/anthropic",
                Model = "deepseek-v4-flash",
                TimeoutSeconds = 10
            },
            secret,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task WorkerProcessCanBeStartedAndStopped()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe");
        Assert.True(File.Exists(workerPath));

        await using var workerClient = new PdfWorkerClient(
            workerPath,
            NullLogger<PdfWorkerClient>.Instance);

        await workerClient.StartAsync(CancellationToken.None);
        await workerClient.PingAsync(CancellationToken.None);
        await workerClient.CancelRequestAsync(Guid.NewGuid(), CancellationToken.None);
        await workerClient.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnexpectedWorkerExitRaisesOneDisconnectNotification()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe");
        await using var workerClient = new PdfWorkerClient(
            workerPath,
            NullLogger<PdfWorkerClient>.Instance);
        var disconnected = new TaskCompletionSource<PdfWorkerDisconnectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        workerClient.Disconnected += (_, args) =>
        {
            Interlocked.Increment(ref notificationCount);
            disconnected.TrySetResult(args);
        };

        await workerClient.StartAsync(CancellationToken.None);
        var processId = Assert.IsType<int>(workerClient.WorkerProcessId);
        using (var process = Process.GetProcessById(processId))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var eventArgs = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref notificationCount));
        Assert.True(eventArgs.Reason is
            PdfWorkerDisconnectReason.ProcessExited or
            PdfWorkerDisconnectReason.PipeDisconnected);
        await workerClient.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerCanRestartAndReconnectAfterUnexpectedExit()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe");
        await using var workerClient = new PdfWorkerClient(
            workerPath,
            NullLogger<PdfWorkerClient>.Instance);
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        workerClient.Disconnected += (_, _) => disconnected.TrySetResult();

        await workerClient.StartAsync(CancellationToken.None);
        var firstProcessId = Assert.IsType<int>(workerClient.WorkerProcessId);
        using (var process = Process.GetProcessById(firstProcessId))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workerClient.StopAsync(CancellationToken.None);
        await workerClient.StartAsync(CancellationToken.None);
        await workerClient.PingAsync(CancellationToken.None);

        var secondProcessId = Assert.IsType<int>(workerClient.WorkerProcessId);
        Assert.NotEqual(firstProcessId, secondProcessId);
        await workerClient.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerOpensAndRendersTheFirstPageThroughSharedMemory()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Tests.{Guid.NewGuid():N}");
        var pdfPath = Path.Combine(directoryPath, "sample.pdf");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(pdfPath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Local PDF Reader C Phase")));

        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe");
            await using var workerClient = new PdfWorkerClient(workerPath, NullLogger<PdfWorkerClient>.Instance);
            await workerClient.StartAsync(CancellationToken.None);

            var document = await workerClient.OpenDocumentAsync(pdfPath, password: null, CancellationToken.None);
            Assert.Equal("sample.pdf", document.FileName);
            Assert.Equal(1, document.PageCount);

            var request = new PageRenderRequest(Guid.NewGuid(), document.DocumentId, 0, 1, PageRotation.Rotate0, 1, 1, RenderQuality.Normal);
            var rendered = await workerClient.RenderPageAsync(request, CancellationToken.None);
            Assert.Equal("BGRA32", rendered.PixelFormat);
            Assert.True(rendered.PixelWidth > 0);
            Assert.True(rendered.PixelHeight > 0);

            using (var memoryMap = MemoryMappedFile.OpenExisting(rendered.MemoryMapName, MemoryMappedFileRights.Read))
            using (var view = memoryMap.CreateViewAccessor(0, rendered.DataLength, MemoryMappedFileAccess.Read))
            {
                Assert.Equal(rendered.DataLength, view.Capacity);
            }

            await workerClient.ReleaseSharedMemoryAsync(rendered.MemoryMapName, CancellationToken.None);

            var pageText = await workerClient.GetPageTextAsync(document.DocumentId, 0, CancellationToken.None);
            Assert.Contains("Local PDF Reader", pageText.RawText);
            Assert.NotEmpty(pageText.Glyphs);
            var hit = await workerClient.HitTestTextAsync(
                document.DocumentId,
                0,
                new PdfPoint(75, 725),
                tolerance: 8,
                CancellationToken.None);
            Assert.NotNull(hit);

            await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
            await workerClient.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task FixedPdfCorpusOpensRendersExtractsTextAndRejectsDamage()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader 固定文档集 {Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var portraitPath = Path.Combine(directoryPath, "纵向 示例.pdf");
        var landscapePath = Path.Combine(directoryPath, "landscape.pdf");
        var multiPagePath = Path.Combine(directoryPath, "multi-page.pdf");
        var multilinePath = Path.Combine(directoryPath, "multiline-hyphen.pdf");
        var noTextPath = Path.Combine(directoryPath, "no-text-layer.pdf");
        var readOnlyPath = Path.Combine(directoryPath, "read-only.pdf");
        var damagedPath = Path.Combine(directoryPath, "damaged.pdf");

        File.WriteAllBytes(portraitPath, PdfTestDocumentFactory.Create(
            new PdfTestPage(595, 842, "Portrait corpus text")));
        File.WriteAllBytes(landscapePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(842, 595, "Landscape corpus text")));
        File.WriteAllBytes(multiPagePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Corpus page one"),
            new PdfTestPage(612, 792, "Corpus page two"),
            new PdfTestPage(612, 792, "Corpus page three")));
        File.WriteAllBytes(multilinePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "An inter-", "national multi-line paragraph.")));
        File.WriteAllBytes(noTextPath, PdfTestDocumentFactory.Create(new PdfTestPage(612, 792)));
        File.WriteAllBytes(readOnlyPath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Read-only corpus text")));
        File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
        File.WriteAllBytes(damagedPath, PdfTestDocumentFactory.CreateDamaged());

        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe");
            await using var workerClient = new PdfWorkerClient(workerPath, NullLogger<PdfWorkerClient>.Instance);
            await workerClient.StartAsync(CancellationToken.None);

            await ValidateCorpusDocumentAsync(workerClient, portraitPath, 1, new PdfSize(595, 842), "Portrait corpus text", true);
            await ValidateCorpusDocumentAsync(workerClient, landscapePath, 1, new PdfSize(842, 595), "Landscape corpus text", true);
            await ValidateCorpusDocumentAsync(workerClient, multiPagePath, 3, new PdfSize(612, 792), "Corpus page one", true);
            await ValidateCorpusDocumentAsync(workerClient, multilinePath, 1, new PdfSize(612, 792), "international", true);
            await ValidateCorpusDocumentAsync(workerClient, noTextPath, 1, new PdfSize(612, 792), string.Empty, false);
            await ValidateCorpusDocumentAsync(workerClient, readOnlyPath, 1, new PdfSize(612, 792), "Read-only corpus text", true);

            await Assert.ThrowsAsync<PdfWorkerException>(() =>
                workerClient.OpenDocumentAsync(damagedPath, password: null, CancellationToken.None));
            await workerClient.PingAsync(CancellationToken.None);
            await workerClient.StopAsync(CancellationToken.None);
        }
        finally
        {
            File.SetAttributes(readOnlyPath, FileAttributes.Normal);
            await DeleteDirectoryWithRetryAsync(directoryPath);
        }
    }

    [Fact]
    public async Task FixedPortraitDocumentRendersAllFourRotations()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Rotation.{Guid.NewGuid():N}");
        var pdfPath = Path.Combine(directoryPath, "rotation.pdf");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(pdfPath, PdfTestDocumentFactory.Create(
            new PdfTestPage(300, 500, "Rotation corpus text")));

        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe");
            await using var workerClient = new PdfWorkerClient(workerPath, NullLogger<PdfWorkerClient>.Instance);
            await workerClient.StartAsync(CancellationToken.None);
            var document = await workerClient.OpenDocumentAsync(pdfPath, password: null, CancellationToken.None);

            foreach (var rotation in Enum.GetValues<PageRotation>())
            {
                var descriptor = await workerClient.RenderPageAsync(
                    new PageRenderRequest(Guid.NewGuid(), document.DocumentId, 0, 1, rotation, 1, 1, RenderQuality.Normal),
                    CancellationToken.None);
                var quarterTurn = rotation is PageRotation.Rotate90 or PageRotation.Rotate270;
                Assert.Equal(quarterTurn ? 667 : 400, descriptor.PixelWidth);
                Assert.Equal(quarterTurn ? 400 : 667, descriptor.PixelHeight);
                await workerClient.ReleaseSharedMemoryAsync(descriptor.MemoryMapName, CancellationToken.None);
            }

            await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
            await workerClient.StopAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ResponsesCompleteTheirMatchingRequestsWhenTheyArriveOutOfOrder()
    {
        var requestRegistry = new PendingRequestRegistry();
        var firstRequestId = Guid.NewGuid();
        var secondRequestId = Guid.NewGuid();
        var firstResponseTask = requestRegistry.Register(firstRequestId, CancellationToken.None);
        var secondResponseTask = requestRegistry.Register(secondRequestId, CancellationToken.None);
        var firstResponse = CreateResponse(firstRequestId);
        var secondResponse = CreateResponse(secondRequestId);

        Assert.True(requestRegistry.TryComplete(secondResponse));
        Assert.True(requestRegistry.TryComplete(firstResponse));

        Assert.Equal(firstResponse, await firstResponseTask);
        Assert.Equal(secondResponse, await secondResponseTask);
    }

    private static LocalPdfReader.PdfProtocol.PipeMessageEnvelope CreateResponse(Guid requestId) => new(
        LocalPdfReader.PdfProtocol.PdfWorkerProtocol.CurrentVersion,
        LocalPdfReader.PdfProtocol.PipeMessageTypes.HandshakeResponse,
        requestId,
        null,
        LocalPdfReader.PdfProtocol.PipeMessageSerializer.SerializePayload(
            new LocalPdfReader.PdfProtocol.HandshakeResponse(true, LocalPdfReader.PdfProtocol.PdfWorkerProtocol.CurrentVersion)));

    private static async Task ValidateCorpusDocumentAsync(
        PdfWorkerClient workerClient,
        string filePath,
        int expectedPageCount,
        PdfSize expectedPageSize,
        string expectedNormalizedText,
        bool expectedTextLayer)
    {
        var document = await workerClient.OpenDocumentAsync(filePath, password: null, CancellationToken.None);
        Assert.Equal(Path.GetFileName(filePath), document.FileName);
        Assert.Equal(expectedPageCount, document.PageCount);
        Assert.Equal(expectedTextLayer, document.HasTextLayer);

        var descriptor = await workerClient.RenderPageAsync(
            new PageRenderRequest(Guid.NewGuid(), document.DocumentId, 0, 0.5, PageRotation.Rotate0, 1, 1, RenderQuality.Normal),
            CancellationToken.None);
        Assert.Equal(expectedPageSize, descriptor.OriginalPageSize);
        Assert.Equal("BGRA32", descriptor.PixelFormat);
        Assert.True(descriptor.DataLength > 0);
        await workerClient.ReleaseSharedMemoryAsync(descriptor.MemoryMapName, CancellationToken.None);

        var pageText = await workerClient.GetPageTextAsync(document.DocumentId, 0, CancellationToken.None);
        if (expectedNormalizedText.Length == 0)
        {
            Assert.Empty(pageText.RawText);
            Assert.Empty(pageText.Glyphs);
        }
        else if (expectedNormalizedText == "international")
        {
            var selectionService = new LocalPdfReader.Application.Reader.TextSelectionService();
            var selection = selectionService.CreateSelection(pageText, 0, pageText.Glyphs.Count - 1);
            Assert.NotNull(selection);
            Assert.Contains(expectedNormalizedText, selection.NormalizedText, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(expectedNormalizedText, pageText.RawText);
            Assert.NotEmpty(pageText.Glyphs);
        }

        await workerClient.CloseDocumentAsync(document.DocumentId, CancellationToken.None);
    }

    private static async Task DeleteDirectoryWithRetryAsync(string directoryPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
            }
        }
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string expectedSecret) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.deepseek.com/models", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedSecret, request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
