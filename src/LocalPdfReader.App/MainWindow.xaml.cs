using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Domain;
using Microsoft.Win32;

namespace LocalPdfReader.App;

public partial class MainWindow : Window
{
    private readonly ReaderViewModel _viewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly AboutViewModel _aboutViewModel;
    private readonly ISettingsService? _settingsService;
    private Point _tabDragStartPoint;
    private DocumentTabItemViewModel? _draggedDocumentTab;
    private bool _isApplyingRequestedScroll;
    private Point _leftSidebarHandleStartPoint;
    private Point _rightSidebarHandleStartPoint;
    private double _leftSidebarWidthAtDragStart;
    private double _rightSidebarWidthAtDragStart;
    private bool _isLeftSidebarHandleCaptured;
    private bool _isRightSidebarHandleCaptured;
    private bool _hasLeftSidebarHandleDragged;
    private bool _hasRightSidebarHandleDragged;
    private TranslationHistoryWindow? _translationHistoryWindow;
    private TranslationPreferencesWindow? _translationPreferencesWindow;
    private TranslationComparisonWindow? _translationComparisonWindow;
    private KeyboardShortcutGesture _searchShortcut = KeyboardShortcutGesture.DefaultSearch;
    private KeyboardShortcutGesture _translationShortcut = KeyboardShortcutGesture.DefaultTranslation;

    public MainWindow(
        ReaderViewModel viewModel,
        SettingsViewModel settingsViewModel,
        AboutViewModel aboutViewModel,
        ISettingsService? settingsService = null)
    {
        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _aboutViewModel = aboutViewModel;
        _settingsService = settingsService;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.ErrorOccurred += OnReaderErrorOccurred;
        _viewModel.ScrollOffsetRequested += OnScrollOffsetRequested;
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow(_aboutViewModel) { Owner = this }.ShowDialog();

    private async void TranslationHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.Translation.RefreshTranslationMemoryAsync(CancellationToken.None);
        if (_translationHistoryWindow is { IsLoaded: true })
        {
            _translationHistoryWindow.Activate();
            return;
        }

        _translationHistoryWindow = new TranslationHistoryWindow(_viewModel.Translation)
        {
            Owner = this
        };
        _translationHistoryWindow.Closed += (_, _) => _translationHistoryWindow = null;
        _translationHistoryWindow.Show();
    }

    private async void TranslationPreferencesButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.Translation.RefreshTranslationMemoryAsync(CancellationToken.None);
        if (_translationPreferencesWindow is { IsLoaded: true })
        {
            _translationPreferencesWindow.Activate();
            return;
        }

