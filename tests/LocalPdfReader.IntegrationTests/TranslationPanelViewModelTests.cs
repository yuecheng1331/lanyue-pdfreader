using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using LocalPdfReader.App;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public class TranslationPanelViewModelTests
{
    private static IUserErrorService ErrorService { get; } =
        new UserErrorService(NullLogger<UserErrorService>.Instance);

    [Fact]
    public void TranslationCannotStartWithoutASelection()
    {
        var viewModel = CreateViewModel(new StreamingTranslationService());

        Assert.False(viewModel.CanTranslate);
        Assert.False(viewModel.TranslateCommand.CanExecute(null));
    }

    [Fact]
    public void UserFacingErrorsIncludeStableCodeWithoutTechnicalExceptionText()
    {
        var error = ErrorService.Report(
            UserErrorCode.DocumentOpenFailed,
            new InvalidOperationException("private-path-and-internal-detail"));

        Assert.Equal("PDF_OPEN_FAILED", error.Code);
        Assert.Contains("请确认", error.SuggestedAction);
        Assert.DoesNotContain("private-path-and-internal-detail", error.DialogText);
    }

    [Fact]
    public void SelectingTextDoesNotAutomaticallyCallTheApiService()
    {
        var service = new StreamingTranslationService();
        var viewModel = CreateViewModel(service);

        viewModel.SetSourceText("Selected source");

        Assert.True(viewModel.CanTranslate);
        Assert.Null(service.LastRequest);
        Assert.Contains("点击", viewModel.StatusText);
    }

    [Fact]
    public async Task StreamingChunksAreDisplayedAndRequestUsesSelectedOptions()
    {
        var service = new StreamingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText("Selected source");
        viewModel.SelectedPreset = TranslationPreset.ComputerScience;
        viewModel.TargetLanguage = "zh-CN";

        await viewModel.TranslateAsync();

        Assert.Equal("流式译文", viewModel.TranslatedText);
        Assert.Equal(TranslationTaskState.Completed, viewModel.State);
        Assert.Equal("Selected source", service.LastRequest?.SourceText);
        Assert.Equal(TranslationPreset.ComputerScience, service.LastRequest?.Preset);
        Assert.Equal("zh-CN", service.LastRequest?.TargetLanguage);
    }

    [Fact]
    public async Task EditedSourceTextIsUsedWithoutAutomaticTranslation()
    {
        var service = new StreamingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText("Journal header\nUseful paragraph\nPage 12");

        viewModel.SourceText = "Useful paragraph";

        Assert.Null(service.LastRequest);
        Assert.Equal("Useful paragraph".Length, viewModel.SourceText.Length);
        await viewModel.TranslateAsync();
        Assert.Equal("Useful paragraph", service.LastRequest?.SourceText);
    }

    [Fact]
    public async Task StopCancelsTheActiveTranslationWithoutRetrying()
    {
        var service = new BlockingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText("Selected source");
        var translation = viewModel.TranslateAsync();
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.CancelAsync();
        await translation;

        Assert.Equal(1, service.CancelCount);
        Assert.Equal(TranslationTaskState.Cancelled, viewModel.State);
        Assert.Contains("可能仍会计费", viewModel.StatusText);
    }

    [Fact]
    public async Task EditedTranslationCanBeCopied()
    {
        var clipboard = new RecordingClipboardService();
        var viewModel = CreateViewModel(new StreamingTranslationService(), clipboard);
        viewModel.TranslatedText = "人工修改后的译文";

        await viewModel.CopyTranslationAsync();

        Assert.Equal("人工修改后的译文", clipboard.Text);
    }

    [Fact]
    public async Task BusyClipboardIsRetriedBeforeCopyReportsSuccess()
    {
        var attempts = 0;
        string? copiedText = null;
        var clipboard = new WpfClipboardService(
            text =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new COMException("clipboard busy", unchecked((int)0x800401D0));
                }

                copiedText = text;
            },
            (_, _) => Task.CompletedTask);
        var viewModel = CreateViewModel(new StreamingTranslationService(), clipboard);
        viewModel.TranslatedText = "等待剪贴板后复制的译文";

        await viewModel.CopyTranslationAsync();

        Assert.Equal(3, attempts);
        Assert.Equal("等待剪贴板后复制的译文", copiedText);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Contains("复制", viewModel.StatusText);
    }

    [Fact]
    public async Task SavedPresetAndTargetLanguageAreLoaded()
    {
        var settings = new AppSettings
        {
            Translation = new TranslationSettings
            {
                DefaultPreset = "Medical",
                TargetLanguage = "en"
            }
        };
        var viewModel = new TranslationPanelViewModel(
            new StreamingTranslationService(),
            new StubSettingsService(settings),
            new RecordingClipboardService(),
            ErrorService);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(TranslationPreset.Medical, viewModel.SelectedPreset);
        Assert.Equal("en", viewModel.TargetLanguage);
    }

    [Fact]
    public async Task ClassifiedApiErrorIsShownWithoutAutomaticRetry()
    {
        var service = new FailingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText("Selected source");

        await viewModel.TranslateAsync();

        Assert.Equal(TranslationTaskState.Failed, viewModel.State);
        Assert.Contains("限流", viewModel.ErrorMessage);
        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task LongSourceTextIsTranslatedInSegmentsWithProgress()
    {
        var service = new SegmentRecordingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText(new string('A', 4100));

        await viewModel.TranslateAsync();

        Assert.True(service.Requests.Count >= 3);
        Assert.All(service.Requests, request => Assert.True(request.SourceText.Length <= 1800));
        Assert.Equal(TranslationTaskState.Completed, viewModel.State);
        Assert.Contains("已完成", viewModel.ProgressText);
    }

    [Fact]
    public async Task SegmentTranslationCarriesPreviousTranslationContext()
    {
        var service = new SegmentRecordingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText(new string('A', 1900) + Environment.NewLine + "next paragraph");

        await viewModel.TranslateAsync();

        Assert.True(service.Requests.Count >= 2);
        Assert.Contains("上一段译文片段", service.Requests[1].PreferenceInstruction);
    }

    [Fact]
    public async Task CachedTranslationDoesNotCallApi()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("cache me");
        await viewModel.TranslateAsync();
        Assert.Single(service.Requests);
        Assert.Single(repository.History);

        var secondViewModel = CreateViewModel(service, repository: repository);
        secondViewModel.SetSourceText("cache me");
        await secondViewModel.TranslateAsync();

        Assert.Single(service.Requests);
        Assert.Single(repository.History);
        Assert.Equal("译文:cache me", secondViewModel.TranslatedText);
        Assert.Contains("缓存", secondViewModel.ProgressText);
    }

    [Fact]
    public async Task ExactCacheIgnoresPdfHardLineBreaks()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("This framework first introduces two distinct\nconsistency regularizations.");
        await viewModel.TranslateAsync();
        Assert.Single(service.Requests);

        var secondViewModel = CreateViewModel(service, repository: repository);
        secondViewModel.SetSourceText("This framework first introduces two distinct consistency regularizations.");
        await secondViewModel.TranslateAsync();

        Assert.Single(service.Requests);
        Assert.Equal(
            "译文:This framework first introduces two distinct consistency regularizations.",
            secondViewModel.TranslatedText);
        Assert.Contains("缓存", secondViewModel.ProgressText);
    }

    [Fact]
    public async Task SentenceAlignedReusableCacheDoesNotCallApi()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var cachedAt = DateTimeOffset.UtcNow;
        await repository.SaveCacheAsync(
            new TranslationCacheEntry(
                "long-cache",
                "First sentence. Second sentence. Third sentence.",
                "第一句。第二句。第三句。",
                "zh-CN",
                TranslationPreset.Academic,
                null,
                null,
                cachedAt,
                cachedAt,
                1),
            CancellationToken.None);
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("First sentence. Second sentence.");

        await viewModel.TranslateAsync();

        Assert.Empty(service.Requests);
        Assert.Equal("第一句。\r\n第二句。", viewModel.TranslatedText);
        Assert.Contains("句级缓存", viewModel.ProgressText);
    }

    [Fact]
    public async Task ReusableCacheMatchesSelectedSentenceWithoutTrailingPunctuation()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var cachedAt = DateTimeOffset.UtcNow;
        await repository.SaveCacheAsync(
            new TranslationCacheEntry(
                "sentence-prefix-cache",
                "One is the consistency between a weakly perturbed image and strongly perturbed images.",
                "一是弱扰动图像和强扰动图像之间的一致性。",
                "zh-CN",
                TranslationPreset.Academic,
                null,
                null,
                cachedAt,
                cachedAt,
                1),
            CancellationToken.None);
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("One is the consistency between a weakly\nperturbed image and strongly perturbed images");

        await viewModel.TranslateAsync();

        Assert.Empty(service.Requests);
        Assert.Equal("一是弱扰动图像和强扰动图像之间的一致性。", viewModel.TranslatedText);
        Assert.Contains("句级缓存", viewModel.ProgressText);
    }

    [Fact]
    public async Task FuzzySentenceReusableCacheIgnoresPunctuationAndWhitespace()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var cachedAt = DateTimeOffset.UtcNow;
        await repository.SaveCacheAsync(
            new TranslationCacheEntry(
                "punctuated-cache",
                "First sentence, with punctuation. Second sentence: has tokens. Third sentence.",
                "第一句。\r\n第二句。\r\n第三句。",
                "zh-CN",
                TranslationPreset.Academic,
                null,
                null,
                cachedAt,
                cachedAt,
                1),
            CancellationToken.None);
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("First sentence with punctuation.\nSecond sentence has tokens.");

        await viewModel.TranslateAsync();

        Assert.Empty(service.Requests);
        Assert.Equal("第一句。\r\n第二句。", viewModel.TranslatedText);
        Assert.Contains("句级缓存", viewModel.ProgressText);
    }

    [Fact]
    public async Task DisabledCacheStillWritesHistoryWithoutSavingCache()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var settings = new AppSettings
        {
            Translation = new TranslationSettings { MaximumCacheEntries = 0 }
        };
        var viewModel = new TranslationPanelViewModel(
            service,
            new StubSettingsService(settings),
            new RecordingClipboardService(),
            ErrorService,
            repository);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SetSourceText("no cache");

        await viewModel.TranslateAsync();

        Assert.Single(repository.History);
        Assert.Empty(repository.Cache);
    }

    [Fact]
    public async Task PreferredHistorySamplesAreSentAsPreferenceInstructions()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("first source");
        await viewModel.TranslateAsync();
        viewModel.SelectedHistoryEntry = Assert.Single(viewModel.HistoryEntries);
        await ExecuteAsync(viewModel.MarkHistoryPreferredCommand);

        viewModel.SetSourceText("second source");
        await viewModel.TranslateAsync();

        Assert.Contains(
            service.Requests,
            request => request.PreferenceInstruction?.Contains("参考表达", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task DeletingHistoryEntryAlsoRemovesReusableCache()
    {
        var service = new AlignedTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("This framework first introduces two regularizations. It then optimizes the model.");
        await viewModel.TranslateAsync();
        viewModel.SelectedHistoryEntry = Assert.Single(viewModel.HistoryEntries);

        await ExecuteAsync(viewModel.DeleteHistoryEntryCommand);

        Assert.Empty(repository.History);
        Assert.Empty(repository.Cache);
        Assert.Empty(repository.CacheSegments);
        Assert.Contains("删除", viewModel.ProgressText);
    }

    [Fact]
    public async Task GlossaryTermsAreSentAsInstructions()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.GlossarySourceTerm = "Transformer";
        viewModel.GlossaryTargetTerm = "Transformer 模型";
        await ExecuteAsync(viewModel.AddGlossaryEntryCommand);
        viewModel.SetSourceText("Transformer improves the model.");

        await viewModel.TranslateAsync();

        var request = Assert.Single(service.Requests);
        Assert.Contains("Transformer => Transformer 模型", request.GlossaryInstruction);
    }

    [Fact]
    public async Task CurrentTaskInstructionIsSentWithNonCustomPreset()
    {
        var service = new SegmentRecordingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SelectedPreset = TranslationPreset.Academic;
        viewModel.CustomInstruction = "保留更多英文缩写";
        viewModel.SetSourceText("CNN improves classification.");

        await viewModel.TranslateAsync();

        var request = Assert.Single(service.Requests);
        Assert.Equal("保留更多英文缩写", request.CustomInstruction);
    }

    [Fact]
    public async Task CustomStylePromptIsSentSeparatelyFromCurrentTaskInstruction()
    {
        var service = new SegmentRecordingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SelectedPreset = TranslationPreset.Custom;
        viewModel.CustomStylePrompt = "用简洁学术中文翻译";
        viewModel.CustomInstruction = "保留 MCSAL 缩写";
        viewModel.SetSourceText("MCSAL improves classification.");

        await viewModel.TranslateAsync();

        var request = Assert.Single(service.Requests);
        Assert.Equal("用简洁学术中文翻译", request.StyleInstruction);
        Assert.Equal("保留 MCSAL 缩写", request.CustomInstruction);
    }

    [Fact]
    public async Task ManagedCustomStyleCanBeSavedAndUsedForTranslation()
    {
        var service = new SegmentRecordingTranslationService();
        var settingsService = new StubSettingsService(new AppSettings());
        var viewModel = new TranslationPanelViewModel(
            service,
            settingsService,
            new RecordingClipboardService(),
            ErrorService);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.ManagedStyleName = "审稿意见";
        viewModel.ManagedStylePrompt = "使用简洁、直接的审稿意见语气。";

        await ExecuteAsync(viewModel.AddTranslationStyleCommand);
        viewModel.SetSourceText("This method is useful.");
        await viewModel.TranslateAsync();

        var request = Assert.Single(service.Requests);
        Assert.Equal(TranslationPreset.Custom, request.Preset);
        Assert.Equal("使用简洁、直接的审稿意见语气。", request.StyleInstruction);
        Assert.Contains(settingsService.Settings.TranslationStyles.Items, item => item.Name == "审稿意见");
    }

    [Fact]
    public async Task BuiltInStyleEditsCanBeHiddenAndRestored()
    {
        var service = new SegmentRecordingTranslationService();
        var settingsService = new StubSettingsService(new AppSettings());
        var viewModel = new TranslationPanelViewModel(
            service,
            settingsService,
            new RecordingClipboardService(),
            ErrorService);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedManagedStyle = viewModel.ManagedStyleOptions.Single(item => item.Id == "preset-academic");
        viewModel.ManagedStylePrompt = "使用高度凝练的学术中文。";
        await ExecuteAsync(viewModel.SaveTranslationStyleCommand);
        viewModel.SetSourceText("The model improves accuracy.");

        await viewModel.TranslateAsync();

        Assert.Equal("使用高度凝练的学术中文。", Assert.Single(service.Requests).StyleInstruction);
        await ExecuteAsync(viewModel.DeleteTranslationStyleCommand);
        Assert.DoesNotContain(viewModel.StyleOptions, item => item.Id == "preset-academic");

        viewModel.SelectedManagedStyle = viewModel.ManagedStyleOptions.Single(item => item.Id == "preset-academic");
        await ExecuteAsync(viewModel.RestoreTranslationStyleCommand);

        var restored = viewModel.StyleOptions.Single(item => item.Id == "preset-academic");
        Assert.False(restored.IsDeleted);
        Assert.Equal("学术论文", restored.Name);
    }

    [Fact]
    public async Task ComparisonSegmentsUseStableParagraphAlignment()
    {
        var service = new SegmentRecordingTranslationService();
        var viewModel = CreateViewModel(service);
        viewModel.SetSourceText("First sentence. Second sentence.");

        await viewModel.TranslateAsync();

        var segment = Assert.Single(viewModel.ComparisonSegments);
        Assert.Equal("First sentence. Second sentence.", segment.SourceText);
    }

    [Fact]
    public async Task AlignmentJsonDisplaysTranslationAndStoresSemanticSegments()
    {
        var service = new AlignedTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("This framework first introduces two regularizations. It then optimizes the model.");

        await viewModel.TranslateAsync();

        Assert.Equal("该框架首先引入两种正则化。\r\n随后优化模型。", viewModel.TranslatedText);
        Assert.Single(service.Requests);
        Assert.Equal(2, repository.CacheSegments.Count);
        Assert.Contains(repository.CacheSegments, segment => segment.SourceText == "This framework first introduces two regularizations.");
        Assert.Contains(repository.CacheSegments, segment => segment.TranslatedText == "随后优化模型。");
        Assert.Equal(2, viewModel.ComparisonSegments.Count);
        Assert.Equal("This framework first introduces two regularizations.", viewModel.ComparisonSegments[0].SourceText);
        Assert.Equal("该框架首先引入两种正则化。", viewModel.ComparisonSegments[0].TranslatedText);
        Assert.Equal("It then optimizes the model.", viewModel.ComparisonSegments[1].SourceText);
        Assert.Equal("随后优化模型。", viewModel.ComparisonSegments[1].TranslatedText);
    }

    [Fact]
    public async Task SemanticSegmentCacheMatchesSelectionWithLineBreakAndPunctuationChanges()
    {
        var service = new AlignedTranslationService();
        var repository = new MemoryTranslationRepository();
        var viewModel = CreateViewModel(service, repository: repository);
        viewModel.SetSourceText("This framework first introduces two regularizations. It then optimizes the model.");
        await viewModel.TranslateAsync();

        var secondViewModel = CreateViewModel(service, repository: repository);
        secondViewModel.SetSourceText("This framework first introduces\ntwo regularizations");
        await secondViewModel.TranslateAsync();

        Assert.Single(service.Requests);
        Assert.Equal("该框架首先引入两种正则化。", secondViewModel.TranslatedText);
        Assert.Contains("语义片段缓存", secondViewModel.ProgressText);
        var comparison = Assert.Single(secondViewModel.ComparisonSegments);
        Assert.Equal("This framework first introduces two regularizations.", comparison.SourceText);
        Assert.Equal("该框架首先引入两种正则化。", comparison.TranslatedText);
    }

    [Fact]
    public async Task SingleWordTranslationUsesLocalWordServiceWithoutCallingApi()
    {
        var service = new SegmentRecordingTranslationService();
        var repository = new MemoryTranslationRepository();
        var wordService = new StaticWordTranslationService();
        var viewModel = CreateViewModel(service, repository: repository, wordService: wordService);
        viewModel.SetSourceText("framework");

        await viewModel.TranslateAsync();

        Assert.Empty(service.Requests);
        Assert.Equal("框架", viewModel.TranslatedText);
        Assert.Contains("本地测试词典", viewModel.ProgressText);
        Assert.Single(repository.CacheSegments);
    }

    [Fact]
    public async Task ClickingPageWhitespaceKeepsSelectionAndEditedTranslationContent()
    {
        var documentId = new DocumentId(Guid.NewGuid());
        var pageText = new PageTextData(
            documentId,
            0,
            "AB",
            new[]
            {
                new TextGlyph(0, "A", new PdfRect(10, 70, 20, 80), 0, 0),
                new TextGlyph(1, "B", new PdfRect(20, 70, 30, 80), 0, 0)
            });
        var readerState = new ReaderState();
        readerState.Open(new PdfDocumentInfo(
            documentId,
            "sample.pdf",
            1,
            IsEncrypted: false,
            HasTextLayer: true,
            Title: null,
            Author: null));
        readerState.SetCurrentPageSize(new PdfSize(100, 100));
        var translation = CreateViewModel(new StreamingTranslationService());
        var reader = new ReaderViewModel(
            new PageTextWorkerClient(pageText),
            readerState,
            new IdentityCoordinateTransformer(),
            new TextSelectionService(),
            translation,
            ErrorService);
        string? selectionError = null;
        reader.ErrorOccurred += (_, args) => selectionError = args.Error.InlineText;
        await reader.BeginSelectionAsync(new ViewPoint(15, 75), CancellationToken.None);
        reader.UpdateSelection(new ViewPoint(25, 75));
        reader.CompleteSelection();
        Assert.True(reader.CurrentSelection is not null, selectionError ?? reader.StatusText);
        translation.SourceText = "人工清理后的原文";
        translation.TranslatedText = "已存在的译文";

        await reader.BeginSelectionAsync(new ViewPoint(100, 100), CancellationToken.None);
        reader.CompleteSelection();

        Assert.NotNull(reader.CurrentSelection);
        Assert.Equal("人工清理后的原文", translation.SourceText);
        Assert.Equal("已存在的译文", translation.TranslatedText);
        Assert.Contains("已保留", reader.StatusText);
    }

    [Fact]
    public async Task ClickingTextWithoutDraggingClearsSelectionButKeepsTranslationContext()
    {
        var documentId = new DocumentId(Guid.NewGuid());
        var pageText = new PageTextData(
            documentId,
            0,
            "AB",
            new[]
            {
                new TextGlyph(0, "A", new PdfRect(10, 70, 20, 80), 0, 0),
                new TextGlyph(1, "B", new PdfRect(20, 70, 30, 80), 0, 0)
            });
        var readerState = new ReaderState();
        readerState.Open(new PdfDocumentInfo(
            documentId,
            "sample.pdf",
            1,
            IsEncrypted: false,
            HasTextLayer: true,
            Title: null,
            Author: null));
        readerState.SetCurrentPageSize(new PdfSize(100, 100));
        var translation = CreateViewModel(new StreamingTranslationService());
        var reader = new ReaderViewModel(
            new PageTextWorkerClient(pageText),
            readerState,
            new IdentityCoordinateTransformer(),
            new TextSelectionService(),
            translation,
            ErrorService);
        await reader.BeginSelectionAsync(new ViewPoint(15, 75), CancellationToken.None);
        reader.UpdateSelection(new ViewPoint(25, 75));
        reader.CompleteSelection();
        translation.SourceText = "人工清理后的原文";
        translation.TranslatedText = "已存在的译文";
        var existingSelection = reader.CurrentSelection;

        await reader.BeginSelectionAsync(new ViewPoint(25, 75), CancellationToken.None);
        reader.CompleteSelection();

        Assert.NotNull(existingSelection);
        Assert.Null(reader.CurrentSelection);
        Assert.Empty(reader.SelectionRectangles);
        Assert.Equal("人工清理后的原文", translation.SourceText);
        Assert.Equal("已存在的译文", translation.TranslatedText);
        Assert.Contains("已清除选区", reader.StatusText);
    }

    [Fact]
    public void WorkerDisconnectDisablesReaderOperationsButKeepsTranslationContent()
    {
        var translation = CreateViewModel(new StreamingTranslationService());
        translation.SourceText = "仍可处理的原文";
        translation.TranslatedText = "仍可复制的译文";
        var readerState = new ReaderState();
        readerState.Open(new PdfDocumentInfo(
            new DocumentId(Guid.NewGuid()),
            "sample.pdf",
            2,
            IsEncrypted: false,
            HasTextLayer: true,
            Title: null,
            Author: null));
        var reader = new ReaderViewModel(
            new PageTextWorkerClient(new PageTextData(
                new DocumentId(Guid.NewGuid()),
                0,
                string.Empty,
                [])),
            readerState,
            new IdentityCoordinateTransformer(),
            new TextSelectionService(),
            translation,
            ErrorService);

        Assert.True(reader.CanUseDocumentControls);
        Assert.True(reader.RotateClockwiseCommand.CanExecute(null));
        Assert.True(reader.GoToPageCommand.CanExecute(null));

        reader.HandleWorkerDisconnected(new PdfWorkerDisconnectedEventArgs(
            PdfWorkerDisconnectReason.ProcessExited,
            exitCode: -1,
            exception: null));

        Assert.False(reader.IsWorkerAvailable);
        Assert.False(reader.CanOpenDocument);
        Assert.False(reader.CanUseDocumentControls);
        Assert.False(reader.RotateClockwiseCommand.CanExecute(null));
        Assert.False(reader.GoToPageCommand.CanExecute(null));
        Assert.Contains("意外停止", reader.StatusText);
        Assert.Equal("仍可处理的原文", translation.SourceText);
        Assert.Equal("仍可复制的译文", translation.TranslatedText);
    }

    [Fact]
    public void RestartedWorkerAllowsOpeningWhenNoDocumentSessionNeedsRecovery()
    {
        var translation = CreateViewModel(new StreamingTranslationService());
        var reader = new ReaderViewModel(
            new PageTextWorkerClient(new PageTextData(
                new DocumentId(Guid.NewGuid()),
                0,
                string.Empty,
                [])),
            new ReaderState(),
            new IdentityCoordinateTransformer(),
            new TextSelectionService(),
            translation,
            ErrorService);
        var disconnect = new PdfWorkerDisconnectedEventArgs(
            PdfWorkerDisconnectReason.ProcessExited,
            exitCode: -1,
            exception: null);

        reader.HandleWorkerDisconnected(disconnect);
        reader.HandleWorkerRestarting();
        reader.HandleWorkerRestarted();

        Assert.True(reader.IsWorkerAvailable);
        Assert.True(reader.CanOpenDocument);
        Assert.Contains("可以打开文档", reader.StatusText);
    }

    private static TranslationPanelViewModel CreateViewModel(
        ITranslationService service,
        IClipboardService? clipboard = null,
        ITranslationMemoryRepository? repository = null,
        IWordTranslationService? wordService = null) => new(
            service,
            new StubSettingsService(new AppSettings()),
            clipboard ?? new RecordingClipboardService(),
            ErrorService,
            repository,
            wordService);

    private static Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        return Task.Delay(20);
    }

    private sealed class StubSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Settings { get; private set; } = settings;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task SaveAsync(AppSettings settingsToSave, CancellationToken cancellationToken)
        {
            Settings = settingsToSave;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class StreamingTranslationService : ITranslationService
    {
        public TranslationRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            yield return new TranslationChunk(request.RequestId, "流式", IsCompleted: false);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TranslationChunk(request.RequestId, "译文", IsCompleted: false);
            yield return new TranslationChunk(request.RequestId, string.Empty, IsCompleted: true);
        }

        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingTranslationService : ITranslationService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCount { get; private set; }

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingTranslationService : ITranslationService
    {
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Yield();
            throw new TranslationException(TranslationError.RateLimited, "rate limited");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SegmentRecordingTranslationService : ITranslationService
    {
        public List<TranslationRequest> Requests { get; } = [];

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new TranslationChunk(
                request.RequestId,
                "译文:" + request.SourceText,
                IsCompleted: true);
        }

        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlignedTranslationService : ITranslationService
    {
        public List<TranslationRequest> Requests { get; } = [];

        public async IAsyncEnumerable<TranslationChunk> TranslateAsync(
            TranslationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new TranslationChunk(
                request.RequestId,
                """
                {"segments":[{"source":"This framework first introduces two regularizations.","target":"该框架首先引入两种正则化。"},{"source":"It then optimizes the model.","target":"随后优化模型。"}]}
                """,
                IsCompleted: true);
        }

        public Task CancelAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StaticWordTranslationService : IWordTranslationService
    {
        public Task<WordTranslationResult?> TryTranslateAsync(
            string sourceText,
            string targetLanguage,
            CancellationToken cancellationToken) =>
            Task.FromResult<WordTranslationResult?>(
                string.Equals(sourceText, "framework", StringComparison.OrdinalIgnoreCase) &&
                targetLanguage == "zh-CN"
                    ? new WordTranslationResult("framework", "框架", "本地测试词典")
                    : null);
    }

    private sealed class MemoryTranslationRepository : ITranslationMemoryRepository
    {
        private readonly Dictionary<string, TranslationCacheEntry> _cache = [];
        private readonly List<TranslationCacheSegment> _cacheSegments = [];
        private readonly List<TranslationHistoryEntry> _history = [];
        private readonly List<TranslationGlossaryEntry> _glossary = [];

        public IReadOnlyList<TranslationHistoryEntry> History => _history;

        public IReadOnlyDictionary<string, TranslationCacheEntry> Cache => _cache;

        public IReadOnlyList<TranslationCacheSegment> CacheSegments => _cacheSegments;

        public Task<TranslationCacheEntry?> FindCacheAsync(
            string cacheKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(_cache.TryGetValue(cacheKey, out var entry) ? entry : null);

        public Task SaveCacheAsync(
            TranslationCacheEntry entry,
            CancellationToken cancellationToken)
        {
            _cache[entry.CacheKey] = entry;
            return Task.CompletedTask;
        }

        public Task SaveCacheSegmentsAsync(
            IReadOnlyList<TranslationCacheSegment> segments,
            CancellationToken cancellationToken)
        {
            foreach (var segment in segments)
            {
                var index = _cacheSegments.FindIndex(item =>
                    item.CacheKey == segment.CacheKey &&
                    item.SegmentIndex == segment.SegmentIndex);
                if (index >= 0)
                {
                    _cacheSegments[index] = segment;
                }
                else
                {
                    _cacheSegments.Add(segment);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranslationCacheEntry>> GetReusableCacheCandidatesAsync(
            string targetLanguage,
            TranslationPreset preset,
            string? customInstruction,
            string? glossaryInstruction,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationCacheEntry>>(
                _cache.Values
                    .Where(entry =>
                        entry.TargetLanguage == targetLanguage &&
                        entry.Preset == preset &&
                        string.Equals(entry.CustomInstruction, customInstruction, StringComparison.Ordinal) &&
                        string.Equals(entry.GlossaryInstruction, glossaryInstruction, StringComparison.Ordinal))
                    .Take(limit)
                    .ToArray());

        public Task<IReadOnlyList<TranslationCacheSegment>> GetReusableCacheSegmentsAsync(
            string targetLanguage,
            TranslationPreset preset,
            string? customInstruction,
            string? glossaryInstruction,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationCacheSegment>>(
                _cacheSegments
                    .Where(segment =>
                        segment.TargetLanguage == targetLanguage &&
                        segment.Preset == preset &&
                        string.Equals(segment.CustomInstruction, customInstruction, StringComparison.Ordinal) &&
                        string.Equals(segment.GlossaryInstruction, glossaryInstruction, StringComparison.Ordinal))
                    .OrderBy(segment => segment.CacheKey)
                    .ThenBy(segment => segment.SegmentIndex)
                    .Take(limit)
                    .ToArray());

        public Task PruneCacheAsync(
            int maximumEntries,
            CancellationToken cancellationToken)
        {
            if (maximumEntries == 0)
            {
                _cache.Clear();
                _cacheSegments.Clear();
            }

            return Task.CompletedTask;
        }

        public Task AddHistoryAsync(
            TranslationHistoryEntry entry,
            CancellationToken cancellationToken)
        {
            _history.Insert(0, entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranslationHistoryEntry>> GetRecentHistoryAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationHistoryEntry>>(_history.Take(limit).ToArray());

        public Task UpdateHistorySampleKindAsync(
            Guid historyId,
            TranslationSampleKind sampleKind,
            CancellationToken cancellationToken)
        {
            var index = _history.FindIndex(entry => entry.HistoryId == historyId);
            if (index >= 0)
            {
                _history[index] = _history[index] with { SampleKind = sampleKind };
            }

            return Task.CompletedTask;
        }

        public Task DeleteHistoryAndRelatedCacheAsync(
            TranslationHistoryEntry entry,
            CancellationToken cancellationToken)
        {
            _history.RemoveAll(item => item.HistoryId == entry.HistoryId);
            var matchingCacheKeys = _cache.Values
                .Where(cache =>
                    cache.SourceText == entry.SourceText &&
                    cache.TranslatedText == entry.TranslatedText &&
                    cache.TargetLanguage == entry.TargetLanguage &&
                    cache.Preset == entry.Preset)
                .Select(cache => cache.CacheKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var cacheKey in matchingCacheKeys)
            {
                _cache.Remove(cacheKey);
            }

            _cacheSegments.RemoveAll(segment =>
                matchingCacheKeys.Contains(segment.CacheKey) ||
                (segment.SourceText == entry.SourceText &&
                 segment.TranslatedText == entry.TranslatedText &&
                 segment.TargetLanguage == entry.TargetLanguage &&
                 segment.Preset == entry.Preset));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranslationGlossaryEntry>> GetGlossaryAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationGlossaryEntry>>(_glossary.ToArray());

        public Task<TranslationGlossaryEntry> UpsertGlossaryEntryAsync(
            string sourceTerm,
            string targetTerm,
            CancellationToken cancellationToken)
        {
            var entry = new TranslationGlossaryEntry(
                Guid.NewGuid(),
                sourceTerm.Trim(),
                targetTerm.Trim(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            _glossary.RemoveAll(item => string.Equals(item.SourceTerm, entry.SourceTerm, StringComparison.OrdinalIgnoreCase));
            _glossary.Add(entry);
            return Task.FromResult(entry);
        }

        public Task DeleteGlossaryEntryAsync(
            Guid entryId,
            CancellationToken cancellationToken)
        {
            _glossary.RemoveAll(item => item.EntryId == entryId);
            return Task.CompletedTask;
        }
    }

    private sealed class PageTextWorkerClient(PageTextData pageText) : IPdfWorkerClient
    {
        public event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected;

        public Task<PageTextData> GetPageTextAsync(
            DocumentId documentId,
            int pageIndex,
            CancellationToken cancellationToken) => Task.FromResult(pageText);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CloseDocumentAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RenderedPageDescriptor> RenderPageAsync(PageRenderRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TextHitTestResult?> HitTestTextAsync(
            DocumentId documentId,
            int pageIndex,
            PdfPoint point,
            double tolerance,
            CancellationToken cancellationToken) => Task.FromResult<TextHitTestResult?>(null);

        public Task ReleaseSharedMemoryAsync(string memoryMapName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void ReportDisconnected(PdfWorkerDisconnectedEventArgs eventArgs) =>
            Disconnected?.Invoke(this, eventArgs);
    }

    private sealed class IdentityCoordinateTransformer : ICoordinateTransformer
    {
        public ViewPoint PdfToView(PdfPoint pdfPoint, PageTransformContext context) =>
            new(pdfPoint.X, pdfPoint.Y);

        public PdfPoint ViewToPdf(ViewPoint viewPoint, PageTransformContext context) =>
            new(viewPoint.X, viewPoint.Y);

        public ViewRect PdfToView(PdfRect pdfRect, PageTransformContext context) =>
            new(pdfRect.Left, pdfRect.Bottom, pdfRect.Right - pdfRect.Left, pdfRect.Top - pdfRect.Bottom);
    }
}
