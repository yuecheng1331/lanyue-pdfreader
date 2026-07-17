using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using LocalPdfReader.Application.Annotations;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Application.Metadata;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Infrastructure.Configuration;
using LocalPdfReader.Infrastructure.Diagnostics;
using LocalPdfReader.Infrastructure.Metadata;
using LocalPdfReader.Infrastructure.Persistence;
using LocalPdfReader.Infrastructure.PdfWorker;
using LocalPdfReader.Infrastructure.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.App;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _heartbeatCancellationSource;
    private Task? _heartbeatTask;
    private bool _isClosing;
    private bool _isClosePromptPending;
    private int _workerRestartInProgress;
    private Task? _workerRestartTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _serviceProvider = ConfigureServices();
        var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        IPdfWorkerClient? pdfWorkerClient = null;

        try
        {
            pdfWorkerClient = _serviceProvider.GetRequiredService<IPdfWorkerClient>();
            await pdfWorkerClient.StartAsync(CancellationToken.None);

            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync(CancellationToken.None);
            var localDataStore = _serviceProvider.GetRequiredService<ILocalDataStore>();
            try
            {
                await localDataStore.InitializeAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                var warning = _serviceProvider.GetRequiredService<IUserErrorService>().Report(
                    UserErrorCode.DatabaseUnavailable,
                    exception);
                MessageBox.Show(
                    warning.DialogText,
                    warning.Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            StartHeartbeat();

            logger.LogInformation(new EventId(1000, "ApplicationStarted"), "Local PDF Reader started.");

            MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            pdfWorkerClient.Disconnected += OnPdfWorkerDisconnected;
            MainWindow.Closing += OnMainWindowClosing;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            var userError = _serviceProvider.GetRequiredService<IUserErrorService>().Report(
                UserErrorCode.AppStartFailed,
                exception);
            MessageBox.Show(
                userError.DialogText,
                userError.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await StopHeartbeatAsync();

            if (pdfWorkerClient is not null)
            {
                await pdfWorkerClient.StopAsync(CancellationToken.None);
            }

            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosePromptPending)
        {
            return;
        }

        _isClosePromptPending = true;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
            await CloseMainWindowAfterPromptAsync(sender as Window)));
    }

    private async Task CloseMainWindowAfterPromptAsync(Window? owner)
    {
        var readerForPrompt = _serviceProvider?.GetRequiredService<ReaderViewModel>();
        if (readerForPrompt is not null &&
            !await ConfirmSavePendingAnnotationsBeforeCloseAsync(owner, readerForPrompt))
        {
            _isClosePromptPending = false;
            return;
        }

        _isClosing = true;

        try
        {
            await StopHeartbeatAsync();
            if (_workerRestartTask is not null)
            {
                await _workerRestartTask;
            }

            var translationPanel = _serviceProvider?.GetRequiredService<TranslationPanelViewModel>();
            if (translationPanel is not null)
            {
                await translationPanel.CancelAsync();
            }

            var reader = _serviceProvider?.GetRequiredService<ReaderViewModel>();
            if (reader is not null)
            {
                await reader.ShutdownAsync(CancellationToken.None);
            }

            var workerClient = _serviceProvider?.GetRequiredService<IPdfWorkerClient>();

            if (workerClient is not null)
            {
                workerClient.Disconnected -= OnPdfWorkerDisconnected;
                await workerClient.StopAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _serviceProvider?.GetRequiredService<ILogger<App>>().LogError(exception, "The PDF worker could not be stopped cleanly.");
        }
        finally
        {
            _isClosePromptPending = false;
            MainWindow.Closing -= OnMainWindowClosing;
            MainWindow.Close();
            Shutdown();
        }
    }

    private static async Task<bool> ConfirmSavePendingAnnotationsBeforeCloseAsync(
        Window? owner,
        ReaderViewModel reader)
    {
        if (!reader.HasPendingLocalAnnotationChanges)
        {
            return true;
        }

        var result = owner is null
            ? MessageBox.Show(
                "当前有尚未保存到本地数据库的批注颜色或笔记修改。是否在关闭前保存？\n\n是：保存后关闭\n否：不保存并关闭\n取消：返回阅读",
                "保存未保存的批注",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question)
            : MessageBox.Show(
                owner,
                "当前有尚未保存到本地数据库的批注颜色或笔记修改。是否在关闭前保存？\n\n是：保存后关闭\n否：不保存并关闭\n取消：返回阅读",
                "保存未保存的批注",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.No)
        {
            return true;
        }

        var saved = await reader.SaveAllPendingLocalAnnotationChangesAsync(CancellationToken.None);
        if (saved)
        {
            return true;
        }

        if (owner is null)
        {
            MessageBox.Show(
                "未保存的批注修改没有成功写入本地数据库，程序将保持打开。",
                "保存批注失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(
                owner,
                "未保存的批注修改没有成功写入本地数据库，程序将保持打开。",
                "保存批注失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private void StartHeartbeat()
    {
        _heartbeatCancellationSource = new CancellationTokenSource();
        _heartbeatTask = MonitorWorkerHeartbeatAsync(_heartbeatCancellationSource.Token);
    }

    private void OnPdfWorkerDisconnected(object? sender, PdfWorkerDisconnectedEventArgs e)
    {
        if (_isClosing || Interlocked.Exchange(ref _workerRestartInProgress, 1) != 0)
        {
            return;
        }

        _workerRestartTask = RestartWorkerAfterDisconnectAsync(e);
    }

    private async Task RestartWorkerAfterDisconnectAsync(PdfWorkerDisconnectedEventArgs eventArgs)
    {
        var logger = _serviceProvider?.GetService<ILogger<App>>();
        var workerClient = _serviceProvider?.GetService<IPdfWorkerClient>();

        if (logger is null || workerClient is null)
        {
            Interlocked.Exchange(ref _workerRestartInProgress, 0);
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                var reader = _serviceProvider?.GetService<ReaderViewModel>();
                reader?.HandleWorkerDisconnected(eventArgs);
                reader?.HandleWorkerRestarting();
            });

            await StopHeartbeatAsync();
            await workerClient.StopAsync(CancellationToken.None);

            if (_isClosing)
            {
                return;
            }

            await workerClient.StartAsync(CancellationToken.None);
            await workerClient.PingAsync(CancellationToken.None);

            logger.LogInformation(
                new EventId(1004, "PdfWorkerRestartSucceeded"),
                "PDF worker restarted and passed its connection check.");
            var recoveryTask = await Dispatcher.InvokeAsync(() =>
            {
                var reader = _serviceProvider?.GetService<ReaderViewModel>();
                return reader is null
                    ? Task.CompletedTask
                    : reader.RecoverDocumentAfterWorkerRestartAsync(CancellationToken.None);
            });
            await recoveryTask;
            StartHeartbeat();
        }
        catch (Exception exception)
        {
            var userError = _serviceProvider!.GetRequiredService<IUserErrorService>().Report(
                UserErrorCode.WorkerRestartFailed,
                exception);
            await Dispatcher.InvokeAsync(() =>
                _serviceProvider?.GetService<ReaderViewModel>()?.HandleWorkerRestartFailed(userError));
        }
        finally
        {
            Interlocked.Exchange(ref _workerRestartInProgress, 0);
        }
    }

    private async Task StopHeartbeatAsync()
    {
        var cancellationSource = _heartbeatCancellationSource;
        var heartbeatTask = _heartbeatTask;
        _heartbeatCancellationSource = null;
        _heartbeatTask = null;

        cancellationSource?.Cancel();

        if (heartbeatTask is not null)
        {
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellationSource?.Dispose();
    }

    private async Task MonitorWorkerHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        var consecutiveFailures = 0;
        var logger = _serviceProvider?.GetRequiredService<ILogger<App>>();
        var workerClient = _serviceProvider?.GetRequiredService<IPdfWorkerClient>();

        if (logger is null || workerClient is null)
        {
            return;
        }

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await workerClient.PingAsync(cancellationToken);

                if (consecutiveFailures > 0)
                {
                    logger.LogInformation(new EventId(1003, "PdfWorkerHeartbeatRecovered"), "PDF worker heartbeat recovered.");
                }

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                var logLevel = consecutiveFailures >= 2 ? LogLevel.Error : LogLevel.Warning;
                logger.Log(logLevel, new EventId(1002, "PdfWorkerHeartbeatFailed"), exception, "PDF worker heartbeat failed {FailureCount} consecutive time(s).", consecutiveFailures);
            }
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalPdfReader");
        var logDirectory = Path.Combine(appDataDirectory, "logs");

        services.AddSingleton<ISettingsService>(serviceProvider => new JsonSettingsService(
            Path.Combine(appDataDirectory, "settings.json"),
            serviceProvider.GetRequiredService<ILogger<JsonSettingsService>>()));
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IApplicationInfoProvider>(new ApplicationInfoProvider(
            typeof(App).Assembly,
            AppContext.BaseDirectory,
            appDataDirectory,
            logDirectory));
        services.AddSingleton<SqliteLocalDatabase>(serviceProvider => new SqliteLocalDatabase(
            Path.Combine(appDataDirectory, "data", "reader.db"),
            serviceProvider.GetRequiredService<IApplicationInfoProvider>().GetApplicationInfo().ProductVersion,
            serviceProvider.GetRequiredService<ILogger<SqliteLocalDatabase>>()));
        services.AddSingleton<ILocalDataStore>(serviceProvider =>
            serviceProvider.GetRequiredService<SqliteLocalDatabase>());
        services.AddSingleton<IDocumentHistoryRepository, SqliteDocumentHistoryRepository>();
        services.AddSingleton<IReadingStateRepository, SqliteReadingStateRepository>();
        services.AddSingleton<IDocumentSessionRepository, SqliteDocumentSessionRepository>();
        services.AddSingleton<IAnnotationRepository, SqliteAnnotationRepository>();
        services.AddSingleton<ITranslationMemoryRepository, SqliteTranslationMemoryRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAnnotationService, AnnotationService>();
        services.AddSingleton<IPdfAnnotationSyncService, PdfAnnotationSyncService>();
        services.AddSingleton<IDocumentFingerprintService, Sha256DocumentFingerprintService>();
        services.AddSingleton<DocumentHistoryService>();
        services.AddSingleton<DocumentSessionService>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<ITranslationConnectionTester>(serviceProvider => new DeepSeekConnectionTester(
            serviceProvider.GetRequiredService<HttpClient>(),
            serviceProvider.GetRequiredService<ILogger<DeepSeekConnectionTester>>()));
        services.AddSingleton<ITranslationProvider>(serviceProvider => new DeepSeekTranslationProvider(
            serviceProvider.GetRequiredService<HttpClient>(),
            serviceProvider.GetRequiredService<ILogger<DeepSeekTranslationProvider>>()));
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<IWordTranslationService, LocalWordTranslationService>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IUserErrorService, UserErrorService>();
        services.AddSingleton<TranslationPanelViewModel>();
        services.AddSingleton<IPdfWorkerClient>(serviceProvider => new PdfWorkerClient(
            Path.Combine(AppContext.BaseDirectory, "LocalPdfReader.PdfWorker.exe"),
            serviceProvider.GetRequiredService<ILogger<PdfWorkerClient>>()));
        services.AddSingleton<ReaderState>();
        services.AddSingleton<ICoordinateTransformer, CoordinateTransformer>();
        services.AddSingleton<TextSelectionService>();
        services.AddSingleton<ReaderViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new LocalFileLoggerProvider(logDirectory));
        });

        return services.BuildServiceProvider();
    }
}