        _translationPreferencesWindow = new TranslationPreferencesWindow(_viewModel.Translation)
        {
            Owner = this
        };
        _translationPreferencesWindow.Closed += (_, _) => _translationPreferencesWindow = null;
        _translationPreferencesWindow.Show();
    }

    private void TranslationComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationComparisonWindow is { IsLoaded: true })
        {
            _translationComparisonWindow.Activate();
            return;
        }

        _translationComparisonWindow = new TranslationComparisonWindow(_viewModel.Translation)
        {
            Owner = this
        };
        _translationComparisonWindow.Closed += (_, _) => _translationComparisonWindow = null;
        _translationComparisonWindow.Show();
    }

    private void ExpandSourceTextButton_Click(object sender, RoutedEventArgs e) =>
        ShowTranslationTextEditor(
            "原文",
            _viewModel.Translation.SourceText,
            text => _viewModel.Translation.SourceText = text);

    private void ExpandTranslatedTextButton_Click(object sender, RoutedEventArgs e) =>
        ShowTranslationTextEditor(
            "译文",
            _viewModel.Translation.TranslatedText,
            text => _viewModel.Translation.TranslatedText = text);

    private void ShowTranslationTextEditor(string title, string text, Action<string> applyText)
    {
        var window = new TranslationTextEditorWindow(title, text)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            applyText(window.Text);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _translationHistoryWindow?.Close();
        _translationPreferencesWindow?.Close();
        _translationComparisonWindow?.Close();
        _viewModel.ErrorOccurred -= OnReaderErrorOccurred;
        _viewModel.ScrollOffsetRequested -= OnScrollOffsetRequested;
        base.OnClosed(e);
    }

    private void OnScrollOffsetRequested(object? sender, ScrollOffsetRequestedEventArgs e)
    {
        _isApplyingRequestedScroll = true;
        PdfViewport.UpdateLayout();
        PdfViewport.ScrollToHorizontalOffset(e.HorizontalOffset);
        PdfViewport.ScrollToVerticalOffset(e.VerticalOffset);
        Dispatcher.BeginInvoke(() => _isApplyingRequestedScroll = false);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadKeyboardShortcutsAsync();
        await _viewModel.Translation.InitializeAsync(CancellationToken.None);
        await _viewModel.InitializeAsync(CancellationToken.None);
        ApplyRestoredScrollOffsets();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenDocumentWithPasswordPromptAsync(dialog.FileName);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void RecentDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentDocumentItemViewModel recentDocument })
        {
            if (!recentDocument.IsMissing && File.Exists(recentDocument.FullPath))
            {
                await OpenDocumentWithPasswordPromptAsync(recentDocument.FullPath);
            }
            else
            {
                await _viewModel.OpenRecentDocumentAsync(recentDocument, CancellationToken.None);
            }

            ApplyRestoredScrollOffsets();
        }
    }

    private async void RemoveRecentDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentDocumentItemViewModel recentDocument })
        {
            await _viewModel.RemoveRecentDocumentAsync(recentDocument, CancellationToken.None);
        }
    }

    private async void ClearRecentDocumentsButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空最近文件列表吗？这不会删除 PDF 文件和阅读位置。",
            "清空最近文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.ClearRecentDocumentsAsync(CancellationToken.None);
        }
    }

    private void RecentDocumentsPanel_OpenDocumentRequested(object? sender, EventArgs e) =>
        OpenButton_Click(sender!, new RoutedEventArgs());

    private async void RecentDocumentsPanel_RestoreSessionRequested(object? sender, EventArgs e)
    {
        await _viewModel.RestoreLastSessionAsync(CancellationToken.None);
        ApplyRestoredScrollOffsets();
    }

    private void RecentDocumentsPanel_RecentDocumentRequested(object? sender, RecentDocumentRequestedEventArgs e) =>
        OpenRecentDocumentAsync(e.Document);

    private void RecentDocumentsPanel_RemoveRecentDocumentRequested(object? sender, RecentDocumentRequestedEventArgs e) =>
        RemoveRecentDocumentAsync(e.Document);

    private void RecentDocumentsPanel_ClearRecentDocumentsRequested(object? sender, EventArgs e) =>
        ClearRecentDocumentsButton_Click(sender!, new RoutedEventArgs());

    private async void OpenRecentDocumentAsync(RecentDocumentItemViewModel recentDocument)
    {
        if (!recentDocument.IsMissing && File.Exists(recentDocument.FullPath))
        {
            await OpenDocumentWithPasswordPromptAsync(recentDocument.FullPath);
        }
        else
        {
            await _viewModel.OpenRecentDocumentAsync(recentDocument, CancellationToken.None);
        }

        ApplyRestoredScrollOffsets();
    }

    private async void RemoveRecentDocumentAsync(RecentDocumentItemViewModel recentDocument) =>
        await _viewModel.RemoveRecentDocumentAsync(recentDocument, CancellationToken.None);

    private async void ReopenClosedTabButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ReopenLastClosedTabAsync(CancellationToken.None);
        ApplyRestoredScrollOffsets();
    }

    private async void DocumentTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTabItemViewModel tab })
        {
            await _viewModel.ActivateDocumentTabAsync(tab, CancellationToken.None);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void CloseDocumentTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTabItemViewModel tab })
        {
            await _viewModel.CloseDocumentTabAsync(tab, CancellationToken.None);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void SearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchResultItemViewModel item })
        {
            await _viewModel.NavigateToSearchResultAsync(item, CancellationToken.None);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void OutlineItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentOutlineItemViewModel item })
        {
            await _viewModel.NavigateToOutlineItemAsync(item, CancellationToken.None);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void ThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentThumbnailViewModel thumbnail })
        {
            await _viewModel.NavigateToThumbnailAsync(thumbnail, CancellationToken.None);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void ThumbnailButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentThumbnailViewModel thumbnail })
        {
            await _viewModel.LoadThumbnailAsync(thumbnail, CancellationToken.None);
        }
    }

    private async void NavigateAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AnnotationItemViewModel item })
        {
            await _viewModel.NavigateToAnnotationAsync(item, CancellationToken.None);
            ApplyRestoredScrollOffsets();
        }
    }

    private async void SaveAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AnnotationItemViewModel item })
        {
            await _viewModel.SaveAnnotationChangesAsync(item, CancellationToken.None);
        }
    }

    private async void DeleteAnnotationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AnnotationItemViewModel item })
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "确定删除这条本地批注吗？这不会修改原 PDF 文件。",
            "删除批注",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteAnnotationAsync(item, CancellationToken.None);
        }
    }

    private async void SavePdfAnnotationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ActiveDocumentTab is not { } tab)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "确定将当前本地批注写入这个 PDF 文件吗？保存前会先写入临时文件并重新验证。",
            "写入 PDF 批注",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await SavePdfAnnotationsAsync(tab.FullPath, PdfAnnotationSaveMode.Full);
    }

    private async void SaveAsPdfAnnotationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ActiveDocumentTab is not { } tab)
        {
            return;
        }

        await SaveAsPdfAnnotationsAsync(tab, useOriginalFileName: false);
    }

    private async Task SaveAsPdfAnnotationsAsync(
        DocumentTabItemViewModel tab,
        bool useOriginalFileName)
    {
        if (!await ConfirmPendingAnnotationChangesForPdfSaveAsync(tab))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = useOriginalFileName
                ? Path.GetFileName(tab.FullPath)
                : Path.GetFileNameWithoutExtension(tab.FullPath) + "-annotated.pdf",
            InitialDirectory = Path.GetDirectoryName(tab.FullPath),
            AddExtension = true,
            DefaultExt = ".pdf",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await SavePdfAnnotationsAsync(
                dialog.FileName,
                PdfAnnotationSaveMode.Full,
                confirmPendingAnnotationChanges: false);
        }
    }

    private async Task SavePdfAnnotationsAsync(
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode,
        bool confirmPendingAnnotationChanges = true)
    {
        try
        {
            if (confirmPendingAnnotationChanges &&
                _viewModel.ActiveDocumentTab is { } tab &&
                !await ConfirmPendingAnnotationChangesForPdfSaveAsync(tab))
            {
                return;
            }

            var result = await _viewModel.SaveActiveAnnotationsToPdfAsync(
                destinationFilePath,
                saveMode,
                CancellationToken.None);
            if (result is null)
            {
                MessageBox.Show(
                    this,
                    "当前文档没有可写入 PDF 的本地批注。",
                    "写入 PDF 批注",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "写入 PDF 批注失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<bool> ConfirmPendingAnnotationChangesForPdfSaveAsync(DocumentTabItemViewModel tab)
    {
        if (!_viewModel.TabHasPendingLocalAnnotationChanges(tab))
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "当前批注中有尚未保存到本地的颜色或笔记修改。是否先保存这些修改，并一起写入 PDF？\n\n是：先保存到本地，再写入 PDF\n否：仅写入已保存的批注\n取消：停止保存",
            "保存未保存的批注修改",
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

        var saved = await _viewModel.SavePendingLocalAnnotationChangesAsync(
            tab,
            CancellationToken.None);
        if (!saved)
        {
            MessageBox.Show(
                this,
                "未保存的批注修改没有成功写入本地数据库，本次 PDF 保存已停止。",
                "保存批注",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return saved;
    }

    private void DocumentTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(this);
        _draggedDocumentTab = (sender as FrameworkElement)?.DataContext as DocumentTabItemViewModel;
    }

    private void DocumentTab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedDocumentTab is null)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedTab = _draggedDocumentTab;
        _draggedDocumentTab = null;
        DragDrop.DoDragDrop((DependencyObject)sender, draggedTab, DragDropEffects.Move);
    }

    private void DocumentTab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DocumentTabItemViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void DocumentTab_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTabItemViewModel targetTab } &&
            e.Data.GetData(typeof(DocumentTabItemViewModel)) is DocumentTabItemViewModel sourceTab)
        {
            _viewModel.MoveDocumentTab(sourceTab, targetTab);
        }

        e.Handled = true;
    }

    private void OnReaderErrorOccurred(object? sender, ReaderErrorEventArgs e) =>
        MessageBox.Show(this, e.Error.DialogText, e.Error.Title, MessageBoxButton.OK, MessageBoxImage.Error);

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.CheckActiveDocumentExternalModification();
    }

    private async Task<OpenDocumentResult> OpenDocumentWithPasswordPromptAsync(string filePath)
    {
        string? password = null;
        var hasPrompted = false;
        while (true)
        {
            var result = await _viewModel.OpenDocumentAsync(filePath, password, CancellationToken.None);
            if (result != OpenDocumentResult.PasswordRequiredOrInvalid)
            {
                return result;
            }

            var prompt = new PasswordPromptWindow(Path.GetFileName(filePath), hasPrompted)
            {
                Owner = this
            };
            if (prompt.ShowDialog() != true)
            {
                return OpenDocumentResult.Cancelled;
            }

            password = prompt.Password;
            hasPrompted = true;
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settingsViewModel) { Owner = this };
        window.ShowDialog();
        await LoadKeyboardShortcutsAsync();
        await _viewModel.Translation.InitializeAsync(CancellationToken.None);
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsShortcutMatch(e, new KeyboardShortcutGesture(ModifierKeys.Control, Key.S)))
        {
            e.Handled = true;
            await SaveActiveAnnotationsAsOriginalNameAsync();
            return;
        }

        if (IsShortcutMatch(e, _searchShortcut))
        {
            await _viewModel.OpenSearchPanelFromSelectionAsync();
            _ = Dispatcher.BeginInvoke(() =>
            {
                SearchQueryTextBox.Focus();
                SearchQueryTextBox.SelectAll();
            });
            e.Handled = true;
            return;
        }

        if (IsShortcutMatch(e, _translationShortcut))
        {
            e.Handled = true;
            await _viewModel.TranslateSelectionAndOpenPanelAsync();
            return;
        }

        if (IsShortcutMatch(e, new KeyboardShortcutGesture(ModifierKeys.Control, Key.C)) &&
            !IsTextInputFocus() &&
            _viewModel.CopySelectionCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.CopySelectionCommand.Execute(null);
        }
    }

    private async Task SaveActiveAnnotationsAsOriginalNameAsync()
    {
        if (_viewModel.ActiveDocumentTab is not { } tab)
        {
            return;
        }

        await SaveAsPdfAnnotationsAsync(tab, useOriginalFileName: true);
    }

    private async Task LoadKeyboardShortcutsAsync()
    {
        if (_settingsService is null)
        {
            return;
        }

        try
        {
            var settings = await _settingsService.LoadAsync(CancellationToken.None);
            _searchShortcut = KeyboardShortcutGesture.TryParse(
                settings.Shortcuts.SearchShortcut,
                out var searchShortcut)
                ? searchShortcut
                : KeyboardShortcutGesture.DefaultSearch;
            _translationShortcut = KeyboardShortcutGesture.TryParse(
                settings.Shortcuts.TranslationShortcut,
                out var translationShortcut)
                ? translationShortcut
                : KeyboardShortcutGesture.DefaultTranslation;
        }
        catch
        {
            _searchShortcut = KeyboardShortcutGesture.DefaultSearch;
            _translationShortcut = KeyboardShortcutGesture.DefaultTranslation;
        }
    }

    private static bool IsShortcutMatch(KeyEventArgs e, KeyboardShortcutGesture shortcut)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == shortcut.Key &&
               (Keyboard.Modifiers & shortcut.Modifiers) == shortcut.Modifiers &&
               (Keyboard.Modifiers & ~(shortcut.Modifiers | ModifierKeys.None)) == ModifierKeys.None;
    }

    private static bool IsTextInputFocus()
    {
        var focused = Keyboard.FocusedElement;
        return focused is TextBoxBase or PasswordBox;
    }

    private void LeftSidebarHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _leftSidebarHandleStartPoint = e.GetPosition(this);
        _leftSidebarWidthAtDragStart = _viewModel.LeftSidebarWidth;
        _hasLeftSidebarHandleDragged = false;
        _isLeftSidebarHandleCaptured = true;
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }

        e.Handled = true;
    }

    private void LeftSidebarHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isLeftSidebarHandleCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this).X - _leftSidebarHandleStartPoint.X;
        if (Math.Abs(delta) < 3)
        {
            return;
        }

        _hasLeftSidebarHandleDragged = true;
        if (_viewModel.IsLeftSidebarOpen)
        {
            _viewModel.LeftSidebarWidth = _leftSidebarWidthAtDragStart + delta;
        }

        e.Handled = true;
    }

    private void LeftSidebarHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isLeftSidebarHandleCaptured)
        {
            return;
        }

        _isLeftSidebarHandleCaptured = false;
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }

        if (!_hasLeftSidebarHandleDragged)
        {
            _viewModel.IsLeftSidebarOpen = !_viewModel.IsLeftSidebarOpen;
        }

        e.Handled = true;
    }

    private void RightSidebarHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rightSidebarHandleStartPoint = e.GetPosition(this);
        _rightSidebarWidthAtDragStart = _viewModel.TranslationPanelWidth;
        _hasRightSidebarHandleDragged = false;
        _isRightSidebarHandleCaptured = true;
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }

        e.Handled = true;
    }

    private void RightSidebarHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRightSidebarHandleCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this).X - _rightSidebarHandleStartPoint.X;
        if (Math.Abs(delta) < 3)
        {
            return;
        }

        _hasRightSidebarHandleDragged = true;
        if (_viewModel.IsTranslationPanelOpen)
        {
            _viewModel.TranslationPanelWidth = _rightSidebarWidthAtDragStart - delta;
        }

        e.Handled = true;
    }

    private void RightSidebarHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRightSidebarHandleCaptured)
        {
            return;
        }

        _isRightSidebarHandleCaptured = false;
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }

        if (!_hasRightSidebarHandleDragged)
        {
            _viewModel.IsTranslationPanelOpen = !_viewModel.IsTranslationPanelOpen;
        }

        e.Handled = true;
    }

    private async void TranslateSelectionButton_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.TranslateSelectionAndOpenPanelAsync();

    private void PageNumberTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsAsciiDigit(character));

    private void PageNumberTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text ||
            text.Any(character => !char.IsAsciiDigit(character)))
        {
            e.CancelCommand();
        }
    }

    private void PdfViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double pageMargin = 48;
        _viewModel.UpdateViewportSize(
            Math.Max(0, e.NewSize.Width - pageMargin),
            Math.Max(0, e.NewSize.Height - pageMargin));
    }

    private void PdfViewport_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isApplyingRequestedScroll)
        {
            return;
        }

        _viewModel.UpdateScrollOffsets(e.HorizontalOffset, e.VerticalOffset);
    }

    private async void PdfViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || !_viewModel.HasDocument)
        {
            return;
        }

        e.Handled = true;
        var oldZoomFactor = _viewModel.ZoomFactor;
        if (oldZoomFactor <= 0)
        {
            return;
        }

        var pointer = e.GetPosition(PdfViewport);
        var anchorX = PdfViewport.HorizontalOffset + pointer.X;
        var anchorY = PdfViewport.VerticalOffset + pointer.Y;

        await _viewModel.ZoomByMouseWheelAsync(e.Delta);
        PdfViewport.UpdateLayout();

        var newZoomFactor = _viewModel.ZoomFactor;
        if (Math.Abs(newZoomFactor - oldZoomFactor) < 0.0001)
        {
            return;
        }

        var zoomRatio = newZoomFactor / oldZoomFactor;
        var horizontalOffset = Math.Max(0, anchorX * zoomRatio - pointer.X);
        var verticalOffset = Math.Max(0, anchorY * zoomRatio - pointer.Y);
        PdfViewport.ScrollToHorizontalOffset(horizontalOffset);
        PdfViewport.ScrollToVerticalOffset(verticalOffset);
    }

    private void ApplyRestoredScrollOffsets()
    {
        PdfViewport.UpdateLayout();
        PdfViewport.ScrollToHorizontalOffset(_viewModel.HorizontalScrollOffset);
        PdfViewport.ScrollToVerticalOffset(_viewModel.VerticalScrollOffset);
        Dispatcher.BeginInvoke(async () =>
        {
            PdfViewport.UpdateLayout();
            await _viewModel.RefreshVisibleRenderingAsync(CancellationToken.None);
        });
    }

    private async void PageSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<ButtonBase>(source) is not null || FindAncestor<ComboBox>(source) is not null))
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: DocumentPageViewModel page } pageSurface)
        {
            return;
        }

        var point = e.GetPosition(pageSurface);
        await _viewModel.BeginSelectionAsync(page.PageIndex, new ViewPoint(point.X, point.Y), CancellationToken.None);
    }

    private void PageSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<ButtonBase>(source) is not null || FindAncestor<ComboBox>(source) is not null))
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: DocumentPageViewModel page } pageSurface)
        {
            return;
        }

        var point = e.GetPosition(pageSurface);
        ShowSelectionContextMenu(pageSurface, page.PageIndex, new ViewPoint(point.X, point.Y));
        e.Handled = true;
    }

    private async void PageSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement { DataContext: DocumentPageViewModel page } pageSurface)
        {
            return;
        }

        var point = e.GetPosition(pageSurface);
        await _viewModel.UpdateSelectionAsync(page.PageIndex, new ViewPoint(point.X, point.Y), CancellationToken.None);
    }

    private void PageSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.CompleteSelection();
        if (Mouse.Captured is UIElement captured)
        {
            captured.ReleaseMouseCapture();
        }
    }

    private void PageSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        _viewModel.HideSelectionToolbarPreservingSelection();
    }

    private void ShowSelectionContextMenu(FrameworkElement placementTarget, int pageIndex, ViewPoint point)
    {
        _viewModel.HideSelectionToolbar();
        if (_viewModel.CurrentSelection is null || !_viewModel.HasSelectionOnPage(pageIndex))
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.MousePoint
        };

        var translateItem = new MenuItem { Header = "翻译选区" };
        translateItem.Click += TranslateSelectionButton_Click;
        menu.Items.Add(translateItem);
        menu.Items.Add(new MenuItem
        {
            Header = "复制原文",
            Command = _viewModel.CopySelectionCommand
        });
        menu.Items.Add(new MenuItem
        {
            Header = "复制 PDF 原始文字",
            Command = _viewModel.CopyRawSelectionCommand
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "高亮",
            Command = _viewModel.CreateHighlightCommand
        });
        menu.Items.Add(new MenuItem
        {
            Header = "高亮并记笔记",
            Command = _viewModel.CreateHighlightWithNoteCommand
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "复制译文",
            Command = _viewModel.Translation.CopyTranslationCommand
        });

        var searchItem = new MenuItem { Header = "用选区搜索" };
        searchItem.Click += async (_, _) =>
        {
            await _viewModel.OpenSearchPanelFromSelectionAsync();
            _ = Dispatcher.BeginInvoke(() =>
            {
                SearchQueryTextBox.Focus();
                SearchQueryTextBox.SelectAll();
            });
        };
        menu.Items.Add(searchItem);
        menu.IsOpen = true;
    }

    private static T? FindAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = LogicalTreeHelper.GetParent(current) ?? System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

