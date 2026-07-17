using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using LocalPdfReader.App;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public sealed class MultiDocumentTabTests
{
    [Fact]
    public async Task MultipleDocumentsRemainOpenAndKeepIndependentViewAndTranslationState()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        var firstPath = Path.GetFullPath("first-tab.pdf");
        var secondPath = Path.GetFullPath("second-tab.pdf");

        await reader.OpenDocumentAsync(firstPath, CancellationToken.None);
        var firstTab = Assert.Single(reader.DocumentTabs);
        firstTab.ReaderState.SetPageNumber(2);
        firstTab.ReaderState.SetZoomFactor(1.5);
        reader.UpdateScrollOffsets(12, 240);
        reader.Translation.SourceText = "first source";
        reader.Translation.TranslatedText = "first translation";

        await reader.OpenDocumentAsync(secondPath, CancellationToken.None);
        var secondTab = Assert.Single(reader.DocumentTabs, tab => tab.FullPath == secondPath);
        reader.Translation.SourceText = "second source";
        reader.Translation.TranslatedText = "second translation";

        Assert.Equal(2, reader.DocumentTabs.Count);
        Assert.Same(secondTab, reader.ActiveDocumentTab);
        Assert.Empty(worker.ClosedDocumentIds);
        Assert.Equal(2, worker.OpenedPaths.Count);

        await reader.ActivateDocumentTabAsync(firstTab, CancellationToken.None);

        Assert.Equal(1, firstTab.ReaderState.CurrentPageIndex);
        Assert.Equal(1.5, firstTab.ReaderState.ZoomFactor);
        Assert.Equal(12, reader.HorizontalScrollOffset);
        Assert.Equal(240, reader.VerticalScrollOffset);
        Assert.Equal("first source", reader.Translation.SourceText);
        Assert.Equal("first translation", reader.Translation.TranslatedText);

        await reader.ActivateDocumentTabAsync(secondTab, CancellationToken.None);

        Assert.Equal("second source", reader.Translation.SourceText);
        Assert.Equal("second translation", reader.Translation.TranslatedText);
    }

    [Fact]
    public async Task ClosingOneTabReleasesOnlyItsDocumentAndClosingLastTabReturnsHome()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("first-close.pdf", CancellationToken.None);
        await reader.OpenDocumentAsync("second-close.pdf", CancellationToken.None);
        var firstTab = reader.DocumentTabs[0];
        var secondTab = reader.DocumentTabs[1];
        var firstDocumentId = firstTab.ReaderState.DocumentInfo!.DocumentId;
        var secondDocumentId = secondTab.ReaderState.DocumentInfo!.DocumentId;

        await reader.CloseDocumentTabAsync(firstTab, CancellationToken.None);

        Assert.Single(reader.DocumentTabs);
        Assert.Same(secondTab, reader.ActiveDocumentTab);
        Assert.Contains(firstDocumentId, worker.ClosedDocumentIds);
        Assert.DoesNotContain(secondDocumentId, worker.ClosedDocumentIds);

        await reader.CloseDocumentTabAsync(secondTab, CancellationToken.None);

        Assert.Empty(reader.DocumentTabs);
        Assert.Null(reader.ActiveDocumentTab);
        Assert.False(reader.HasDocument);
        Assert.Null(reader.PageImage);
        Assert.Contains(secondDocumentId, worker.ClosedDocumentIds);
    }

    [Fact]
    public async Task ReopeningTheSamePathActivatesExistingTabWithoutDuplicatingWorkerDocument()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        var path = Path.GetFullPath("same-path.pdf");

        await reader.OpenDocumentAsync(path, CancellationToken.None);
        await reader.OpenDocumentAsync(path.ToUpperInvariant(), CancellationToken.None);

        Assert.Single(reader.DocumentTabs);
        Assert.Single(worker.OpenedPaths);
    }

    [Fact]
    public async Task RecentlyClosedTabCanBeReopenedWithoutAffectingOtherTabs()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        var directoryPath = CreateTemporaryDirectory();
        var firstPath = Path.Combine(directoryPath, "first-reopen.pdf");
        var secondPath = Path.Combine(directoryPath, "second-reopen.pdf");
        await File.WriteAllTextAsync(firstPath, "test");
        await File.WriteAllTextAsync(secondPath, "test");

        try
        {
            await reader.OpenDocumentAsync(firstPath, CancellationToken.None);
            await reader.OpenDocumentAsync(secondPath, CancellationToken.None);
            var firstTab = reader.DocumentTabs[0];
            var secondTab = reader.DocumentTabs[1];

            await reader.CloseDocumentTabAsync(firstTab, CancellationToken.None);

            Assert.True(reader.CanReopenClosedTab);
            Assert.Single(reader.RecentlyClosedTabs);
            Assert.Same(secondTab, reader.ActiveDocumentTab);

            await reader.ReopenLastClosedTabAsync(CancellationToken.None);

            Assert.Equal(2, reader.DocumentTabs.Count);
            Assert.Equal("first-reopen.pdf", reader.ActiveDocumentTab!.FileName);
            Assert.False(reader.CanReopenClosedTab);
            Assert.Equal(3, worker.OpenedPaths.Count);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task MissingClosedTabIsDiscardedSoTheNextClosedTabCanBeRecovered()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        var directoryPath = CreateTemporaryDirectory();
        var recoverablePath = Path.Combine(directoryPath, "recoverable.pdf");
        var missingPath = Path.Combine(directoryPath, "missing.pdf");
        await File.WriteAllTextAsync(recoverablePath, "test");
        await File.WriteAllTextAsync(missingPath, "test");

        try
        {
            await reader.OpenDocumentAsync(recoverablePath, CancellationToken.None);
            await reader.OpenDocumentAsync(missingPath, CancellationToken.None);
            var recoverableTab = reader.DocumentTabs[0];
            var missingTab = reader.DocumentTabs[1];
            await reader.CloseDocumentTabAsync(recoverableTab, CancellationToken.None);
            await reader.CloseDocumentTabAsync(missingTab, CancellationToken.None);
            File.Delete(missingPath);

            await reader.ReopenLastClosedTabAsync(CancellationToken.None);

            Assert.Single(reader.RecentlyClosedTabs);
            Assert.Contains("已跳过", reader.StatusText, StringComparison.Ordinal);

            await reader.ReopenLastClosedTabAsync(CancellationToken.None);

            Assert.Equal("recoverable.pdf", reader.ActiveDocumentTab!.FileName);
            Assert.False(reader.CanReopenClosedTab);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerRestartRestoresEveryOpenTabAndKeepsTheActiveTab()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("first-recovery.pdf", CancellationToken.None);
        var firstTab = reader.DocumentTabs[0];
        await reader.OpenDocumentAsync("second-recovery.pdf", CancellationToken.None);
        var secondTab = reader.DocumentTabs[1];
        await reader.ActivateDocumentTabAsync(firstTab, CancellationToken.None);
        var oldDocumentIds = reader.DocumentTabs
            .Select(tab => tab.ReaderState.DocumentInfo!.DocumentId)
            .ToArray();

        reader.HandleWorkerDisconnected(new PdfWorkerDisconnectedEventArgs(
            PdfWorkerDisconnectReason.ProcessExited,
            exitCode: 1,
            exception: null));
        await reader.RecoverDocumentAfterWorkerRestartAsync(CancellationToken.None);

        Assert.Equal(2, reader.DocumentTabs.Count);
        Assert.Same(firstTab, reader.ActiveDocumentTab);
        Assert.Equal(4, worker.OpenedPaths.Count);
        Assert.DoesNotContain(firstTab.ReaderState.DocumentInfo!.DocumentId, oldDocumentIds);
        Assert.DoesNotContain(secondTab.ReaderState.DocumentInfo!.DocumentId, oldDocumentIds);
        Assert.True(reader.IsWorkerAvailable);
    }

    [Fact]
    public async Task LateRenderFromAnotherTabCannotReplaceTheActiveTab()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("first-late-render.pdf", CancellationToken.None);
        var firstTab = reader.DocumentTabs[0];
        await reader.OpenDocumentAsync("second-late-render.pdf", CancellationToken.None);
        var secondTab = reader.DocumentTabs[1];
        firstTab.ReaderState.SetZoomFactor(1.75);
        worker.BlockNextRender();

        var activateFirstTask = reader.ActivateDocumentTabAsync(firstTab, CancellationToken.None);
        await worker.BlockedRenderStarted;
        await reader.ActivateDocumentTabAsync(secondTab, CancellationToken.None);
        worker.ReleaseBlockedRender();
        await activateFirstTask;

        Assert.Same(secondTab, reader.ActiveDocumentTab);
        Assert.Contains(secondTab.FileName, reader.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryTabReceivesTheCurrentViewportForFitCommands()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);

        await reader.OpenDocumentAsync("first-fit.pdf", CancellationToken.None);
        var firstTab = reader.DocumentTabs[0];
        Assert.True(reader.CanFitPage);

        await reader.OpenDocumentAsync("second-fit.pdf", CancellationToken.None);
        Assert.True(reader.CanFitPage);

        await reader.ActivateDocumentTabAsync(firstTab, CancellationToken.None);
        Assert.True(reader.CanFitPage);
    }

    [Fact]
    public async Task OpenDocumentCreatesAContinuousSinglePageCanvas()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);

        await reader.OpenDocumentAsync("continuous-open.pdf", CancellationToken.None);

        Assert.Equal(5, reader.DocumentPages.Count);
        Assert.True(reader.DocumentPages[0].HasImage);
        Assert.True(reader.DocumentPages[0].IsCurrentPage);
        Assert.True(reader.DocumentPages[1].Bounds.Y > reader.DocumentPages[0].Bounds.Y);
    }

    [Fact]
    public async Task ViewportResizeAfterInitialOpenRecentersThePageCanvas()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);

        await reader.OpenDocumentAsync("initial-viewport-layout.pdf", CancellationToken.None);
        Assert.Equal(0, reader.DocumentPages[0].DisplayLeft);

        reader.UpdateViewportSize(1200, 650);

        Assert.True(reader.DocumentCanvasWidth >= 1200);
        Assert.True(reader.DocumentPages[0].DisplayLeft > 0);
    }

    [Fact]
    public async Task ViewportResizeAfterInitialOpenRendersVisiblePages()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);

        await reader.OpenDocumentAsync("initial-viewport-render.pdf", CancellationToken.None);
        Assert.True(reader.DocumentPages[0].HasImage);
        Assert.False(reader.DocumentPages[1].HasImage);

        reader.UpdateViewportSize(900, 650);

        await WaitUntilAsync(() => reader.DocumentPages[1].HasImage);
    }

    [Fact]
    public async Task ContinuousScrollChangesCurrentPageAndRendersVisiblePlaceholder()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("continuous-scroll.pdf", CancellationToken.None);
        var secondPageTop = reader.DocumentPages[1].Bounds.Y;

        reader.UpdateScrollOffsets(0, secondPageTop + 20);

        await WaitUntilAsync(() => reader.DocumentPages[1].HasImage);
        Assert.Equal(1, reader.ActiveDocumentTab!.ReaderState.CurrentPageIndex);
        Assert.Equal("2", reader.PageNumberText);
        Assert.True(reader.DocumentPages[1].IsCurrentPage);
    }

    [Fact]
    public async Task PageNavigationRequestsScrollToTheTargetPage()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("page-button-navigation.pdf", CancellationToken.None);
        ScrollOffsetRequestedEventArgs? requestedScroll = null;
        reader.ScrollOffsetRequested += (_, args) => requestedScroll = args;

        reader.NextPageCommand.Execute(null);

        await WaitUntilAsync(() => requestedScroll is not null);
        Assert.Equal(1, reader.ActiveDocumentTab!.ReaderState.CurrentPageIndex);
        Assert.True(requestedScroll!.VerticalOffset > 0);

        requestedScroll = null;
        reader.PageNumberText = "4";
        reader.GoToPageCommand.Execute(null);

        await WaitUntilAsync(() => requestedScroll is not null);
        Assert.Equal(3, reader.ActiveDocumentTab.ReaderState.CurrentPageIndex);
        Assert.True(requestedScroll!.VerticalOffset > reader.DocumentPages[1].Bounds.Y);
    }

    [Fact]
    public async Task RotatingContinuousPageRendersANewRotatedBitmap()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("continuous-rotate.pdf", CancellationToken.None);
        var renderCountBeforeRotate = worker.RenderRequests.Count;

        reader.RotateClockwiseCommand.Execute(null);

        await WaitUntilAsync(() =>
            worker.RenderRequests.Count > renderCountBeforeRotate &&
            !reader.IsRendering &&
            reader.DocumentPages[0].HasImage);
        Assert.Contains(worker.RenderRequests, request => request.Rotation == PageRotation.Rotate90);
        Assert.True(reader.DocumentPages[0].DisplayWidth > reader.DocumentPages[0].DisplayHeight);
    }

    [Fact]
    public async Task DoublePageModeArrangesPagesInVerticalSpreads()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("double-page-layout.pdf", CancellationToken.None);

        reader.DoublePageModeCommand.Execute(null);

        Assert.True(reader.IsDoublePageMode);
        Assert.Equal(reader.DocumentPages[0].DisplayTop, reader.DocumentPages[1].DisplayTop, precision: 3);
        Assert.True(reader.DocumentPages[1].DisplayLeft > reader.DocumentPages[0].DisplayLeft);
        Assert.True(reader.DocumentPages[2].DisplayTop > reader.DocumentPages[0].DisplayTop);
    }

    [Fact]
    public async Task FitPageInDoublePageModeUsesTheWholeSpread()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("double-page-fit.pdf", CancellationToken.None);

        reader.DoublePageModeCommand.Execute(null);
        Assert.True(reader.IsDoublePageMode);
        Assert.Equal(0, reader.ActiveDocumentTab!.ReaderState.CurrentPageIndex);
        Assert.Equal(5, reader.DocumentPages.Count);
        reader.FitPageCommand.Execute(null);

        await WaitUntilAsync(() => !reader.IsRendering);
        Assert.Equal(ReaderZoomMode.FitPage, reader.ActiveDocumentTab!.ReaderState.ZoomMode);
        Assert.Equal(0.546, reader.ActiveDocumentTab.ReaderState.ZoomFactor, precision: 3);
    }

    [Fact]
    public async Task DoublePageSelectionUsesClickedPageWithoutChangingReadingState()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("double-page-selection.pdf", CancellationToken.None);
        reader.DoublePageModeCommand.Execute(null);

        await reader.BeginSelectionAsync(1, new ViewPoint(16, 45), CancellationToken.None);
        reader.UpdateSelection(new ViewPoint(70, 45));
        reader.CompleteSelection();

        var selection = Assert.IsType<TextSelection>(reader.CurrentSelection);
        Assert.Equal(1, selection.PageIndex);
        Assert.Equal(0, reader.ActiveDocumentTab!.ReaderState.CurrentPageIndex);
        Assert.True(reader.DocumentPages[0].IsCurrentPage);
        Assert.True(reader.DocumentPages[1].IsSelectionPage);
        Assert.NotEmpty(reader.SelectionRectangles);
        Assert.False(reader.IsSelectionToolbarVisible);

        reader.ToggleSelectionToolbar(1, new ViewPoint(70, 45));
        Assert.True(reader.IsSelectionToolbarVisible);

        reader.ToggleSelectionToolbar(1, new ViewPoint(70, 45));
        Assert.False(reader.IsSelectionToolbarVisible);
        reader.Translation.SourceText = "manually kept source";
        reader.Translation.TranslatedText = "manually kept translation";

        reader.HideSelectionVisualsPreservingTranslation();

        Assert.Null(reader.CurrentSelection);
        Assert.Empty(reader.SelectionRectangles);
        Assert.False(reader.DocumentPages[1].IsSelectionPage);
        Assert.Equal("manually kept source", reader.Translation.SourceText);
        Assert.Equal("manually kept translation", reader.Translation.TranslatedText);
    }

    [Fact]
    public async Task MouseWheelZoomChangesZoomAroundReaderState()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("mouse-wheel-zoom.pdf", CancellationToken.None);
        var oldZoom = reader.ZoomFactor;

        await reader.ZoomByMouseWheelAsync(120);

        Assert.True(reader.ZoomFactor > oldZoom);
        Assert.Equal(ReaderZoomMode.ActualZoom, reader.ActiveDocumentTab!.ReaderState.ZoomMode);
    }

    [Fact]
    public async Task SelectionCanContinueAcrossPagesAfterScrolling()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("cross-page-selection.pdf", CancellationToken.None);

        await reader.BeginSelectionAsync(0, new ViewPoint(16, 45), CancellationToken.None);
        await reader.UpdateSelectionAsync(1, new ViewPoint(70, 45), CancellationToken.None);
        reader.CompleteSelection();

        var selection = Assert.IsType<TextSelection>(reader.CurrentSelection);
        Assert.Equal(0, selection.PageIndex);
        Assert.Equal("PAGE1\nPAGE2", selection.NormalizedText);
        Assert.Contains(reader.SelectionRectangles, rectangle => rectangle.PageIndex == 0);
        Assert.Contains(reader.SelectionRectangles, rectangle => rectangle.PageIndex == 1);
        Assert.NotEmpty(reader.DocumentPages[0].SelectionRectangles);
        Assert.NotEmpty(reader.DocumentPages[1].SelectionRectangles);
        Assert.True(reader.DocumentPages[0].IsSelectionPage);
        Assert.True(reader.DocumentPages[1].IsSelectionPage);
    }

    [Fact]
    public async Task LeavingPageKeepsSelectionVisualsUntilNextSelection()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("selection-retention.pdf", CancellationToken.None);

        await reader.BeginSelectionAsync(0, new ViewPoint(16, 45), CancellationToken.None);
        reader.UpdateSelection(new ViewPoint(70, 45));
        reader.CompleteSelection();
        reader.ToggleSelectionToolbar(0, new ViewPoint(70, 45));

        reader.HideSelectionToolbarPreservingSelection();

        Assert.NotNull(reader.CurrentSelection);
        Assert.NotEmpty(reader.SelectionRectangles);
        Assert.True(reader.DocumentPages[0].IsSelectionPage);
        Assert.False(reader.IsSelectionToolbarVisible);
    }

    [Fact]
    public async Task SearchPanelUsesCurrentSelectionAsQuery()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        reader.UpdateViewportSize(900, 650);
        await reader.OpenDocumentAsync("selection-search.pdf", CancellationToken.None);

        await reader.BeginSelectionAsync(0, new ViewPoint(16, 45), CancellationToken.None);
        reader.UpdateSelection(new ViewPoint(70, 45));
        reader.CompleteSelection();

        await reader.OpenSearchPanelFromSelectionAsync();

        Assert.Equal(2, reader.LeftSidebarIndex);
        Assert.True(reader.IsLeftSidebarOpen);
        Assert.Equal("PAGE1", reader.ActiveDocumentTab!.Search.Query);
        Assert.Single(reader.ActiveDocumentTab.Search.Results);
    }

    [Fact]
    public async Task TabsCanBeReorderedWithoutChangingTheActiveDocument()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("first-order.pdf", CancellationToken.None);
        await reader.OpenDocumentAsync("second-order.pdf", CancellationToken.None);
        await reader.OpenDocumentAsync("third-order.pdf", CancellationToken.None);
        var firstTab = reader.DocumentTabs[0];
        var secondTab = reader.DocumentTabs[1];
        var thirdTab = reader.DocumentTabs[2];

        Assert.True(reader.MoveDocumentTab(thirdTab, firstTab));

        Assert.Equal([thirdTab, firstTab, secondTab], reader.DocumentTabs);
        Assert.Same(thirdTab, reader.ActiveDocumentTab);
        Assert.False(reader.MoveDocumentTab(thirdTab, thirdTab));
    }

    [Fact]
    public async Task SearchResultNavigationChangesPageAndRestoresHighlightWithItsTab()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("first-search-tab.pdf", CancellationToken.None);
        var firstTab = reader.DocumentTabs[0];
        firstTab.Search.Query = "pdf";
        await firstTab.Search.StartSearchAsync();
        var result = Assert.Single(firstTab.Search.Results);

        await reader.NavigateToSearchResultAsync(result, CancellationToken.None);

        Assert.Equal(2, firstTab.ReaderState.CurrentPageIndex);
        Assert.Equal("3", reader.PageNumberText);
        Assert.NotEmpty(reader.SearchHighlightRectangles);
        Assert.Contains("第 3 页", reader.StatusText, StringComparison.Ordinal);

        await reader.OpenDocumentAsync("second-search-tab.pdf", CancellationToken.None);
        Assert.Empty(reader.SearchHighlightRectangles);

        await reader.ActivateDocumentTabAsync(firstTab, CancellationToken.None);
        Assert.NotEmpty(reader.SearchHighlightRectangles);
    }

    [Fact]
    public async Task ClosingLeftSidebarClearsSelectedSearchHighlight()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("search-sidebar-close.pdf", CancellationToken.None);
        var tab = reader.ActiveDocumentTab!;
        tab.Search.Query = "pdf";
        await tab.Search.StartSearchAsync();
        var result = Assert.Single(tab.Search.Results);

        await reader.NavigateToSearchResultAsync(result, CancellationToken.None);
        Assert.NotEmpty(reader.SearchHighlightRectangles);

        reader.IsLeftSidebarOpen = false;

        Assert.Null(tab.Search.SelectedResult);
        Assert.Empty(reader.SearchHighlightRectangles);
        Assert.Equal("pdf", tab.Search.Query);
        Assert.Single(tab.Search.Results);
    }

    [Fact]
    public async Task FailureAfterWorkerOpenRollsBackTheTabAndWorkerDocument()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        var throwOnce = true;
        reader.PropertyChanged += (_, args) =>
        {
            if (throwOnce &&
                args.PropertyName == nameof(ReaderViewModel.ActiveDocumentTab) &&
                reader.ActiveDocumentTab is not null)
            {
                throwOnce = false;
                throw new InvalidOperationException("Simulated UI activation failure.");
            }
        };

        await reader.OpenDocumentAsync("failed-after-open.pdf", CancellationToken.None);

        Assert.Empty(reader.DocumentTabs);
        Assert.Null(reader.ActiveDocumentTab);
        Assert.False(reader.HasDocument);
        Assert.Single(worker.OpenedPaths);
        Assert.Single(worker.ClosedDocumentIds);
        Assert.Contains("PDF_OPEN_FAILED", reader.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchProgressBindingsNeverWriteBackToReadOnlyViewModelProperties()
    {
        Exception? threadException = null;
        BindingMode? maximumMode = null;
        BindingMode? valueMode = null;
        var thread = new Thread(() =>
        {
            try
            {
                var worker = new MultiDocumentWorkerClient();
                var window = new MainWindow(CreateReader(worker), null!, null!);
                var progressBar = Assert.IsType<ProgressBar>(window.FindName("SearchProgressBar"));
                maximumMode = BindingOperations.GetBinding(progressBar, RangeBase.MaximumProperty)?.Mode;
                valueMode = BindingOperations.GetBinding(progressBar, RangeBase.ValueProperty)?.Mode;
                window.Close();
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadException is not null)
        {
            ExceptionDispatchInfo.Capture(threadException).Throw();
        }

        Assert.Equal(BindingMode.OneWay, maximumMode);
        Assert.Equal(BindingMode.OneWay, valueMode);
    }

    [Fact]
    public async Task FirstPageRenderFailureRollsBackTheNewTabAndWorkerDocument()
    {
        await using var worker = new MultiDocumentWorkerClient();
        worker.FailNextRender();
        var reader = CreateReader(worker);

        await reader.OpenDocumentAsync("first-render-failure.pdf", CancellationToken.None);

        Assert.Empty(reader.DocumentTabs);
        Assert.Null(reader.ActiveDocumentTab);
        Assert.False(reader.HasDocument);
        Assert.Single(worker.ClosedDocumentIds);
        Assert.Contains("PDF_OPEN_FAILED", reader.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSecondOpenRestoresThePreviouslyActiveTab()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        await reader.OpenDocumentAsync("existing-before-failure.pdf", CancellationToken.None);
        var existingTab = Assert.Single(reader.DocumentTabs);
        var throwOnce = true;
        reader.PropertyChanged += (_, args) =>
        {
            if (throwOnce &&
                args.PropertyName == nameof(ReaderViewModel.ActiveDocumentTab) &&
                reader.ActiveDocumentTab is { FileName: "second-open-failure.pdf" })
            {
                throwOnce = false;
                throw new InvalidOperationException("Simulated second-tab activation failure.");
            }
        };

        await reader.OpenDocumentAsync("second-open-failure.pdf", CancellationToken.None);

        Assert.Single(reader.DocumentTabs);
        Assert.Same(existingTab, reader.ActiveDocumentTab);
        Assert.True(reader.HasDocument);
        Assert.Equal(2, worker.OpenedPaths.Count);
        Assert.Single(worker.ClosedDocumentIds);
    }

    [Fact]
    public async Task PasswordRequiredOpenReturnsResultWithoutCreatingTab()
    {
        await using var worker = new MultiDocumentWorkerClient
        {
            RequiredPassword = "secret"
        };
        var reader = CreateReader(worker);

        var missingPassword = await reader.OpenDocumentAsync("protected.pdf", password: null, CancellationToken.None);
        var opened = await reader.OpenDocumentAsync("protected.pdf", password: "secret", CancellationToken.None);

        Assert.Equal(OpenDocumentResult.PasswordRequiredOrInvalid, missingPassword);
        Assert.Equal(OpenDocumentResult.Opened, opened);
        Assert.Single(reader.DocumentTabs);
        Assert.Equal([null, "secret"], worker.OpenedPasswords);
    }

    [Fact]
    public async Task OutlineAndThumbnailNavigationMoveToTargetPage()
    {
        await using var worker = new MultiDocumentWorkerClient();
        worker.OutlineItems =
        [
            new PdfOutlineItem(
                "Chapter",
                3,
                [new PdfOutlineItem("Section", 4, [])])
        ];
        var reader = CreateReader(worker);

        await reader.OpenDocumentAsync("navigation.pdf", CancellationToken.None);
        var tab = Assert.Single(reader.DocumentTabs);
        await WaitUntilAsync(() => tab.Navigation.OutlineItems.Count == 1);

        await reader.NavigateToOutlineItemAsync(tab.Navigation.OutlineItems[0], CancellationToken.None);
        var thumbnail = tab.Navigation.Thumbnails[1];
        await reader.LoadThumbnailAsync(thumbnail, CancellationToken.None);
        await reader.NavigateToThumbnailAsync(thumbnail, CancellationToken.None);

        Assert.Equal(1, tab.ReaderState.CurrentPageIndex);
        Assert.True(thumbnail.HasImage);
        Assert.Contains(worker.RenderRequests, request =>
            request.PageIndex == 1 && request.Quality == RenderQuality.Preview);
    }

    [Fact]
    public async Task ExternalFileModificationMarksActiveTabWithoutClosingIt()
    {
        await using var worker = new MultiDocumentWorkerClient();
        var reader = CreateReader(worker);
        var directoryPath = CreateTemporaryDirectory();
        var pdfPath = Path.Combine(directoryPath, "external-change.pdf");
        await File.WriteAllTextAsync(pdfPath, "before");

        try
        {
            await reader.OpenDocumentAsync(pdfPath, CancellationToken.None);
            var tab = Assert.Single(reader.DocumentTabs);

            await File.WriteAllTextAsync(pdfPath, "after");
            File.SetLastWriteTimeUtc(pdfPath, DateTime.UtcNow.AddMinutes(1));
            reader.CheckActiveDocumentExternalModification();

            Assert.True(tab.IsExternallyModified);
            Assert.Contains("外部", tab.ExternalModificationMessage);
            Assert.Same(tab, reader.ActiveDocumentTab);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static ReaderViewModel CreateReader(MultiDocumentWorkerClient worker)
    {
        var settings = new StaticSettingsService();
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
            documentHistoryService: null,
            NullLogger<ReaderViewModel>.Instance);
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.MultiTab.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
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

    private sealed class MultiDocumentWorkerClient : IPdfWorkerClient
    {
        private readonly Dictionary<string, MemoryMappedFile> _memoryMaps = [];
        private TaskCompletionSource? _blockedRenderStarted;
        private TaskCompletionSource? _releaseBlockedRender;
        private bool _failNextRender;

        public event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected
        {
            add { }
            remove { }
        }

        public List<string> OpenedPaths { get; } = [];

        public List<string?> OpenedPasswords { get; } = [];

        public List<DocumentId> ClosedDocumentIds { get; } = [];

        public List<PageRenderRequest> RenderRequests { get; } = [];

        public string? RequiredPassword { get; init; }

        public IReadOnlyList<PdfOutlineItem> OutlineItems { get; set; } = [];

        public Task BlockedRenderStarted =>
            _blockedRenderStarted?.Task ?? Task.CompletedTask;

        public void BlockNextRender()
        {
            _blockedRenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseBlockedRender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseBlockedRender() => _releaseBlockedRender?.TrySetResult();

        public void FailNextRender() => _failNextRender = true;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PdfDocumentInfo> OpenDocumentAsync(
            string filePath,
            string? password,
            CancellationToken cancellationToken)
        {
            OpenedPaths.Add(filePath);
            OpenedPasswords.Add(password);
            if (RequiredPassword is not null && password != RequiredPassword)
            {
                throw new PdfWorkerException(
                    PdfWorkerErrorCodes.PasswordRequiredOrInvalid,
                    "Password required or invalid.");
            }

            return Task.FromResult(new PdfDocumentInfo(
                new DocumentId(Guid.NewGuid()),
                Path.GetFileName(filePath),
                5,
                false,
                true,
                null,
                null));
        }

        public Task CloseDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            ClosedDocumentIds.Add(documentId);
            return Task.CompletedTask;
        }

        public async Task<RenderedPageDescriptor> RenderPageAsync(
            PageRenderRequest request,
            CancellationToken cancellationToken)
        {
            RenderRequests.Add(request);
            if (_failNextRender)
            {
                _failNextRender = false;
                throw new InvalidOperationException("Simulated first-page render failure.");
            }

            if (_blockedRenderStarted is { } started && _releaseBlockedRender is { } release)
            {
                _blockedRenderStarted = null;
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                _releaseBlockedRender = null;
            }

            var memoryMapName = $"LocalPdfReader.MultiTabTest.{Guid.NewGuid():N}";
            var memoryMap = MemoryMappedFile.CreateNew(memoryMapName, 4);
            using (var accessor = memoryMap.CreateViewAccessor())
            {
                accessor.WriteArray(0, new byte[] { 255, 255, 255, 255 }, 0, 4);
            }

            _memoryMaps[memoryMapName] = memoryMap;
            return new RenderedPageDescriptor(
                request.RequestId,
                request.DocumentId,
                request.PageIndex,
                1,
                1,
                4,
                "BGRA32",
                memoryMapName,
                4,
                new PdfSize(612, 792));
        }

        public Task<PageTextData> GetPageTextAsync(
            DocumentId documentId,
            int pageIndex,
            CancellationToken cancellationToken)
        {
            var glyphs = new[]
            {
                new TextGlyph(0, "P", new PdfRect(10, 752, 18, 762), 0, 0),
                new TextGlyph(1, "A", new PdfRect(20, 752, 28, 762), 0, 0),
                new TextGlyph(2, "G", new PdfRect(30, 752, 38, 762), 0, 0),
                new TextGlyph(3, "E", new PdfRect(40, 752, 48, 762), 0, 0),
                new TextGlyph(4, (pageIndex + 1).ToString(), new PdfRect(50, 752, 58, 762), 0, 0)
            };
            return Task.FromResult(new PageTextData(documentId, pageIndex, $"PAGE{pageIndex + 1}", glyphs));
        }

        public Task<TextHitTestResult?> HitTestTextAsync(
            DocumentId documentId,
            int pageIndex,
            PdfPoint point,
            double tolerance,
            CancellationToken cancellationToken) => Task.FromResult<TextHitTestResult?>(null);

        public Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(
            DocumentId documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OutlineItems);

        public async IAsyncEnumerable<SearchUpdate> SearchDocumentAsync(
            DocumentId documentId,
            Guid searchSessionId,
            string query,
            bool matchCase,
            bool wholeWord,
            int batchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new SearchStartedUpdate(searchSessionId, 5);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SearchResultsUpdate(searchSessionId,
            [
                new SearchResult(
                    searchSessionId,
                    0,
                    2,
                    10,
                    query.Length,
                    query,
                    $"result containing {query}",
                    [new PdfRect(72, 700, 120, 720)])
            ]);
            yield return new SearchProgressUpdate(searchSessionId, 5, 5, 1);
            yield return new SearchCompletedUpdate(searchSessionId, 1);
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