internal sealed record KeyboardShortcutGesture(ModifierKeys Modifiers, Key Key)
{
    public static KeyboardShortcutGesture DefaultSearch { get; } =
        new(ModifierKeys.Control, Key.F);

    public static KeyboardShortcutGesture DefaultTranslation { get; } =
        new(ModifierKeys.Control, Key.T);

    public static bool TryParse(string? shortcut, out KeyboardShortcutGesture gesture)
    {
        gesture = DefaultSearch;
        shortcut = (shortcut ?? string.Empty).Trim();
        if (shortcut.Length == 0)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var rawPart in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Equals("Control", StringComparison.OrdinalIgnoreCase)
                ? "Ctrl"
                : rawPart;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
                continue;
            }

            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
                continue;
            }

            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
                continue;
            }

            if (key is not null || !Enum.TryParse<Key>(NormalizeKeyName(part), ignoreCase: true, out var parsedKey))
            {
                return false;
            }

            key = parsedKey;
        }

        if ((modifiers & ModifierKeys.Control) != ModifierKeys.Control || key is null)
        {
            return false;
        }

        gesture = new KeyboardShortcutGesture(modifiers, key.Value);
        return true;
    }

    private static string NormalizeKeyName(string key)
    {
        key = key.Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key.ToUpperInvariant();
        }

        return key.Equals("Esc", StringComparison.OrdinalIgnoreCase)
            ? "Escape"
            : key;
    }
}
