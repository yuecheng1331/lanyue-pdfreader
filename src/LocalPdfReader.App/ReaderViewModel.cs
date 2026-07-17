using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using LocalPdfReader.App.Commands;
using LocalPdfReader.Application.Annotations;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.App;

public sealed class ReaderViewModel : INotifyPropertyChanged
{
    private const double SelectionDragThreshold = 4;
    private const double PageGap = 16;
    private const double DoublePageGap = 16;
    private const int MaximumCachedThumbnailImages = 32;
    private const double ThumbnailMaximumWidth = 150;
    private const double ThumbnailMaximumHeight = 210;
    private const double NormalRenderClarityScale = 1.5;
    private static readonly IReadOnlyDictionary<AnnotationColor, Brush> AnnotationBrushes =
        new Dictionary<AnnotationColor, Brush>
        {
            [AnnotationColor.Yellow] = CreateFrozenBrush(0x88, 0xFF, 0xD5, 0x4F),
            [AnnotationColor.Green] = CreateFrozenBrush(0x78, 0x66, 0xBB, 0x6A),
            [AnnotationColor.Blue] = CreateFrozenBrush(0x70, 0x42, 0xA5, 0xF5),
            [AnnotationColor.Pink] = CreateFrozenBrush(0x78, 0xEC, 0x7C, 0xB5)
        };

    private readonly IPdfWorkerClient _pdfWorkerClient;
    private ReaderState _readerState;
    private readonly ICoordinateTransformer _coordinateTransformer;
    private readonly TextSelectionService _textSelectionService;
    private readonly IUserErrorService _userErrorService;
    private readonly DocumentHistoryService? _documentHistoryService;
    private readonly DocumentSessionService? _documentSessionService;
    private readonly IAnnotationService? _annotationService;
    private readonly IPdfAnnotationSyncService? _pdfAnnotationSyncService;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger<ReaderViewModel>? _logger;
    private readonly PageRenderCoordinator _pageRenderCoordinator = new();
    private readonly AsyncCommand[] _readerCommands;
    private ImageSource? _pageImage;
    private double _pageDisplayWidth;
    private double _pageDisplayHeight;
    private double _documentCanvasWidth;
    private double _documentCanvasHeight;
    private string _statusText = "请选择一个 PDF 文件。";
    private string _pageNumberText = string.Empty;
    private bool _isOpening;
    private int _activeRenderCount;
    private CancellationTokenSource? _viewportRenderCancellationSource;
    private readonly Dictionary<(DocumentId DocumentId, int PageIndex), PageTextData> _pageTextCache = [];
    private readonly LinkedList<(DocumentId DocumentId, int PageIndex)> _pageTextCacheOrder = [];
    private PageTextData? _selectionPageText;
    private TextSelection? _currentSelection;
    private readonly List<TextSelection> _currentSelectionParts = [];
    private int? _selectionPageIndex;
    private int? _selectionStartPageIndex;
    private int? _selectionStartCharacterIndex;
    private ViewPoint? _selectionStartViewPoint;
    private bool _isPointerSelecting;
    private bool _hasSelectionDragStarted;
    private bool _selectionChangedInCurrentGesture;
    private DateTime _lastSelectionUpdate = DateTime.MinValue;
    private CancellationTokenSource? _textSelectionCancellationSource;
    private bool _isWorkerAvailable = true;
    private readonly ReadingStateCoordinator _readingStateCoordinator = new(TimeSpan.FromSeconds(1));
    private readonly ReadingViewportState _homeViewportState = new();
    private readonly ObservableCollection<DocumentPageViewModel> _homePages = [];
    private readonly HashSet<(DocumentId DocumentId, int PageIndex)> _renderingPages = [];
    private readonly HashSet<(DocumentId DocumentId, int PageIndex, PageRotation Rotation)> _renderingThumbnails = [];
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _isInitialized;
    private DocumentTabItemViewModel? _activeDocumentTab;
    private AnnotationColor _selectedAnnotationColor = AnnotationColor.Yellow;
    private bool _isLeftSidebarOpen = true;
    private bool _isTranslationPanelOpen;
    private double _leftSidebarWidth = 290;
    private double _translationPanelWidth = 350;
    private string _zoomText = "100%";
    private double _selectionToolbarX;
    private double _selectionToolbarY;
    private bool _isSelectionToolbarVisible;
    private int _leftSidebarIndex;
    private bool _hasRestorableSession;
    private Guid? _emphasizedAnnotationId;
    private CancellationTokenSource? _annotationEmphasisCancellationSource;

    public ReaderViewModel(
        IPdfWorkerClient pdfWorkerClient,
        ReaderState readerState,
        ICoordinateTransformer coordinateTransformer,
        TextSelectionService textSelectionService,
        TranslationPanelViewModel translation,
        IUserErrorService userErrorService,
        DocumentHistoryService? documentHistoryService = null,
        ILogger<ReaderViewModel>? logger = null,
        IAnnotationService? annotationService = null,
        ISettingsService? settingsService = null,
        DocumentSessionService? documentSessionService = null,
        IPdfAnnotationSyncService? pdfAnnotationSyncService = null)
    {
        _pdfWorkerClient = pdfWorkerClient;
        _readerState = readerState;
        _coordinateTransformer = coordinateTransformer;
        _textSelectionService = textSelectionService;
        _userErrorService = userErrorService;
        _documentHistoryService = documentHistoryService;
        _logger = logger;
        _annotationService = annotationService;
        _pdfAnnotationSyncService = pdfAnnotationSyncService;
        _settingsService = settingsService;
        _documentSessionService = documentSessionService;
        Translation = translation;

        PreviousPageCommand = new AsyncCommand(MoveToPreviousPageAsync, () => CanMoveToPreviousPage);
        NextPageCommand = new AsyncCommand(MoveToNextPageAsync, () => CanMoveToNextPage);
        GoToPageCommand = new AsyncCommand(GoToPageAsync, () => CanUseDocumentControls);
        ZoomOutCommand = new AsyncCommand(ZoomOutAsync, () => CanZoomOut);
        ZoomInCommand = new AsyncCommand(ZoomInAsync, () => CanZoomIn);
        ApplyZoomCommand = new AsyncCommand(ApplyZoomAsync, () => CanUseDocumentControls);
        FitPageCommand = new AsyncCommand(FitPageAsync, () => CanFitPage);
        FitWidthCommand = new AsyncCommand(FitWidthAsync, () => CanFitPage);
        RotateClockwiseCommand = new AsyncCommand(RotateClockwiseAsync, () => CanUseDocumentControls);
        SinglePageModeCommand = new AsyncCommand(() => SetReadingModeAsync(ReadingMode.SinglePage), () => CanUseDocumentControls);
        DoublePageModeCommand = new AsyncCommand(() => SetReadingModeAsync(ReadingMode.DoublePage), () => CanUseDocumentControls);
        CopySelectionCommand = new AsyncCommand(CopySelectionAsync, () => CurrentSelection is not null);
        CopyRawSelectionCommand = new AsyncCommand(CopyRawSelectionAsync, () => CurrentSelection is not null);
        CreateHighlightCommand = new AsyncCommand(
            () => CreateHighlightAsync(openNoteEditor: false),
            CanCreateHighlight);
        CreateHighlightWithNoteCommand = new AsyncCommand(
            () => CreateHighlightAsync(openNoteEditor: true),
            CanCreateHighlight);
        _readerCommands =
        [
            (AsyncCommand)PreviousPageCommand,
            (AsyncCommand)NextPageCommand,
            (AsyncCommand)GoToPageCommand,
            (AsyncCommand)ZoomOutCommand,
            (AsyncCommand)ZoomInCommand,
            (AsyncCommand)ApplyZoomCommand,
            (AsyncCommand)FitPageCommand,
            (AsyncCommand)FitWidthCommand,
            (AsyncCommand)RotateClockwiseCommand,
            (AsyncCommand)SinglePageModeCommand,
            (AsyncCommand)DoublePageModeCommand,
            (AsyncCommand)CopySelectionCommand,
            (AsyncCommand)CopyRawSelectionCommand,
            (AsyncCommand)CreateHighlightCommand,
            (AsyncCommand)CreateHighlightWithNoteCommand
        ];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<ReaderErrorEventArgs>? ErrorOccurred;

    public event EventHandler<ScrollOffsetRequestedEventArgs>? ScrollOffsetRequested;

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public ICommand GoToPageCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ApplyZoomCommand { get; }

    public ICommand FitPageCommand { get; }

    public ICommand FitWidthCommand { get; }

    public ICommand RotateClockwiseCommand { get; }

    public ICommand SinglePageModeCommand { get; }

    public ICommand DoublePageModeCommand { get; }

    public ICommand CopySelectionCommand { get; }

    public ICommand CopyRawSelectionCommand { get; }

    public ICommand CreateHighlightCommand { get; }

    public ICommand CreateHighlightWithNoteCommand { get; }

    public ObservableCollection<SelectionRectangleViewModel> SelectionRectangles { get; } = [];

    public ObservableCollection<SelectionRectangleViewModel> SearchHighlightRectangles { get; } = [];

    public ObservableCollection<AnnotationRectangleViewModel> AnnotationHighlightRectangles { get; } = [];

    public ObservableCollection<RecentDocumentItemViewModel> RecentDocuments { get; } = [];

    public ObservableCollection<DocumentTabItemViewModel> DocumentTabs { get; } = [];

    public ObservableCollection<RecentlyClosedTabItemViewModel> RecentlyClosedTabs { get; } = [];

    public ObservableCollection<DocumentPageViewModel> DocumentPages =>
        ActiveDocumentTab?.Pages ?? _homePages;

    public double DocumentCanvasWidth => _documentCanvasWidth;

    public double DocumentCanvasHeight => _documentCanvasHeight;

    public bool IsSinglePageMode => ActiveViewportState.Mode == ReadingMode.SinglePage;

    public bool IsDoublePageMode => ActiveViewportState.Mode == ReadingMode.DoublePage;

    public bool CanReopenClosedTab => RecentlyClosedTabs.Count > 0;

    public bool HasRestorableSession
    {
        get => _hasRestorableSession;
        private set => SetProperty(ref _hasRestorableSession, value);
    }

    public DocumentTabItemViewModel? ActiveDocumentTab
    {
        get => _activeDocumentTab;
        private set
        {
            if (ReferenceEquals(_activeDocumentTab, value))
            {
                return;
            }

            if (_activeDocumentTab is not null)
            {
                _activeDocumentTab.IsActive = false;
            }

            _activeDocumentTab = value;
            if (_activeDocumentTab is not null)
            {
                _activeDocumentTab.IsActive = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(DocumentPages));
            OnPropertyChanged(nameof(ActiveNavigation));
        }
    }

    public TranslationPanelViewModel Translation { get; }

    public DocumentNavigationViewModel? ActiveNavigation => ActiveDocumentTab?.Navigation;

    public TextSelection? CurrentSelection
    {
        get => _currentSelection;
        private set
        {
            if (SetProperty(ref _currentSelection, value))
            {
                Translation.SetSourceText(value?.NormalizedText ?? string.Empty);
                OnPropertyChanged(nameof(HasSelection));
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => CurrentSelection is not null;

    public bool IsLeftSidebarOpen
    {
        get => _isLeftSidebarOpen;
        set
        {
            if (SetProperty(ref _isLeftSidebarOpen, value) && !value)
            {
                ClearSearchSelectionHighlight();
            }
        }
    }

    public bool IsTranslationPanelOpen
    {
        get => _isTranslationPanelOpen;
        set => SetProperty(ref _isTranslationPanelOpen, value);
    }

    public double LeftSidebarWidth
    {
        get => _leftSidebarWidth;
        set => SetProperty(ref _leftSidebarWidth, Math.Clamp(value, 220, 520));
    }

    public double TranslationPanelWidth
    {
        get => _translationPanelWidth;
        set => SetProperty(ref _translationPanelWidth, Math.Clamp(value, 280, 620));
    }

    public bool IsSelectionToolbarVisible
    {
        get => _isSelectionToolbarVisible;
        private set => SetProperty(ref _isSelectionToolbarVisible, value);
    }

    public double ZoomFactor => _readerState.ZoomFactor;

    public string ZoomText
    {
        get => _zoomText;
        set => SetProperty(ref _zoomText, value ?? string.Empty);
    }

    public IReadOnlyList<AnnotationColorOptionViewModel> AnnotationColorOptions =>
        AnnotationColorOptionViewModel.All;

    public AnnotationColor SelectedAnnotationColor
    {
        get => _selectedAnnotationColor;
        set
        {
            if (SetProperty(ref _selectedAnnotationColor, value))
            {
                RaiseCommandCanExecuteChanged();
            }
        }
    }

    public double SelectionToolbarX
    {
        get => _selectionToolbarX;
        private set => SetProperty(ref _selectionToolbarX, value);
    }

    public double SelectionToolbarY
    {
        get => _selectionToolbarY;
        private set => SetProperty(ref _selectionToolbarY, value);
    }

    public int LeftSidebarIndex
    {
        get => _leftSidebarIndex;
        set => SetProperty(ref _leftSidebarIndex, Math.Clamp(value, 0, 3));
    }

    public void OpenLeftSidebar(int tabIndex)
    {
        LeftSidebarIndex = tabIndex;
        IsLeftSidebarOpen = true;
    }

    public void OpenSearchPanel() => OpenLeftSidebar(2);

    public async Task OpenSearchPanelFromSelectionAsync()
    {
        OpenSearchPanel();
        if (ActiveDocumentTab?.Search is not { } search ||
            CurrentSelection is not { } selection)
        {
            return;
        }

        var query = NormalizeSearchQuery(selection.NormalizedText);
        if (query.Length == 0)
        {
            return;
        }

        search.Query = query;
        if (search.CanSearch)
        {
            await search.StartSearchAsync();
        }
    }

    public async Task TranslateSelectionAndOpenPanelAsync()
    {
        IsTranslationPanelOpen = true;
        await Translation.TranslateAsync();
    }

    private static string NormalizeSearchQuery(string text) =>
        string.Join(
            ' ',
            (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Trim();

    public ImageSource? PageImage
    {
        get => _pageImage;
        private set
        {
            if (SetProperty(ref _pageImage, value))
            {
                UpdatePageDisplaySize();
            }
        }
    }

    public double PageDisplayWidth => _pageDisplayWidth;

    public double PageDisplayHeight => _pageDisplayHeight;

    private void UpdatePageDisplaySize()
    {
        var newWidth = 0d;
        var newHeight = 0d;
        if (PageImage is not null && _readerState.CurrentPageSize is { } pageSize)
        {
            var bounds = ReadingViewLayout.CalculatePageBounds(
                pageSize,
                _readerState.Rotation,
                _readerState.ZoomFactor);
            newWidth = bounds.Width;
            newHeight = bounds.Height;
        }

        if (Math.Abs(_pageDisplayWidth - newWidth) >= 0.1)
        {
            _pageDisplayWidth = newWidth;
            OnPropertyChanged(nameof(PageDisplayWidth));
        }

        if (Math.Abs(_pageDisplayHeight - newHeight) >= 0.1)
        {
            _pageDisplayHeight = newHeight;
            OnPropertyChanged(nameof(PageDisplayHeight));
        }
    }

    private void EnsureDocumentPages(ReaderState readerState, PdfSize defaultPageSize)
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return;
        }

        while (tab.Pages.Count < readerState.PageCount)
        {
            tab.Pages.Add(new DocumentPageViewModel(tab.Pages.Count, defaultPageSize));
        }

        while (tab.Pages.Count > readerState.PageCount)
        {
            tab.Pages.RemoveAt(tab.Pages.Count - 1);
        }

        tab.Navigation.EnsureThumbnails(readerState.PageCount);
        UpdateDocumentPageLayouts(readerState);
    }

    private void UpdateDocumentPageLayouts(ReaderState readerState)
    {
        if (ActiveDocumentTab is not { } tab || tab.Pages.Count == 0)
        {
            SetDocumentCanvasSize(0, 0);
            return;
        }

        if (tab.ViewportState.Mode == ReadingMode.DoublePage)
        {
            UpdateDoublePageLayouts(tab, readerState);
        }
        else
        {
            UpdateSinglePageLayouts(tab, readerState);
        }
    }

    private void UpdateSinglePageLayouts(DocumentTabItemViewModel tab, ReaderState readerState)
    {
        var pageBounds = tab.Pages
            .Select(page => (
                Page: page,
                Bounds: ReadingViewLayout.CalculatePageBounds(
                    page.PdfPageSize,
                    readerState.Rotation,
                    readerState.ZoomFactor)))
            .ToArray();
        var canvasWidth = Math.Max(
            ActiveViewportState.ViewportWidth,
            pageBounds.Length == 0 ? 0 : pageBounds.Max(item => item.Bounds.Width));
        var pageTop = 0d;
        foreach (var item in pageBounds)
        {
            var pageLeft = Math.Max(0, (canvasWidth - item.Bounds.Width) / 2);
            item.Page.UpdateLayout(
                item.Page.PdfPageSize,
                readerState.Rotation,
                readerState.ZoomFactor,
                pageLeft,
                pageTop);
            item.Page.IsCurrentPage = item.Page.PageIndex == readerState.CurrentPageIndex;
            pageTop += item.Page.DisplayHeight + PageGap;
        }

        tab.Navigation.SetCurrentPage(readerState.CurrentPageIndex);
        SetDocumentCanvasSize(canvasWidth, Math.Max(0, pageTop - PageGap));
    }

    private void UpdateDoublePageLayouts(DocumentTabItemViewModel tab, ReaderState readerState)
    {
        var pageBounds = tab.Pages
            .Select(page => (
                Page: page,
                Bounds: ReadingViewLayout.CalculatePageBounds(
                    page.PdfPageSize,
                    readerState.Rotation,
                    readerState.ZoomFactor)))
            .ToArray();
        var spreadWidths = pageBounds
            .Chunk(2)
            .Select(spread => spread.Sum(item => item.Bounds.Width) + (spread.Length > 1 ? DoublePageGap : 0))
            .ToArray();
        var canvasWidth = Math.Max(
            ActiveViewportState.ViewportWidth,
            spreadWidths.Length == 0 ? 0 : spreadWidths.Max());
        var pageTop = 0d;

        foreach (var spread in pageBounds.Chunk(2))
        {
            var spreadWidth = spread.Sum(item => item.Bounds.Width) + (spread.Length > 1 ? DoublePageGap : 0);
            var spreadHeight = spread.Max(item => item.Bounds.Height);
            var pageLeft = Math.Max(0, (canvasWidth - spreadWidth) / 2);

            foreach (var item in spread)
            {
                item.Page.UpdateLayout(
                    item.Page.PdfPageSize,
                    readerState.Rotation,
                    readerState.ZoomFactor,
                    pageLeft,
                    pageTop + Math.Max(0, (spreadHeight - item.Bounds.Height) / 2));
                item.Page.IsCurrentPage = item.Page.PageIndex == readerState.CurrentPageIndex;
                pageLeft += item.Page.DisplayWidth + DoublePageGap;
            }

            pageTop += spreadHeight + PageGap;
        }

        tab.Navigation.SetCurrentPage(readerState.CurrentPageIndex);
        SetDocumentCanvasSize(canvasWidth, Math.Max(0, pageTop - PageGap));
    }

    private void SetDocumentCanvasSize(double width, double height)
    {
        if (Math.Abs(_documentCanvasWidth - width) >= 0.1)
        {
            _documentCanvasWidth = width;
            OnPropertyChanged(nameof(DocumentCanvasWidth));
        }

        if (Math.Abs(_documentCanvasHeight - height) >= 0.1)
        {
            _documentCanvasHeight = height;
            OnPropertyChanged(nameof(DocumentCanvasHeight));
        }
    }

    private void InvalidateActivePageImages()
    {
        if (ActiveDocumentTab is { } tab)
        {
            foreach (var page in tab.Pages)
            {
                page.ClearImage();
            }
        }

        PageImage = null;
    }

    private void SetRenderedPage(
        ReaderState readerState,
        int pageIndex,
        CachedPageBitmap page)
    {
        EnsureDocumentPages(readerState, page.OriginalPageSize);
        if (ActiveDocumentTab is not { } tab ||
            pageIndex < 0 ||
            pageIndex >= tab.Pages.Count)
        {
            return;
        }

        var pageItem = tab.Pages[pageIndex];
        pageItem.UpdateLayout(
            page.OriginalPageSize,
            readerState.Rotation,
            readerState.ZoomFactor,
            pageItem.Bounds.X,
            pageItem.Bounds.Y);
        pageItem.SetImage(page.Bitmap);
        UpdateDocumentPageLayouts(readerState);

        if (pageIndex == readerState.CurrentPageIndex)
        {
            PageImage = page.Bitmap;
            UpdatePageDisplaySize();
        }
    }

    private bool UpdateCurrentPageFromViewport()
    {
        if (ActiveDocumentTab is not { } tab || tab.Pages.Count == 0 || !_readerState.HasDocument)
        {
            return false;
        }

        var pageIndex = ReadingViewLayout.GetDominantVisiblePageIndex(
            tab.Pages.Select(page => new ReadingPageLayout(
                page.PageIndex,
                page.PdfPageSize,
                _readerState.Rotation,
                page.Bounds)).ToArray(),
            ActiveViewportState.VisibleArea,
            _readerState.CurrentPageIndex);
        if (pageIndex == _readerState.CurrentPageIndex)
        {
            return false;
        }

        var changed = _readerState.SetPageNumber(pageIndex + 1);
        if (changed && pageIndex >= 0 && pageIndex < tab.Pages.Count)
        {
            SetCurrentPageSizeForReadingMode(_readerState, tab.Pages[pageIndex].PdfPageSize);
            PageImage = tab.Pages[pageIndex].Image;
            UpdateDocumentPageLayouts(_readerState);
            RebuildSelectionRectangles();
            RebuildSearchHighlightRectangles();
            RebuildAnnotationHighlightRectangles();
            NotifyReaderStateChanged();
        }

        return changed;
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string PageNumberText
    {
        get => _pageNumberText;
        set => SetProperty(ref _pageNumberText, value);
    }

    public string PageCountText => HasDocument ? $"/ {_readerState.PageCount}" : "/ 0";

    public bool IsOpening
    {
        get => _isOpening;
        private set
        {
            if (SetProperty(ref _isOpening, value))
            {
                OnPropertyChanged(nameof(CanOpenDocument));
            }
        }
    }

    public bool IsRendering => _activeRenderCount > 0;

    public bool IsWorkerAvailable => _isWorkerAvailable;

    public bool CanOpenDocument => IsWorkerAvailable && !IsOpening;

    public bool HasDocument => _readerState.HasDocument;

    public bool HasNoDocument => !HasDocument;

    public double HorizontalScrollOffset => ActiveViewportState.HorizontalOffset;

    public double VerticalScrollOffset => ActiveViewportState.VerticalOffset;

    private ReadingViewportState ActiveViewportState =>
        ActiveDocumentTab?.ViewportState ?? _homeViewportState;

    public bool CanUseDocumentControls => IsWorkerAvailable && HasDocument;

    public bool CanMoveToPreviousPage => IsWorkerAvailable && HasDocument && _readerState.CurrentPageIndex > 0;

    public bool CanMoveToNextPage => IsWorkerAvailable && HasDocument && _readerState.CurrentPageIndex < _readerState.PageCount - 1;

    public bool CanZoomOut => IsWorkerAvailable && HasDocument && _readerState.ZoomFactor > ReaderState.MinimumZoomFactor;

    public bool CanZoomIn => IsWorkerAvailable && HasDocument && _readerState.ZoomFactor < ReaderState.MaximumZoomFactor;

    public bool CanFitPage => IsWorkerAvailable && HasDocument && _readerState.CurrentPageSize is not null &&
        _readerState.ViewportWidth > 0 && _readerState.ViewportHeight > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        if (_settingsService is not null)
        {
            try
            {
                var settings = await _settingsService.LoadAsync(cancellationToken);
                if (Enum.TryParse<AnnotationColor>(
                    settings.Annotations.DefaultColor,
                    ignoreCase: true,
                    out var defaultColor) && Enum.IsDefined(defaultColor))
                {
                    SelectedAnnotationColor = defaultColor;
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "The default annotation color could not be loaded.");
            }
        }

        await RefreshRecentDocumentsAsync(cancellationToken);
        await UpdateRestorableSessionAvailabilityAsync(cancellationToken);
    }

    public async Task OpenRecentDocumentAsync(
        RecentDocumentItemViewModel recentDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recentDocument);
        if (recentDocument.IsMissing || !File.Exists(recentDocument.FullPath))
        {
            await RemoveMissingRecentDocumentAsync(recentDocument.DocumentId, cancellationToken);
            StatusText = "该历史 PDF 已被移动、删除或无法访问，已从最近文件列表移除；这不会影响其他文档的会话恢复。";
            return;
        }

        await OpenDocumentAsync(recentDocument.FullPath, cancellationToken);
    }

    public async Task RemoveRecentDocumentAsync(
        RecentDocumentItemViewModel recentDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recentDocument);
        if (_documentHistoryService is null)
        {
            return;
        }

        await _documentHistoryService.RemoveRecentAsync(recentDocument.DocumentId, cancellationToken);
        await RefreshRecentDocumentsAsync(cancellationToken);
    }

    public async Task ClearRecentDocumentsAsync(CancellationToken cancellationToken)
    {
        if (_documentHistoryService is null)
        {
            return;
        }

        await _documentHistoryService.ClearRecentAsync(cancellationToken);
        await RefreshRecentDocumentsAsync(cancellationToken);
    }

    public async Task ReopenLastClosedTabAsync(CancellationToken cancellationToken)
    {
        if (RecentlyClosedTabs.Count == 0)
        {
            return;
        }

        var closedTab = RecentlyClosedTabs[0];
        RecentlyClosedTabs.RemoveAt(0);
        OnPropertyChanged(nameof(CanReopenClosedTab));

        if (!File.Exists(closedTab.FullPath))
        {
            if (closedTab.DocumentId is { } documentId)
            {
                await RemoveMissingRecentDocumentAsync(documentId, cancellationToken);
            }

            StatusText = "已跳过一个已移动、删除或无法访问的关闭标签；可继续恢复其他关闭的文档。";
            return;
        }

        var existingTab = DocumentTabs.FirstOrDefault(tab =>
            string.Equals(tab.FullPath, closedTab.FullPath, StringComparison.OrdinalIgnoreCase));
        if (existingTab is not null)
        {
            await ActivateDocumentTabAsync(existingTab, cancellationToken);
            return;
        }

        await OpenDocumentAsync(closedTab.FullPath, cancellationToken);
        if (!DocumentTabs.Any(tab => string.Equals(tab.FullPath, closedTab.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            RecentlyClosedTabs.Insert(0, closedTab);
            OnPropertyChanged(nameof(CanReopenClosedTab));
        }
    }

    public async Task RestoreLastSessionAsync(CancellationToken cancellationToken)
    {
        if (_documentSessionService is null)
        {
            return;
        }

        DocumentSessionSnapshot? snapshot;
        try
        {
            snapshot = await _documentSessionService.GetAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "The saved document session could not be loaded.");
            return;
        }

        if (snapshot is null)
        {
            HasRestorableSession = false;
            return;
        }

        var restoredTabs = new List<(int OriginalIndex, DocumentTabItemViewModel Tab)>();
        var skippedMissingDocuments = 0;
        for (var index = 0; index < snapshot.Tabs.Count; index++)
        {
            var savedTab = snapshot.Tabs[index];
            if (savedTab.IsMissing || !File.Exists(savedTab.FilePath))
            {
                skippedMissingDocuments++;
                await RemoveMissingRecentDocumentAsync(savedTab.DocumentId, cancellationToken);
                continue;
            }

            await OpenDocumentAsync(savedTab.FilePath, cancellationToken);
            var restoredTab = DocumentTabs.FirstOrDefault(tab =>
                string.Equals(tab.FullPath, savedTab.FilePath, StringComparison.OrdinalIgnoreCase));
            if (restoredTab is not null)
            {
                restoredTabs.Add((index, restoredTab));
            }
        }

        var activeTab = restoredTabs.FirstOrDefault(item => item.OriginalIndex == snapshot.ActiveTabIndex).Tab
            ?? restoredTabs.LastOrDefault().Tab;
        if (activeTab is not null)
        {
            await ActivateDocumentTabAsync(activeTab, cancellationToken);
        }

        await SaveSessionAsync(cancellationToken);
        await UpdateRestorableSessionAvailabilityAsync(cancellationToken);
        if (skippedMissingDocuments > 0)
        {
            StatusText = restoredTabs.Count > 0
                ? $"已恢复 {restoredTabs.Count} 个文档；{skippedMissingDocuments} 个已移动或删除的 PDF 已跳过并从历史记录移除。"
                : "上次会话中的 PDF 已移动、删除或无法访问，已跳过并从历史记录移除。";
        }
    }

    public async Task CloseDocumentTabAsync(
        DocumentTabItemViewModel tab,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var tabIndex = DocumentTabs.IndexOf(tab);
        if (tabIndex < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(ActiveDocumentTab, tab);
        if (wasActive)
        {
            CancelAnnotationEmphasis();
            await Translation.CancelAsync();
            await SaveReadingStateNowAsync(cancellationToken);
            CaptureActiveTabState();
        }
        else
        {
            await SaveReadingStateForTabAsync(tab, cancellationToken);
        }

        if (tab.ReaderState.DocumentInfo is { } documentInfo)
        {
            await tab.Search.CancelAndWaitAsync();
            tab.Search.SelectedResultChanged -= OnSearchSelectedResultChanged;
            ClearDocumentCaches(documentInfo.DocumentId);
            if (IsWorkerAvailable)
            {
                try
                {
                    await _pdfWorkerClient.CloseDocumentAsync(documentInfo.DocumentId, cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning(exception, "A PDF document could not be closed with its tab.");
                }
            }
        }

        RecentlyClosedTabs.Insert(0, new RecentlyClosedTabItemViewModel(
            tab.FileName,
            tab.FullPath,
            tab.PersistenceRecord?.DocumentId));
        if (RecentlyClosedTabs.Count > 10)
        {
            RecentlyClosedTabs.RemoveAt(RecentlyClosedTabs.Count - 1);
        }
        OnPropertyChanged(nameof(CanReopenClosedTab));
        DocumentTabs.RemoveAt(tabIndex);
        _ = SaveSessionAsync(CancellationToken.None);
        if (!wasActive)
        {
            return;
        }

        ActiveDocumentTab = null;
        if (DocumentTabs.Count == 0)
        {
            ResetToHomePage();
            await RefreshRecentDocumentsAsync(cancellationToken);
            return;
        }

        var nextIndex = Math.Min(tabIndex, DocumentTabs.Count - 1);
        await ActivateDocumentTabCoreAsync(
            DocumentTabs[nextIndex],
            saveCurrentState: false,
            cancellationToken);
    }

    public void UpdateScrollOffsets(double horizontalOffset, double verticalOffset)
    {
        if (!ActiveViewportState.UpdateScrollOffsets(horizontalOffset, verticalOffset))
        {
            return;
        }

        var currentPageChanged = UpdateCurrentPageFromViewport();
        if (currentPageChanged)
        {
            HideSelectionVisualsPreservingTranslation();
            SetReadyStatus(_readerState.DocumentInfo!, isCached: PageImage is not null);
        }

        ScheduleReadingStateSave();
        _ = RenderVisiblePagesAsync(CancellationToken.None);
    }

    public async Task RefreshVisibleRenderingAsync(CancellationToken cancellationToken)
    {
        if (!IsWorkerAvailable || !HasDocument)
        {
            return;
        }

        await RenderCurrentPageAsync();
        await RenderVisiblePagesAsync(cancellationToken);
    }

    private void SetActiveScrollOffsets(double horizontalOffset, double verticalOffset)
    {
        if (!ActiveViewportState.UpdateScrollOffsets(horizontalOffset, verticalOffset))
        {
            return;
        }

        OnPropertyChanged(nameof(HorizontalScrollOffset));
        OnPropertyChanged(nameof(VerticalScrollOffset));
        ScheduleReadingStateSave();
        ScrollOffsetRequested?.Invoke(
            this,
            new ScrollOffsetRequestedEventArgs(horizontalOffset, verticalOffset));
        _ = RenderVisiblePagesAsync(CancellationToken.None);
    }

    private double GetCurrentPageTop()
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return 0;
        }

        return tab.Pages.FirstOrDefault(page => page.PageIndex == _readerState.CurrentPageIndex)
            ?.Bounds.Y ?? 0;
    }

    private void ScrollToCurrentPage()
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return;
        }

        var page = tab.Pages.FirstOrDefault(page => page.PageIndex == _readerState.CurrentPageIndex);
        if (page is null)
        {
            return;
        }

        SetActiveScrollOffsets(
            Math.Max(0, page.Bounds.X - 24),
            Math.Max(0, page.Bounds.Y));
    }

    public async Task NavigateToSearchResultAsync(
        SearchResultItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (ActiveDocumentTab is not { } tab || !tab.Search.Results.Contains(item))
        {
            return;
        }

        var result = item.Result;
        tab.Search.SelectedResult = result;
        if (_readerState.CurrentPageIndex != result.PageIndex)
        {
            _readerState.SetPageNumber(result.PageIndex + 1);
            ClearSelection(preserveTranslationContext: true);
            AnnotationHighlightRectangles.Clear();
            NotifyReaderStateChanged();
            ScheduleReadingStateSave();
            await RenderCurrentPageAsync();
        }
        else
        {
            RebuildSearchHighlightRectangles();
        }

        if (SearchHighlightRectangles.FirstOrDefault() is { } firstRectangle)
        {
            var pageTop = GetCurrentPageTop();
            SetActiveScrollOffsets(
                Math.Max(0, firstRectangle.X - 48),
                Math.Max(0, pageTop + firstRectangle.Y - 96));
        }

        StatusText = $"已定位到第 {result.PageIndex + 1} 页的第 {result.ResultIndex + 1} 个搜索结果。";
    }

    public async Task NavigateToAnnotationAsync(
        AnnotationItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (ActiveDocumentTab is not { } tab || !tab.Annotations.Items.Contains(item))
        {
            return;
        }

        var annotation = item.Annotation;
        tab.Annotations.SelectedItem = item;
        OpenLeftSidebar(3);
        if (_readerState.CurrentPageIndex != annotation.PageIndex)
        {
            _readerState.SetPageNumber(annotation.PageIndex + 1);
            ClearSelection(preserveTranslationContext: true);
            AnnotationHighlightRectangles.Clear();
            NotifyReaderStateChanged();
            ScheduleReadingStateSave();
            await RenderCurrentPageAsync();
        }
        else
        {
            RebuildAnnotationHighlightRectangles();
        }

        BeginAnnotationEmphasis(annotation.AnnotationId);
        RebuildAnnotationHighlightRectangles();

        if (AnnotationHighlightRectangles.FirstOrDefault(rectangle => rectangle.IsEmphasized) is { } target)
        {
            var pageTop = GetCurrentPageTop();
            SetActiveScrollOffsets(
                Math.Max(0, target.X - 48),
                Math.Max(0, pageTop + target.Y - 96));
        }

        StatusText = $"已定位到第 {annotation.PageIndex + 1} 页的批注。";
    }

    public async Task NavigateToOutlineItemAsync(
        DocumentOutlineItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        if (ActiveDocumentTab is not { } tab ||
            !ContainsOutlineItem(tab.Navigation.OutlineItems, item) ||
            item.PageIndex is not { } pageIndex ||
            pageIndex < 0 ||
            pageIndex >= _readerState.PageCount)
        {
            return;
        }

        await NavigateToPageIndexAsync(pageIndex, cancellationToken);
        OpenLeftSidebar(0);
        StatusText = $"已定位到目录“{item.Title}”对应的第 {pageIndex + 1} 页。";
    }

    public async Task NavigateToThumbnailAsync(
        DocumentThumbnailViewModel thumbnail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        cancellationToken.ThrowIfCancellationRequested();
        if (ActiveDocumentTab is not { } tab || !tab.Navigation.Thumbnails.Contains(thumbnail))
        {
            return;
        }

        await NavigateToPageIndexAsync(thumbnail.PageIndex, cancellationToken);
        OpenLeftSidebar(1);
        StatusText = $"已定位到第 {thumbnail.PageNumber} 页。";
    }

    public async Task LoadThumbnailAsync(
        DocumentThumbnailViewModel thumbnail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        if (thumbnail.HasImage)
        {
            thumbnail.Touch();
            return;
        }

        if (ActiveDocumentTab is not { } tab ||
            _readerState.DocumentInfo is not { } documentInfo ||
            !tab.Navigation.Thumbnails.Contains(thumbnail) ||
            thumbnail.PageIndex < 0 ||
            thumbnail.PageIndex >= tab.Pages.Count)
        {
            return;
        }

        var renderKey = (documentInfo.DocumentId, thumbnail.PageIndex, _readerState.Rotation);
        if (!_renderingThumbnails.Add(renderKey))
        {
            return;
        }

        thumbnail.IsRendering = true;
        try
        {
            var pageSize = tab.Pages[thumbnail.PageIndex].PdfPageSize;
            var rotatedBounds = ReadingViewLayout.CalculatePageBounds(
                pageSize,
                _readerState.Rotation,
                zoomFactor: 1);
            var zoomFactor = Math.Min(
                ThumbnailMaximumWidth / Math.Max(1, rotatedBounds.Width),
                ThumbnailMaximumHeight / Math.Max(1, rotatedBounds.Height));
            zoomFactor = Math.Clamp(zoomFactor, 0.05, 1);
            var request = new PageRenderRequest(
                Guid.NewGuid(),
                documentInfo.DocumentId,
                thumbnail.PageIndex,
                zoomFactor,
                _readerState.Rotation,
                1,
                1,
                RenderQuality.Preview);

            RenderedPageDescriptor? descriptor = null;
            try
            {
                descriptor = await _pdfWorkerClient.RenderPageAsync(request, cancellationToken);
                if (!ReferenceEquals(ActiveDocumentTab, tab) ||
                    _readerState.DocumentInfo?.DocumentId != documentInfo.DocumentId ||
                    descriptor.RequestId != request.RequestId)
                {
                    return;
                }

                var renderedPage = _pageRenderCoordinator.Read(descriptor);
                thumbnail.SetImage(renderedPage.Bitmap);
                tab.Navigation.TrimThumbnailCache(MaximumCachedThumbnailImages, thumbnail);
            }
            finally
            {
                if (descriptor is not null)
                {
                    await _pdfWorkerClient.ReleaseSharedMemoryAsync(
                        descriptor.MemoryMapName,
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "A PDF thumbnail could not be rendered. DocumentId: {DocumentId}; PageIndex: {PageIndex}",
                documentInfo.DocumentId.Value,
                thumbnail.PageIndex);
        }
        finally
        {
            thumbnail.IsRendering = false;
            _renderingThumbnails.Remove(renderKey);
        }
    }

    private async Task NavigateToPageIndexAsync(int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageIndex < 0 || pageIndex >= _readerState.PageCount)
        {
            return;
        }

        var pageChanged = _readerState.SetPageNumber(pageIndex + 1);
        if (pageChanged)
        {
            ClearSelection(preserveTranslationContext: true);
            AnnotationHighlightRectangles.Clear();
            CancelAnnotationEmphasis();
            NotifyReaderStateChanged();
            ScheduleReadingStateSave();
            await RenderCurrentPageAsync();
        }
        else
        {
            RebuildSelectionRectangles();
            RebuildSearchHighlightRectangles();
            RebuildAnnotationHighlightRectangles();
        }

        ScrollToCurrentPage();
    }

    private static bool ContainsOutlineItem(
        IEnumerable<DocumentOutlineItemViewModel> items,
        DocumentOutlineItemViewModel target) =>
        items.Any(item => ReferenceEquals(item, target) || ContainsOutlineItem(item.Children, target));

    public bool HasPendingLocalAnnotationChanges =>
        DocumentTabs.Any(TabHasPendingLocalAnnotationChanges);

    public bool TabHasPendingLocalAnnotationChanges(DocumentTabItemViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.Annotations.Items.Any(item => item.HasUnsavedChanges);
    }

    public async Task SaveAnnotationChangesAsync(
        AnnotationItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ActiveDocumentTab is not { } tab ||
            !tab.Annotations.Items.Contains(item) ||
            !item.HasUnsavedChanges)
        {
            return;
        }

        await SaveAnnotationChangesCoreAsync(
            tab,
            item,
            refreshActiveVisuals: true,
            cancellationToken);
    }

    public async Task<bool> SavePendingLocalAnnotationChangesAsync(
        DocumentTabItemViewModel tab,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var pendingItems = tab.Annotations.Items
            .Where(item => item.HasUnsavedChanges)
            .ToArray();
        if (pendingItems.Length == 0)
        {
            return true;
        }

        if (_annotationService is null)
        {
            StatusText = "本地阅读数据不可用，未保存的批注无法写入本地数据库。";
            return false;
        }

        foreach (var item in pendingItems)
        {
            var saved = await SaveAnnotationChangesCoreAsync(
                tab,
                item,
                refreshActiveVisuals: ReferenceEquals(tab, ActiveDocumentTab),
                cancellationToken);
            if (!saved)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<bool> SaveAllPendingLocalAnnotationChangesAsync(CancellationToken cancellationToken)
    {
        foreach (var tab in DocumentTabs.ToArray())
        {
            var saved = await SavePendingLocalAnnotationChangesAsync(tab, cancellationToken);
            if (!saved)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> SaveAnnotationChangesCoreAsync(
        DocumentTabItemViewModel tab,
        AnnotationItemViewModel item,
        bool refreshActiveVisuals,
        CancellationToken cancellationToken)
    {
        if (_annotationService is null ||
            !tab.Annotations.Items.Contains(item) ||
            !item.HasUnsavedChanges)
        {
            return false;
        }

        try
        {
            var updated = await _annotationService.UpdateAsync(
                item.Annotation,
                item.SelectedColor,
                string.IsNullOrEmpty(item.DraftNote) ? null : item.DraftNote,
                cancellationToken);
            item.Apply(updated);
            tab.Annotations.Resort();
            tab.HasUnsavedAnnotations = true;
            if (refreshActiveVisuals)
            {
                RebuildAnnotationHighlightRectangles();
            }

            StatusText = "批注颜色和笔记已保存到本地。";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            ReportAnnotationError(UserErrorCode.AnnotationWriteFailed, exception);
            return false;
        }
    }

    public async Task DeleteAnnotationAsync(
        AnnotationItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_annotationService is null ||
            ActiveDocumentTab is not { } tab ||
            !tab.Annotations.Items.Contains(item))
        {
            return;
        }

        try
        {
            await _annotationService.DeleteAsync(item.Annotation.AnnotationId, cancellationToken);
            tab.Annotations.Remove(item);
            tab.HasUnsavedAnnotations = true;
            if (_emphasizedAnnotationId == item.Annotation.AnnotationId)
            {
                CancelAnnotationEmphasis();
            }

            RebuildAnnotationHighlightRectangles();
            StatusText = "批注已从本地数据库删除，原 PDF 文件未被修改。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportAnnotationError(UserErrorCode.AnnotationWriteFailed, exception);
        }
    }

    public async Task<IReadOnlyList<PdfStandardAnnotation>> ReadActivePdfAnnotationsAsync(
        CancellationToken cancellationToken)
    {
        if (_pdfAnnotationSyncService is null ||
            ActiveDocumentTab?.ReaderState.DocumentInfo is not { } documentInfo)
        {
            return [];
        }

        return await _pdfAnnotationSyncService.ReadPdfAnnotationsAsync(
            _pdfWorkerClient,
            documentInfo.DocumentId,
            cancellationToken);
    }

    public async Task<PdfAnnotationSaveResult?> SaveActiveAnnotationsToPdfAsync(
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);
        if (_pdfAnnotationSyncService is null ||
            ActiveDocumentTab is not { ReaderState.DocumentInfo: { } documentInfo } tab ||
            tab.Annotations.Items.Count == 0)
        {
            return null;
        }

        if (tab.IsExternallyModified)
        {
            throw new InvalidOperationException("The PDF file changed outside LocalPdfReader; save annotations to a new file or reload before overwriting it.");
        }

        var annotations = tab.Annotations.Items
            .Select(item => item.Annotation)
            .ToArray();
        var result = await _pdfAnnotationSyncService.SaveLocalHighlightsAsync(
            _pdfWorkerClient,
            documentInfo.DocumentId,
            annotations,
            destinationFilePath,
            saveMode,
            cancellationToken);
        tab.HasUnsavedAnnotations = false;
        StatusText = string.Equals(Path.GetFullPath(destinationFilePath), Path.GetFullPath(tab.FullPath), StringComparison.OrdinalIgnoreCase)
            ? "批注已写入当前 PDF。"
            : "批注已另存到新的 PDF 文件。";
        return result;
    }

    public async Task SaveReadingStateNowAsync(CancellationToken cancellationToken)
    {
        CancelReadingStateSave();
        await SaveReadingStateCoreAsync(cancellationToken);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        CancelReadingStateSave();
        CancelAnnotationEmphasis();
        CaptureActiveTabState();
        foreach (var tab in DocumentTabs)
        {
            await tab.Search.CancelAndWaitAsync();
            await SaveReadingStateForTabAsync(tab, cancellationToken);
        }
        await SaveSessionAsync(cancellationToken);
    }

    public void HandleWorkerDisconnected(PdfWorkerDisconnectedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);

        if (!_isWorkerAvailable)
        {
            return;
        }

        _isWorkerAvailable = false;
        _viewportRenderCancellationSource?.Cancel();
        _textSelectionCancellationSource?.Cancel();
        CancelAnnotationEmphasis();
        foreach (var tab in DocumentTabs)
        {
            tab.Search.Cancel();
        }
        StatusText = "PDF 工作进程已意外停止，当前文档暂时不可操作；翻译面板仍可继续使用。";
        OnPropertyChanged(nameof(IsWorkerAvailable));
        OnPropertyChanged(nameof(CanOpenDocument));
        NotifyReaderStateChanged();
    }

    public void HandleWorkerRestarting()
    {
        StatusText = "正在重新启动 PDF 工作进程...";
    }

    public void HandleWorkerRestarted()
    {
        if (HasDocument)
        {
            StatusText = "PDF 工作进程已重新启动；当前页面已保留，文档会话将在下一步恢复。";
            return;
        }

        SetWorkerAvailability(true);
        StatusText = "PDF 工作进程已重新启动，可以打开文档。";
    }

    public async Task RecoverDocumentAfterWorkerRestartAsync(CancellationToken cancellationToken)
    {
        if (DocumentTabs.Count == 0)
        {
            HandleWorkerRestarted();
            return;
        }

        CaptureActiveTabState();
        StatusText = $"正在恢复 {DocumentTabs.Count} 个 PDF 文档会话...";
        var failedTabs = new List<(DocumentTabItemViewModel Tab, Exception Error)>();

        try
        {
            foreach (var tab in DocumentTabs.ToArray())
            {
                await tab.Search.CancelAndResetAsync();
                var previousDocument = tab.ReaderState.DocumentInfo;
                if (previousDocument is null)
                {
                    continue;
                }

                var viewState = tab.ReaderState.CaptureViewState();
                try
                {
                    var recoveredDocument = await _pdfWorkerClient.OpenDocumentAsync(
                        tab.FullPath,
                        password: null,
                        cancellationToken);
                    ClearDocumentCaches(previousDocument.DocumentId);
                    tab.ReaderState.Restore(recoveredDocument, viewState);
                    await ReloadAnnotationsAsync(tab, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedTabs.Add((tab, exception));
                    _logger?.LogWarning(
                        exception,
                        "A document tab could not be restored after the PDF worker restarted.");
                }
            }

            foreach (var failed in failedTabs)
            {
                if (failed.Tab.ReaderState.DocumentInfo is { } failedDocument)
                {
                    ClearDocumentCaches(failedDocument.DocumentId);
                }

                DocumentTabs.Remove(failed.Tab);
            }

            _pageTextCache.Clear();
            _pageTextCacheOrder.Clear();
            ClearSelection(preserveTranslationContext: true);
            SetWorkerAvailability(true);

            if (DocumentTabs.Count == 0)
            {
                ResetToHomePage();
                if (failedTabs.Count > 0)
                {
                    StatusText = _userErrorService.Report(
                        UserErrorCode.DocumentRecoveryFailed,
                        failedTabs[0].Error).InlineText;
                }

                return;
            }

            var tabToActivate = ActiveDocumentTab is { } active && DocumentTabs.Contains(active)
                ? active
                : DocumentTabs[0];
            ActiveDocumentTab = null;
            await ActivateDocumentTabCoreAsync(
                tabToActivate,
                saveCurrentState: false,
                cancellationToken);
            if (failedTabs.Count > 0)
            {
                StatusText = $"已恢复 {DocumentTabs.Count} 个标签；{failedTabs.Count} 个文件恢复失败并已关闭。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "PDF 文档标签恢复已取消。";
        }
    }

    public void HandleWorkerRestartFailed(UserFacingError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        StatusText = error.InlineText;
    }

    private void SetWorkerAvailability(bool isAvailable)
    {
        if (_isWorkerAvailable == isAvailable)
        {
            return;
        }

        _isWorkerAvailable = isAvailable;
        OnPropertyChanged(nameof(IsWorkerAvailable));
        OnPropertyChanged(nameof(CanOpenDocument));
        NotifyReaderStateChanged();
    }

    public void UpdateViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        _viewportWidth = width;
        _viewportHeight = height;
        var viewportChanged = ActiveViewportState.UpdateViewportSize(width, height);
        var zoomChanged = ActiveViewportState.Mode == ReadingMode.DoublePage &&
            _readerState.ZoomMode is ReaderZoomMode.FitPage or ReaderZoomMode.FitWidth
                ? ApplyFitMode(_readerState.ZoomMode)
                : _readerState.UpdateViewportSize(width, height);
        NotifyFitStateChanged();

        if (viewportChanged && HasDocument)
        {
            UpdateDocumentPageLayouts(_readerState);
        }

        if (!IsWorkerAvailable || !HasDocument || (!viewportChanged && !zoomChanged))
        {
            return;
        }

        if (zoomChanged)
        {
            InvalidateActivePageImages();
            NotifyReaderStateChanged();
        }

        ScheduleReadingStateSave();
        _viewportRenderCancellationSource?.Cancel();
        _viewportRenderCancellationSource?.Dispose();
        _viewportRenderCancellationSource = new CancellationTokenSource();
        _ = RenderAfterViewportChangeAsync(_viewportRenderCancellationSource.Token);
    }

    public Task<OpenDocumentResult> OpenDocumentAsync(string filePath, CancellationToken cancellationToken) =>
        OpenDocumentAsync(filePath, password: null, cancellationToken);

    public async Task<OpenDocumentResult> OpenDocumentAsync(
        string filePath,
        string? password,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalizedPath = Path.GetFullPath(filePath);

        if (!IsWorkerAvailable)
        {
            StatusText = "PDF 工作进程当前不可用，暂时无法打开文档。";
            return OpenDocumentResult.Failed;
        }

        var existingTab = DocumentTabs.FirstOrDefault(tab =>
            string.Equals(tab.FullPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existingTab is not null)
        {
            await ActivateDocumentTabAsync(existingTab, cancellationToken);
            return OpenDocumentResult.ActivatedExisting;
        }

        IsOpening = true;
        StatusText = "正在通过 PDF Worker 打开并渲染第一页...";
        var previousActiveTab = ActiveDocumentTab;
        PdfDocumentInfo? openedDocument = null;
        DocumentTabItemViewModel? addedTab = null;

        try
        {
            var documentInfo = await _pdfWorkerClient.OpenDocumentAsync(
                normalizedPath,
                password,
                cancellationToken);
            openedDocument = documentInfo;
            var readerState = new ReaderState();
            if (_viewportWidth > 0 && _viewportHeight > 0)
            {
                readerState.UpdateViewportSize(_viewportWidth, _viewportHeight);
            }
            readerState.Open(documentInfo);
            DocumentRecord? persistenceRecord = null;
            var horizontalScrollOffset = 0d;
            var verticalScrollOffset = 0d;
            if (_documentHistoryService is not null)
            {
                try
                {
                    var persistence = await _documentHistoryService.RegisterSuccessfulOpenAsync(
                        normalizedPath,
                        cancellationToken);
                    persistenceRecord = persistence?.Document;
                    if (persistence?.ReadingState is { } readingState)
                    {
                        readerState.Restore(
                            documentInfo,
                            DocumentHistoryService.CreateReaderViewState(readingState));
                        horizontalScrollOffset = Math.Max(0, readingState.HorizontalOffset);
                        verticalScrollOffset = Math.Max(0, readingState.VerticalOffset);
                    }
                }
                catch (Exception persistenceException)
                {
                    _logger?.LogWarning(
                        persistenceException,
                        "Document history could not be updated after a successful PDF open.");
                }
            }

            var annotations = await LoadAnnotationsAsync(persistenceRecord, cancellationToken);

            var tab = new DocumentTabItemViewModel(
                normalizedPath,
                readerState,
                persistenceRecord,
                horizontalScrollOffset,
                verticalScrollOffset,
                Translation.CreateEmptySnapshot(),
                new DocumentSearchViewModel(
                    _pdfWorkerClient,
                    () => readerState.DocumentInfo?.DocumentId,
                    _logger),
                new DocumentNavigationViewModel(),
                annotations);
            tab.FileSnapshot = CaptureFileSnapshot(normalizedPath);
            if (_viewportWidth > 0 && _viewportHeight > 0)
            {
                tab.ViewportState.UpdateViewportSize(_viewportWidth, _viewportHeight);
            }
            tab.Search.SelectedResultChanged += OnSearchSelectedResultChanged;
            DocumentTabs.Add(tab);
            addedTab = tab;
            await ActivateDocumentTabCoreAsync(
                tab,
                saveCurrentState: true,
                cancellationToken,
                requireSuccessfulRender: true);
            _ = LoadDocumentNavigationAsync(tab, CancellationToken.None);
            await RefreshRecentDocumentsAsync(cancellationToken);
            _ = SaveSessionAsync(CancellationToken.None);
            return OpenDocumentResult.Opened;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackFailedOpenAsync(addedTab, openedDocument, previousActiveTab);
            StatusText = "已取消打开 PDF。";
            return OpenDocumentResult.Cancelled;
        }
        catch (PdfWorkerException exception)
            when (exception.ErrorCode == PdfWorkerErrorCodes.PasswordRequiredOrInvalid)
        {
            await RollBackFailedOpenAsync(addedTab, openedDocument, previousActiveTab);
            StatusText = password is null
                ? "该 PDF 需要密码才能打开。"
                : "PDF 密码不正确，请重新输入。";
            return OpenDocumentResult.PasswordRequiredOrInvalid;
        }
        catch (Exception exception)
        {
            await RollBackFailedOpenAsync(addedTab, openedDocument, previousActiveTab);
            var userError = _userErrorService.Report(UserErrorCode.DocumentOpenFailed, exception);
            StatusText = userError.InlineText;
            ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
            return OpenDocumentResult.Failed;
        }
        finally
        {
            IsOpening = false;
        }
    }

    private async Task RollBackFailedOpenAsync(
        DocumentTabItemViewModel? addedTab,
        PdfDocumentInfo? openedDocument,
        DocumentTabItemViewModel? previousActiveTab)
    {
        try
        {
            if (addedTab is not null)
            {
                addedTab.Search.SelectedResultChanged -= OnSearchSelectedResultChanged;
                addedTab.Search.Cancel();
                DocumentTabs.Remove(addedTab);
            }

            if (openedDocument is not null)
            {
                ClearDocumentCaches(openedDocument.DocumentId);
                if (IsWorkerAvailable)
                {
                    try
                    {
                        await _pdfWorkerClient.CloseDocumentAsync(
                            openedDocument.DocumentId,
                            CancellationToken.None);
                    }
                    catch (Exception closeException)
                    {
                        _logger?.LogWarning(
                            closeException,
                            "A PDF worker document could not be closed while rolling back a failed open.");
                    }
                }
            }

            ActiveDocumentTab = null;
            var tabToRestore = previousActiveTab is not null && DocumentTabs.Contains(previousActiveTab)
                ? previousActiveTab
                : DocumentTabs.LastOrDefault();
            if (tabToRestore is null)
            {
                ResetToHomePage();
            }
            else
            {
                await ActivateDocumentTabCoreAsync(
                    tabToRestore,
                    saveCurrentState: false,
                    CancellationToken.None);
            }
        }
        catch (Exception rollbackException)
        {
            _logger?.LogError(rollbackException, "The UI could not fully restore after a failed PDF open.");
            ResetToHomePage();
        }
    }

    private async Task LoadDocumentNavigationAsync(
        DocumentTabItemViewModel tab,
        CancellationToken cancellationToken)
    {
        if (tab.ReaderState.DocumentInfo is not { } documentInfo)
        {
            tab.Navigation.SetOutlineUnavailable("当前 PDF 尚未完成打开，无法读取目录。");
            return;
        }

        try
        {
            var outline = await _pdfWorkerClient.GetOutlineAsync(documentInfo.DocumentId, cancellationToken);
            if (!ReferenceEquals(tab.ReaderState.DocumentInfo, documentInfo))
            {
                return;
            }

            tab.Navigation.ReplaceOutline(outline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "A PDF outline could not be loaded. DocumentId: {DocumentId}",
                documentInfo.DocumentId.Value);
            tab.Navigation.SetOutlineUnavailable("目录读取失败，正文阅读不受影响。");
        }
    }

    public void CheckActiveDocumentExternalModification()
    {
        if (ActiveDocumentTab is { } tab)
        {
            CheckExternalModification(tab);
        }
    }

    private void CheckExternalModification(DocumentTabItemViewModel tab)
    {
        if (tab.FileSnapshot is not { } originalSnapshot)
        {
            tab.FileSnapshot = CaptureFileSnapshot(tab.FullPath);
            tab.IsExternallyModified = false;
            tab.ExternalModificationMessage = string.Empty;
            return;
        }

        var currentSnapshot = CaptureFileSnapshot(tab.FullPath);
        if (currentSnapshot is null)
        {
            tab.IsExternallyModified = true;
            tab.ExternalModificationMessage = "原 PDF 文件已被移动、删除或暂时无法访问。";
            return;
        }

        var changed = currentSnapshot.Length != originalSnapshot.Length ||
            currentSnapshot.LastWriteTimeUtc != originalSnapshot.LastWriteTimeUtc;
        tab.IsExternallyModified = changed;
        tab.ExternalModificationMessage = changed
            ? "原 PDF 文件已在阅读器外部发生变化；当前标签仍显示打开时的内容。"
            : string.Empty;
    }

    private static DocumentFileSnapshot? CaptureFileSnapshot(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists
                ? new DocumentFileSnapshot(fileInfo.Length, fileInfo.LastWriteTimeUtc)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task ActivateDocumentTabAsync(
        DocumentTabItemViewModel tab,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!DocumentTabs.Contains(tab))
        {
            throw new ArgumentException("The document tab is not open.", nameof(tab));
        }

        return ActivateDocumentTabCoreAsync(tab, saveCurrentState: true, cancellationToken);
    }

    public bool MoveDocumentTab(
        DocumentTabItemViewModel sourceTab,
        DocumentTabItemViewModel targetTab)
    {
        ArgumentNullException.ThrowIfNull(sourceTab);
        ArgumentNullException.ThrowIfNull(targetTab);
        var sourceIndex = DocumentTabs.IndexOf(sourceTab);
        var targetIndex = DocumentTabs.IndexOf(targetTab);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return false;
        }

        DocumentTabs.Move(sourceIndex, targetIndex);
        _ = SaveSessionAsync(CancellationToken.None);
        return true;
    }

    private async Task ActivateDocumentTabCoreAsync(
        DocumentTabItemViewModel tab,
        bool saveCurrentState,
        CancellationToken cancellationToken,
        bool requireSuccessfulRender = false)
    {
        if (ReferenceEquals(ActiveDocumentTab, tab))
        {
            return;
        }

        if (ActiveDocumentTab is not null)
        {
            await Translation.CancelAsync();
            if (saveCurrentState)
            {
                await SaveReadingStateNowAsync(cancellationToken);
            }

            CaptureActiveTabState();
        }

        _viewportRenderCancellationSource?.Cancel();
        CancelTextSelectionRequest();
        ClearSelection(preserveTranslationContext: true);
        ActiveDocumentTab = tab;
        _readerState = tab.ReaderState;
        CheckExternalModification(tab);
        if (_viewportWidth > 0 && _viewportHeight > 0)
        {
            _readerState.UpdateViewportSize(_viewportWidth, _viewportHeight);
            tab.ViewportState.UpdateViewportSize(_viewportWidth, _viewportHeight);
        }
        Translation.RestoreSnapshot(tab.TranslationSnapshot);
        PageImage = null;
        SearchHighlightRectangles.Clear();
        AnnotationHighlightRectangles.Clear();
        CancelAnnotationEmphasis();
        OnPropertyChanged(nameof(HorizontalScrollOffset));
        OnPropertyChanged(nameof(VerticalScrollOffset));
        NotifyReaderStateChanged();
        await RenderCurrentPageAsync(requireSuccessfulRender);
    }

    private void CaptureActiveTabState()
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return;
        }

        tab.TranslationSnapshot = Translation.CaptureSnapshot();
    }

    public Task BeginSelectionAsync(ViewPoint viewPoint, CancellationToken cancellationToken) =>
        BeginSelectionAsync(_readerState.CurrentPageIndex, viewPoint, cancellationToken);

    public async Task BeginSelectionAsync(
        int pageIndex,
        ViewPoint viewPoint,
        CancellationToken cancellationToken)
    {
        _isPointerSelecting = true;
        CancelTextSelectionRequest();
        _selectionPageText = null;
        _selectionPageIndex = null;
        _selectionStartPageIndex = null;
        IsSelectionToolbarVisible = false;
        _selectionStartCharacterIndex = null;
        _selectionStartViewPoint = null;
        _hasSelectionDragStarted = false;
        _selectionChangedInCurrentGesture = false;
        _lastSelectionUpdate = DateTime.MinValue;
        _textSelectionCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var selectionCancellationToken = _textSelectionCancellationSource.Token;

        if (_readerState.DocumentInfo is not { } documentInfo)
        {
            return;
        }

        if (ActiveDocumentTab is { } tab)
        {
            if (pageIndex < 0 || pageIndex >= tab.Pages.Count)
            {
                return;
            }
        }
        else if (_readerState.CurrentPageSize is null || pageIndex != _readerState.CurrentPageIndex)
        {
            return;
        }

        _selectionPageIndex = pageIndex;
        StatusText = "正在读取页面文字...";

        try
        {
            var pageText = await GetPageTextAsync(documentInfo.DocumentId, pageIndex, selectionCancellationToken);
            if (!_isPointerSelecting ||
                _readerState.DocumentInfo?.DocumentId != documentInfo.DocumentId ||
                _selectionPageIndex != pageIndex)
            {
                return;
            }

            _selectionPageText = pageText;
            var hit = HitTestSelection(viewPoint);
            if (hit is null)
            {
                StatusText = CurrentSelection is not null
                    ? "未点中文字，已保留原选区和翻译内容。"
                    : pageText.Glyphs.Count == 0
                        ? "当前页面没有可选择文字层。"
                        : "请从文字上开始拖动选择。";
                return;
            }

            _selectionStartCharacterIndex = hit.CharacterIndex;
            _selectionStartPageIndex = pageIndex;
            _selectionStartViewPoint = viewPoint;
            StatusText = "已定位文字起点，请拖动鼠标以选择文本。";
        }
        catch (OperationCanceledException) when (selectionCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var userError = _userErrorService.Report(UserErrorCode.TextExtractionFailed, exception);
            StatusText = userError.InlineText;
            ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
        }
    }

    public void UpdateSelection(ViewPoint viewPoint)
    {
        if (_selectionPageIndex is not { } pageIndex)
        {
            return;
        }

        var hit = TryHitSelectionOnPage(pageIndex, viewPoint, _selectionPageText);
        if (hit is not null)
        {
            UpdateSinglePageSelection(pageIndex, hit.CharacterIndex, _selectionPageText);
            _selectionChangedInCurrentGesture = true;
        }
    }

    public async Task UpdateSelectionAsync(
        int pageIndex,
        ViewPoint viewPoint,
        CancellationToken cancellationToken)
    {
        if (!_isPointerSelecting ||
            _selectionStartCharacterIndex is null ||
            _readerState.DocumentInfo is not { } documentInfo ||
            ActiveDocumentTab is not { } tab ||
            pageIndex < 0 ||
            pageIndex >= tab.Pages.Count)
        {
            return;
        }

        PageTextData? pageText = null;
        try
        {
            pageText = await GetPageTextAsync(documentInfo.DocumentId, pageIndex, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            var userError = _userErrorService.Report(UserErrorCode.TextExtractionFailed, exception);
            StatusText = userError.InlineText;
            ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
            return;
        }

        var hit = TryHitSelectionOnPage(pageIndex, viewPoint, pageText);
        if (hit is not null)
        {
            await UpdateCurrentSelectionAsync(
                documentInfo.DocumentId,
                pageIndex,
                hit.CharacterIndex,
                cancellationToken);
            _selectionChangedInCurrentGesture = true;
        }
    }

    private TextGlyph? TryHitSelectionOnPage(int pageIndex, ViewPoint viewPoint, PageTextData? pageText)
    {
        if (!_isPointerSelecting || _selectionStartCharacterIndex is null)
        {
            return null;
        }

        if (!_hasSelectionDragStarted)
        {
            if (_selectionStartViewPoint is not { } startViewPoint)
            {
                return null;
            }

            var deltaX = viewPoint.X - startViewPoint.X;
            var deltaY = viewPoint.Y - startViewPoint.Y;
            if (deltaX * deltaX + deltaY * deltaY < SelectionDragThreshold * SelectionDragThreshold)
            {
                return null;
            }

            _hasSelectionDragStarted = true;
        }

        var now = DateTime.UtcNow;
        if (now - _lastSelectionUpdate < TimeSpan.FromMilliseconds(20))
        {
            return null;
        }

        _lastSelectionUpdate = now;
        return HitTestSelection(pageIndex, viewPoint, pageText);
    }

    public void CompleteSelection()
    {
        _isPointerSelecting = false;
        if (_selectionStartCharacterIndex is null || !_selectionChangedInCurrentGesture)
        {
            CancelTextSelectionRequest();
            if (_selectionStartCharacterIndex is not null && CurrentSelection is not null)
            {
                ClearSelection(preserveTranslationContext: true);
                StatusText = "已清除选区，翻译栏内容已保留。";
            }
            else if (CurrentSelection is not null)
            {
                StatusText = "未拖动选择文字，已保留原选区和翻译内容。";
            }
            else if (_selectionStartCharacterIndex is not null)
            {
                StatusText = "请按住鼠标并拖动以选择文字。";
            }

            return;
        }

        if (CurrentSelection is not null)
        {
            StatusText = $"已选择 {CurrentSelection.NormalizedText.Length} 个字符，可复制或在右侧手动开始翻译。";
        }
    }

    public void ToggleSelectionToolbar(int pageIndex, ViewPoint viewPoint)
    {
        if (CurrentSelection is null ||
            (_currentSelectionParts.Count > 0
                ? _currentSelectionParts.All(part => part.PageIndex != pageIndex)
                : CurrentSelection.PageIndex != pageIndex))
        {
            HideSelectionToolbar();
            return;
        }

        if (IsSelectionToolbarVisible)
        {
            HideSelectionToolbar();
            return;
        }

        RebuildSelectionRectangles();
        if (SelectionRectangles.Count == 0)
        {
            return;
        }

        SetSelectionHostPage(pageIndex);
        var pageBounds = ActiveDocumentTab?.Pages.FirstOrDefault(page => page.PageIndex == pageIndex)?.Bounds ??
            ReadingViewLayout.CalculatePageBounds(
                _readerState.CurrentPageSize!.Value,
                _readerState.Rotation,
                _readerState.ZoomFactor);
        SelectionToolbarX = Math.Clamp(viewPoint.X, 0, Math.Max(0, pageBounds.Width - 430));
        SelectionToolbarY = Math.Clamp(viewPoint.Y, 0, Math.Max(0, pageBounds.Height - 40));
        IsSelectionToolbarVisible = true;
    }

    public void HideSelectionToolbar() => IsSelectionToolbarVisible = false;

    public void HideSelectionVisualsPreservingTranslation() =>
        ClearSelection(preserveTranslationContext: true);

    public void HideSelectionToolbarPreservingSelection() => HideSelectionToolbar();

    public bool HasSelectionOnPage(int pageIndex)
    {
        if (CurrentSelection is null)
        {
            return false;
        }

        return _currentSelectionParts.Count > 0
            ? _currentSelectionParts.Any(part => part.PageIndex == pageIndex)
            : CurrentSelection.PageIndex == pageIndex;
    }

    private Task MoveToPreviousPageAsync() => ChangePageAndRenderAsync(_readerState.MoveToPreviousPage);

    private Task MoveToNextPageAsync() => ChangePageAndRenderAsync(_readerState.MoveToNextPage);

    private async Task GoToPageAsync()
    {
        if (!int.TryParse(PageNumberText, out var pageNumber) ||
            pageNumber < 1 ||
            pageNumber > _readerState.PageCount)
        {
            PageNumberText = _readerState.CurrentPageNumber.ToString();
            StatusText = $"请输入 1 至 {_readerState.PageCount} 之间的页码。";
            return;
        }

        if (!_readerState.SetPageNumber(pageNumber))
        {
            PageNumberText = _readerState.CurrentPageNumber.ToString();
            return;
        }

        ClearSelection(preserveTranslationContext: true);
        AnnotationHighlightRectangles.Clear();
        CancelAnnotationEmphasis();
        NotifyReaderStateChanged();
        ScrollToCurrentPage();
        ScheduleReadingStateSave();
        await RenderCurrentPageAsync();
    }

    private Task ZoomOutAsync() => ChangeReaderStateAndRenderAsync(_readerState.ZoomOut);

    private Task ZoomInAsync() => ChangeReaderStateAndRenderAsync(_readerState.ZoomIn);

    private async Task ApplyZoomAsync()
    {
        if (!TryParseZoomText(ZoomText, out var zoomFactor))
        {
            ZoomText = FormatZoomText(_readerState.ZoomFactor);
            StatusText = $"请输入 {ReaderState.MinimumZoomFactor:P0} 至 {ReaderState.MaximumZoomFactor:P0} 之间的缩放比例。";
            return;
        }

        await ChangeReaderStateAndRenderAsync(() => _readerState.SetZoomFactor(zoomFactor));
        ZoomText = FormatZoomText(_readerState.ZoomFactor);
    }

    public Task ZoomByMouseWheelAsync(int wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return Task.CompletedTask;
        }

        return ChangeReaderStateAndRenderAsync(wheelDelta > 0
            ? _readerState.ZoomIn
            : _readerState.ZoomOut);
    }

    private Task FitPageAsync() => ChangeReaderStateAndRenderAsync(() => ApplyFitMode(ReaderZoomMode.FitPage));

    private Task FitWidthAsync() => ChangeReaderStateAndRenderAsync(() => ApplyFitMode(ReaderZoomMode.FitWidth));

    private Task RotateClockwiseAsync() => ChangeReaderStateAndRenderAsync(_readerState.RotateClockwise);

    private async Task SetReadingModeAsync(ReadingMode mode)
    {
        if (!ActiveViewportState.SetMode(mode))
        {
            return;
        }

        ClearSelection(preserveTranslationContext: true);
        UpdateDocumentPageLayouts(_readerState);
        if (_readerState.ZoomMode is ReaderZoomMode.FitPage or ReaderZoomMode.FitWidth)
        {
            ApplyFitMode(_readerState.ZoomMode);
            InvalidateActivePageImages();
        }

        UpdateCurrentPageFromViewport();
        NotifyReadingModeChanged();
        NotifyReaderStateChanged();
        ScrollToCurrentPage();
        ScheduleReadingStateSave();
        await RenderCurrentPageAsync();
        _ = RenderVisiblePagesAsync(CancellationToken.None);
    }

    private bool ApplyFitMode(ReaderZoomMode zoomMode)
    {
        if (ActiveViewportState.Mode == ReadingMode.DoublePage)
        {
            return ApplyDoublePageFitMode(zoomMode);
        }

        return zoomMode == ReaderZoomMode.FitWidth
            ? _readerState.FitWidth()
            : _readerState.FitPage();
    }

    private bool ApplyDoublePageFitMode(ReaderZoomMode zoomMode)
    {
        if (ActiveDocumentTab is not { } tab ||
            _readerState.CurrentPageSize is null ||
            _readerState.ViewportWidth <= 0 ||
            _readerState.ViewportHeight <= 0)
        {
            return false;
        }

        var spread = GetCurrentSpreadPages(tab).ToArray();
        if (spread.Length == 0)
        {
            return false;
        }

        var spreadWidthAtActualZoom = spread.Sum(page =>
            ReadingViewLayout.CalculatePageBounds(page.PdfPageSize, _readerState.Rotation, zoomFactor: 1).Width) +
            (spread.Length > 1 ? DoublePageGap : 0);
        var spreadHeightAtActualZoom = spread.Max(page =>
            ReadingViewLayout.CalculatePageBounds(page.PdfPageSize, _readerState.Rotation, zoomFactor: 1).Height);
        var widthZoom = _readerState.ViewportWidth / spreadWidthAtActualZoom;
        var calculatedZoom = zoomMode == ReaderZoomMode.FitWidth
            ? widthZoom
            : Math.Min(widthZoom, _readerState.ViewportHeight / spreadHeightAtActualZoom);

        return _readerState.SetZoomModeFactor(zoomMode, calculatedZoom);
    }

    private bool SetCurrentPageSizeForReadingMode(ReaderState readerState, PdfSize pageSize)
    {
        if (ReferenceEquals(_readerState, readerState) &&
            ActiveViewportState.Mode == ReadingMode.DoublePage &&
            readerState.ZoomMode is ReaderZoomMode.FitPage or ReaderZoomMode.FitWidth)
        {
            var changed = readerState.SetCurrentPageSize(pageSize, applyZoomMode: false);
            return ApplyDoublePageFitMode(readerState.ZoomMode) || changed;
        }

        return readerState.SetCurrentPageSize(pageSize);
    }

    private IEnumerable<DocumentPageViewModel> GetCurrentSpreadPages(DocumentTabItemViewModel tab)
    {
        var spreadStart = _readerState.CurrentPageIndex - _readerState.CurrentPageIndex % 2;
        for (var pageIndex = spreadStart; pageIndex < Math.Min(spreadStart + 2, tab.Pages.Count); pageIndex++)
        {
            yield return tab.Pages[pageIndex];
        }
    }

    private async Task ChangePageAndRenderAsync(Func<bool> changePage)
    {
        if (!changePage())
        {
            return;
        }

        ClearSelection(preserveTranslationContext: true);
        AnnotationHighlightRectangles.Clear();
        CancelAnnotationEmphasis();
        NotifyReaderStateChanged();
        ScrollToCurrentPage();
        ScheduleReadingStateSave();
        await RenderCurrentPageAsync();
    }

    private async Task ChangeReaderStateAndRenderAsync(Func<bool> changeState)
    {
        if (!changeState())
        {
            return;
        }

        InvalidateActivePageImages();
        NotifyReaderStateChanged();
        ScheduleReadingStateSave();
        await RenderCurrentPageAsync();
        _ = RenderVisiblePagesAsync(CancellationToken.None);
    }

    private async Task RenderCurrentPageAsync(bool throwOnFailure = false)
    {
        var readerState = _readerState;
        if (readerState.DocumentInfo is not { } documentInfo)
        {
            return;
        }

        var renderGeneration = readerState.RenderGeneration;
        var request = new PageRenderRequest(
            Guid.NewGuid(),
            documentInfo.DocumentId,
            readerState.CurrentPageIndex,
            readerState.ZoomFactor,
            readerState.Rotation,
            NormalRenderClarityScale,
            NormalRenderClarityScale,
            RenderQuality.Normal);
        var cacheKey = new PageRenderCacheKey(
            request.DocumentId,
            request.PageIndex,
            request.ZoomFactor,
            request.Rotation,
            request.DpiScaleX,
            request.DpiScaleY,
            request.Quality);

        if (_pageRenderCoordinator.TryGet(cacheKey, out var cachedPage) && cachedPage is not null)
        {
            var shouldRerenderForFit = SetCurrentPageSizeForReadingMode(readerState, cachedPage.OriginalPageSize);
            if (ReferenceEquals(_readerState, readerState))
            {
                NotifyFitStateChanged();
            }

            if (shouldRerenderForFit)
            {
                if (ReferenceEquals(_readerState, readerState))
                {
                    NotifyReaderStateChanged();
                    await RenderCurrentPageAsync(throwOnFailure);
                }
                return;
            }

            if (ReferenceEquals(_readerState, readerState) && readerState.IsLatestRender(renderGeneration))
            {
                SetRenderedPage(readerState, request.PageIndex, cachedPage);
                SetReadyStatus(documentInfo, isCached: true);
                RebuildSelectionRectangles();
                RebuildSearchHighlightRectangles();
                RebuildAnnotationHighlightRectangles();
            }

            return;
        }

        SetRendering(true);
        StatusText = "正在渲染页面...";

        try
        {
            var descriptor = await _pdfWorkerClient.RenderPageAsync(request, CancellationToken.None);
            var shouldRerenderForFit = false;
            try
            {
                if (!ReferenceEquals(_readerState, readerState) ||
                    !readerState.IsLatestRender(renderGeneration) ||
                    descriptor.RequestId != request.RequestId)
                {
                    return;
                }

                shouldRerenderForFit = SetCurrentPageSizeForReadingMode(readerState, descriptor.OriginalPageSize);
                NotifyFitStateChanged();

                if (!shouldRerenderForFit)
                {
                    var renderedPage = _pageRenderCoordinator.Read(descriptor);
                    var bitmap = renderedPage.Bitmap;
                    if (!ReferenceEquals(_readerState, readerState) ||
                        !readerState.IsLatestRender(renderGeneration))
                    {
                        return;
                    }

                    _pageRenderCoordinator.Cache(cacheKey, renderedPage, descriptor.DataLength);
                    SetRenderedPage(readerState, request.PageIndex, renderedPage);
                    SetReadyStatus(documentInfo, isCached: false);
                    RebuildSelectionRectangles();
                    RebuildSearchHighlightRectangles();
                    RebuildAnnotationHighlightRectangles();
                }
            }
            finally
            {
                // Whether displayed or discarded, every worker-owned mapping receives an explicit release acknowledgement.
                await _pdfWorkerClient.ReleaseSharedMemoryAsync(descriptor.MemoryMapName, CancellationToken.None);
            }

            if (shouldRerenderForFit)
            {
                if (ReferenceEquals(_readerState, readerState))
                {
                    NotifyReaderStateChanged();
                    await RenderCurrentPageAsync(throwOnFailure);
                }
            }
        }
        catch (Exception exception)
        {
            if (throwOnFailure)
            {
                throw;
            }

            if (ReferenceEquals(_readerState, readerState) && readerState.IsLatestRender(renderGeneration))
            {
                var userError = _userErrorService.Report(UserErrorCode.PageRenderFailed, exception);
                StatusText = userError.InlineText;
                ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
            }
        }
        finally
        {
            SetRendering(false);
        }
    }

    private async Task RenderVisiblePagesAsync(CancellationToken cancellationToken)
    {
        if (ActiveDocumentTab is not { } tab ||
            _readerState.DocumentInfo is not { } documentInfo ||
            tab.Pages.Count == 0)
        {
            return;
        }

        var visibleArea = ActiveViewportState.VisibleArea;
        var visiblePageIndexes = tab.Pages
            .Where(page => Intersects(page.Bounds, visibleArea))
            .Select(page => page.PageIndex)
            .Append(_readerState.CurrentPageIndex)
            .SelectMany(pageIndex => new[] { pageIndex - 1, pageIndex, pageIndex + 1 })
            .Where(pageIndex => pageIndex >= 0 && pageIndex < tab.Pages.Count)
            .Distinct()
            .OrderBy(pageIndex => Math.Abs(pageIndex - _readerState.CurrentPageIndex))
            .ToArray();

        foreach (var pageIndex in visiblePageIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RenderDocumentPageAsync(tab, documentInfo, _readerState, pageIndex, cancellationToken);
        }
    }

    private async Task RenderDocumentPageAsync(
        DocumentTabItemViewModel tab,
        PdfDocumentInfo documentInfo,
        ReaderState readerState,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var renderKey = (documentInfo.DocumentId, pageIndex);
        if (!_renderingPages.Add(renderKey))
        {
            return;
        }

        var pageItem = pageIndex >= 0 && pageIndex < tab.Pages.Count
            ? tab.Pages[pageIndex]
            : null;
        if (pageItem is not null)
        {
            pageItem.IsRendering = true;
        }

        var request = new PageRenderRequest(
            Guid.NewGuid(),
            documentInfo.DocumentId,
            pageIndex,
            readerState.ZoomFactor,
            readerState.Rotation,
            NormalRenderClarityScale,
            NormalRenderClarityScale,
            RenderQuality.Normal);
        var cacheKey = new PageRenderCacheKey(
            request.DocumentId,
            request.PageIndex,
            request.ZoomFactor,
            request.Rotation,
            request.DpiScaleX,
            request.DpiScaleY,
            request.Quality);

        try
        {
            if (_pageRenderCoordinator.TryGet(cacheKey, out var cachedPage) && cachedPage is not null)
            {
                if (ReferenceEquals(ActiveDocumentTab, tab) &&
                    ReferenceEquals(_readerState, readerState))
                {
                    SetRenderedPage(readerState, pageIndex, cachedPage);
                }

                return;
            }

            SetRendering(true);
            RenderedPageDescriptor? descriptor = null;
            try
            {
                descriptor = await _pdfWorkerClient.RenderPageAsync(request, cancellationToken);
                if (!ReferenceEquals(ActiveDocumentTab, tab) ||
                    !ReferenceEquals(_readerState, readerState) ||
                    descriptor.RequestId != request.RequestId ||
                    readerState.DocumentInfo?.DocumentId != documentInfo.DocumentId)
                {
                    return;
                }

                var renderedPage = _pageRenderCoordinator.Read(descriptor);
                _pageRenderCoordinator.Cache(cacheKey, renderedPage, descriptor.DataLength);
                SetRenderedPage(readerState, pageIndex, renderedPage);
            }
            finally
            {
                if (descriptor is not null)
                {
                    await _pdfWorkerClient.ReleaseSharedMemoryAsync(
                        descriptor.MemoryMapName,
                        CancellationToken.None);
                }

                SetRendering(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "A visible PDF page could not be rendered. DocumentId: {DocumentId}; PageIndex: {PageIndex}",
                documentInfo.DocumentId.Value,
                pageIndex);
        }
        finally
        {
            if (pageItem is not null)
            {
                pageItem.IsRendering = false;
            }

            _renderingPages.Remove(renderKey);
        }
    }

    private static bool Intersects(ViewRect first, ViewRect second)
    {
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return right > Math.Max(first.X, second.X) &&
            bottom > Math.Max(first.Y, second.Y);
    }

    private void NotifyReaderStateChanged()
    {
        UpdateDocumentPageLayouts(_readerState);
        PageNumberText = HasDocument ? _readerState.CurrentPageNumber.ToString() : string.Empty;
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasNoDocument));
        OnPropertyChanged(nameof(CanUseDocumentControls));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(CanMoveToPreviousPage));
        OnPropertyChanged(nameof(CanMoveToNextPage));
        OnPropertyChanged(nameof(CanZoomOut));
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanFitPage));
        OnPropertyChanged(nameof(ZoomFactor));
        ZoomText = FormatZoomText(_readerState.ZoomFactor);
        NotifyReadingModeChanged();

        foreach (var command in _readerCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void NotifyFitStateChanged()
    {
        OnPropertyChanged(nameof(CanFitPage));

        foreach (var command in _readerCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void NotifyReadingModeChanged()
    {
        OnPropertyChanged(nameof(IsSinglePageMode));
        OnPropertyChanged(nameof(IsDoublePageMode));
    }

    private async Task<PageTextData> GetPageTextAsync(
        DocumentId documentId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var key = (documentId, pageIndex);
        if (_pageTextCache.TryGetValue(key, out var cached))
        {
            TouchPageTextCache(key);
            return cached;
        }

        var pageText = await _pdfWorkerClient.GetPageTextAsync(documentId, pageIndex, cancellationToken);
        _pageTextCache[key] = pageText;
        TouchPageTextCache(key);
        while (_pageTextCacheOrder.Count > 3)
        {
            var oldest = _pageTextCacheOrder.Last!.Value;
            _pageTextCacheOrder.RemoveLast();
            _pageTextCache.Remove(oldest);
        }

        return pageText;
    }

    private void TouchPageTextCache((DocumentId DocumentId, int PageIndex) key)
    {
        var existing = _pageTextCacheOrder.Find(key);
        if (existing is not null)
        {
            _pageTextCacheOrder.Remove(existing);
        }

        _pageTextCacheOrder.AddFirst(key);
    }

    private TextGlyph? HitTestSelection(ViewPoint viewPoint)
    {
        return _selectionPageIndex is { } pageIndex
            ? HitTestSelection(pageIndex, viewPoint, _selectionPageText)
            : null;
    }

    private TextGlyph? HitTestSelection(
        int pageIndex,
        ViewPoint viewPoint,
        PageTextData? pageText)
    {
        if (_selectionPageText is null ||
            pageText is null ||
            TryCreateTransformContext(pageIndex) is not { } context)
        {
            return null;
        }

        var pdfPoint = _coordinateTransformer.ViewToPdf(viewPoint, context);
        var pdfTolerance = 5 / (Math.Max(ReaderState.MinimumZoomFactor, _readerState.ZoomFactor) * 96d / 72d);
        return _textSelectionService.HitTest(pageText, pdfPoint, pdfTolerance);
    }

    private void UpdateSinglePageSelection(
        int endPageIndex,
        int endCharacterIndex,
        PageTextData? endPageText)
    {
        if (_selectionStartPageIndex is not { } startPageIndex ||
            _selectionStartCharacterIndex is not { } startCharacterIndex ||
            startPageIndex != endPageIndex ||
            endPageText is null)
        {
            return;
        }

        var selection = _textSelectionService.CreateSelection(
            endPageText,
            startCharacterIndex,
            endCharacterIndex);
        if (selection is null)
        {
            return;
        }

        _currentSelectionParts.Clear();
        _currentSelectionParts.Add(selection);
        CurrentSelection = selection;
        RebuildSelectionRectangles();
    }

    private async Task UpdateCurrentSelectionAsync(
        DocumentId documentId,
        int endPageIndex,
        int endCharacterIndex,
        CancellationToken cancellationToken)
    {
        if (_selectionStartPageIndex is not { } startPageIndex ||
            _selectionStartCharacterIndex is not { } startCharacterIndex)
        {
            return;
        }

        await UpdateCurrentSelectionAsync(
            documentId,
            startPageIndex,
            startCharacterIndex,
            endPageIndex,
            endCharacterIndex,
            cancellationToken);
    }

    private async Task UpdateCurrentSelectionAsync(
        DocumentId documentId,
        int startPageIndex,
        int startCharacterIndex,
        int endPageIndex,
        int endCharacterIndex,
        CancellationToken cancellationToken)
    {
        var startsBeforeEnd = startPageIndex < endPageIndex ||
            startPageIndex == endPageIndex && startCharacterIndex <= endCharacterIndex;
        var firstPageIndex = startsBeforeEnd ? startPageIndex : endPageIndex;
        var lastPageIndex = startsBeforeEnd ? endPageIndex : startPageIndex;
        var firstCharacterIndex = startsBeforeEnd ? startCharacterIndex : endCharacterIndex;
        var lastCharacterIndex = startsBeforeEnd ? endCharacterIndex : startCharacterIndex;
        var parts = new List<TextSelection>();

        for (var pageIndex = firstPageIndex; pageIndex <= lastPageIndex; pageIndex++)
        {
            var pageText = await GetPageTextAsync(documentId, pageIndex, cancellationToken);
            if (pageText.Glyphs.Count == 0)
            {
                continue;
            }

            var pageFirstCharacterIndex = pageIndex == firstPageIndex
                ? firstCharacterIndex
                : pageText.Glyphs.Min(glyph => glyph.CharacterIndex);
            var pageLastCharacterIndex = pageIndex == lastPageIndex
                ? lastCharacterIndex
                : pageText.Glyphs.Max(glyph => glyph.CharacterIndex);
            var selection = _textSelectionService.CreateSelection(
                pageText,
                pageFirstCharacterIndex,
                pageLastCharacterIndex);
            if (selection is not null)
            {
                parts.Add(selection);
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        _currentSelectionParts.Clear();
        _currentSelectionParts.AddRange(parts);
        CurrentSelection = parts.Count == 1
            ? parts[0]
            : new TextSelection(
                documentId,
                parts[0].PageIndex,
                parts[0].StartCharacterIndex,
                parts[^1].EndCharacterIndex,
                string.Join("\n", parts.Select(part => part.RawText)),
                string.Join("\n", parts.Select(part => part.NormalizedText)),
                []);
        RebuildSelectionRectangles();
    }

    private void RebuildSelectionRectangles()
    {
        SelectionRectangles.Clear();
        ClearPageSelectionRectangles();
        if (CurrentSelection is not { } selection ||
            _readerState.DocumentInfo?.DocumentId != selection.DocumentId)
        {
            SetSelectionHostPage(null);
            return;
        }

        var parts = _currentSelectionParts.Count > 0
            ? _currentSelectionParts
            : [selection];
        SetSelectionHostPages(parts.Select(part => part.PageIndex));
        var toolbarPart = parts[^1];
        ViewRect? toolbarPageBounds = null;

        foreach (var part in parts)
        {
            if (TryCreateTransformContext(part.PageIndex) is not { } context)
            {
                continue;
            }

            var pageBounds = ReadingViewLayout.CalculatePageBounds(
                context.PdfPageSize,
                _readerState.Rotation,
                _readerState.ZoomFactor);
            var page = ActiveDocumentTab?.Pages.FirstOrDefault(page => page.PageIndex == part.PageIndex);
            if (page is not null)
            {
                pageBounds = page.Bounds;
            }

            if (part.PageIndex == toolbarPart.PageIndex)
            {
                toolbarPageBounds = pageBounds;
            }

            foreach (var rectangle in part.HighlightRectangles)
            {
                var viewRectangle = _coordinateTransformer.PdfToView(rectangle, context);
                var viewModel = new SelectionRectangleViewModel(
                    part.PageIndex,
                    viewRectangle.X,
                    viewRectangle.Y,
                    viewRectangle.Width,
                    viewRectangle.Height);
                SelectionRectangles.Add(viewModel);
                page?.SelectionRectangles.Add(viewModel);
            }
        }

        if (SelectionRectangles.Count > 0)
        {
            var toolbarRectangles = SelectionRectangles
                .Where(rectangle => rectangle.PageIndex == toolbarPart.PageIndex)
                .ToArray();
            if (toolbarRectangles.Length == 0)
            {
                return;
            }

            var selectionLeft = toolbarRectangles.Min(rectangle => rectangle.X);
            var selectionTop = toolbarRectangles.Min(rectangle => rectangle.Y);
            var selectionBottom = toolbarRectangles.Max(rectangle => rectangle.Y + rectangle.Height);
            SelectionToolbarX = Math.Clamp(
                selectionLeft,
                0,
                Math.Max(0, (toolbarPageBounds?.Width ?? PageDisplayWidth) - 430));
            SelectionToolbarY = selectionTop >= 48
                ? selectionTop - 44
                : Math.Min(Math.Max(0, (toolbarPageBounds?.Height ?? PageDisplayHeight) - 40), selectionBottom + 6);
        }
    }

    private void RebuildSearchHighlightRectangles()
    {
        SearchHighlightRectangles.Clear();
        if (ActiveDocumentTab?.Search.SelectedResult is not { } result ||
            _readerState.CurrentPageIndex != result.PageIndex ||
            _readerState.CurrentPageSize is null)
        {
            return;
        }

        var context = CreateTransformContext();
        foreach (var rectangle in result.HighlightRectangles)
        {
            var viewRectangle = _coordinateTransformer.PdfToView(rectangle, context);
            SearchHighlightRectangles.Add(new SelectionRectangleViewModel(
                result.PageIndex,
                viewRectangle.X,
                viewRectangle.Y,
                viewRectangle.Width,
                viewRectangle.Height));
        }
    }

    private void OnSearchSelectedResultChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(ActiveDocumentTab?.Search, sender))
        {
            RebuildSearchHighlightRectangles();
        }
    }

    private void ClearSearchSelectionHighlight()
    {
        if (ActiveDocumentTab?.Search is { } search)
        {
            search.SelectedResult = null;
        }

        SearchHighlightRectangles.Clear();
    }

    private void RebuildAnnotationHighlightRectangles()
    {
        AnnotationHighlightRectangles.Clear();
        if (ActiveDocumentTab is not { } tab || _readerState.CurrentPageSize is null)
        {
            return;
        }

        var context = CreateTransformContext();
        foreach (var item in tab.Annotations.Items.Where(item =>
                     item.Annotation.PageIndex == _readerState.CurrentPageIndex))
        {
            var annotation = item.Annotation;
            var fill = AnnotationBrushes[annotation.Color];
            foreach (var rectangle in annotation.Rectangles)
            {
                var viewRectangle = _coordinateTransformer.PdfToView(rectangle, context);
                AnnotationHighlightRectangles.Add(new AnnotationRectangleViewModel(
                    viewRectangle.X,
                    viewRectangle.Y,
                    viewRectangle.Width,
                    viewRectangle.Height,
                    fill,
                    annotation.AnnotationId == _emphasizedAnnotationId));
            }
        }
    }

    private PageTransformContext CreateTransformContext()
    {
        var pageSize = _readerState.CurrentPageSize!.Value;
        return ReadingViewLayout.CreateTransformContext(
            pageSize,
            _readerState.ZoomFactor,
            _readerState.Rotation);
    }

    private PageTransformContext? TryCreateTransformContext(int pageIndex)
    {
        if (ActiveDocumentTab?.Pages.FirstOrDefault(page => page.PageIndex == pageIndex) is not { } page)
        {
            return pageIndex == _readerState.CurrentPageIndex && _readerState.CurrentPageSize is { } currentPageSize
                ? ReadingViewLayout.CreateTransformContext(
                    currentPageSize,
                    _readerState.ZoomFactor,
                    _readerState.Rotation)
                : null;
        }

        return ReadingViewLayout.CreateTransformContext(
            page.PdfPageSize,
            _readerState.ZoomFactor,
            _readerState.Rotation);
    }

    private bool CanCreateHighlight()
    {
        if (_annotationService is null ||
            CurrentSelection is not { } selection ||
            _currentSelectionParts.Count != 1 ||
            ActiveDocumentTab is not { PersistenceRecord: not null } tab ||
            !tab.Annotations.IsAvailable)
        {
            return false;
        }

        return tab.ReaderState.DocumentInfo?.DocumentId == selection.DocumentId;
    }

    private Task CreateHighlightAsync(bool openNoteEditor) =>
        CreateHighlightAsync(SelectedAnnotationColor, openNoteEditor, CancellationToken.None);

    public async Task CreateHighlightAsync(
        AnnotationColor color,
        bool openNoteEditor,
        CancellationToken cancellationToken)
    {
        if (!CanCreateHighlight() ||
            _annotationService is null ||
            CurrentSelection is not { } selection ||
            ActiveDocumentTab is not { PersistenceRecord: { } document } tab)
        {
            return;
        }

        try
        {
            var annotation = await _annotationService.CreateHighlightAsync(
                document,
                selection,
                color,
                note: null,
                cancellationToken);
            var item = tab.Annotations.Add(annotation);
            tab.HasUnsavedAnnotations = true;
            ClearSelection(preserveTranslationContext: true);
            RebuildAnnotationHighlightRectangles();

            if (openNoteEditor)
            {
                OpenLeftSidebar(3);
                tab.Annotations.SelectedItem = item;
                StatusText = "高亮已保存，请在左侧批注栏填写笔记后点击“保存修改”。";
            }
            else
            {
                StatusText = "高亮已保存到本地数据库，原 PDF 文件未被修改。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportAnnotationError(UserErrorCode.AnnotationWriteFailed, exception);
        }
    }

    private void ReportAnnotationError(UserErrorCode code, Exception exception)
    {
        var userError = _userErrorService.Report(code, exception);
        StatusText = userError.InlineText;
        ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
    }

    private Task CopySelectionAsync()
    {
        if (CurrentSelection is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            System.Windows.Clipboard.SetText(CurrentSelection.NormalizedText);
            StatusText = "已复制规范化文字。";
        }
        catch (Exception exception)
        {
            var userError = _userErrorService.Report(UserErrorCode.ClipboardWriteFailed, exception);
            StatusText = userError.InlineText;
            ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
        }

        return Task.CompletedTask;
    }

    private Task CopyRawSelectionAsync()
    {
        if (CurrentSelection is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            System.Windows.Clipboard.SetText(CurrentSelection.RawText);
            StatusText = "已复制 PDF 原始提取文字。";
        }
        catch (Exception exception)
        {
            var userError = _userErrorService.Report(UserErrorCode.ClipboardWriteFailed, exception);
            StatusText = userError.InlineText;
            ErrorOccurred?.Invoke(this, new ReaderErrorEventArgs(userError));
        }

        return Task.CompletedTask;
    }

    private void ClearSelection(
        bool keepPointerSelecting = false,
        bool preserveTranslationContext = false)
    {
        CancelTextSelectionRequest();
        if (!keepPointerSelecting)
        {
            _isPointerSelecting = false;
        }

        _selectionPageText = null;
        _selectionPageIndex = null;
        SetSelectionHostPage(null);
        IsSelectionToolbarVisible = false;
        _currentSelectionParts.Clear();
        _selectionStartCharacterIndex = null;
        _selectionStartViewPoint = null;
        _hasSelectionDragStarted = false;
        _selectionChangedInCurrentGesture = false;
        if (preserveTranslationContext)
        {
            if (SetProperty(ref _currentSelection, null, nameof(CurrentSelection)))
            {
                OnPropertyChanged(nameof(HasSelection));
                RaiseCommandCanExecuteChanged();
            }
        }
        else
        {
            CurrentSelection = null;
        }
        SelectionRectangles.Clear();
        ClearPageSelectionRectangles();
    }

    private void SetSelectionHostPage(int? pageIndex)
    {
        SetSelectionHostPages(pageIndex.HasValue ? [pageIndex.Value] : []);
    }

    private void SetSelectionHostPages(IEnumerable<int> pageIndexes)
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return;
        }

        var pageIndexSet = pageIndexes.ToHashSet();
        foreach (var page in tab.Pages)
        {
            page.IsSelectionPage = pageIndexSet.Contains(page.PageIndex);
        }
    }

    private void ClearPageSelectionRectangles()
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return;
        }

        foreach (var page in tab.Pages)
        {
            page.SelectionRectangles.Clear();
        }
    }

    private void CancelTextSelectionRequest()
    {
        _textSelectionCancellationSource?.Cancel();
        _textSelectionCancellationSource?.Dispose();
        _textSelectionCancellationSource = null;
    }

    private void BeginAnnotationEmphasis(Guid annotationId)
    {
        CancelAnnotationEmphasis();
        _emphasizedAnnotationId = annotationId;
        _annotationEmphasisCancellationSource = new CancellationTokenSource();
        _ = ClearAnnotationEmphasisAfterDelayAsync(
            annotationId,
            _annotationEmphasisCancellationSource.Token);
    }

    private async Task ClearAnnotationEmphasisAfterDelayAsync(
        Guid annotationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            if (_emphasizedAnnotationId == annotationId)
            {
                _emphasizedAnnotationId = null;
                RebuildAnnotationHighlightRectangles();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelAnnotationEmphasis()
    {
        _annotationEmphasisCancellationSource?.Cancel();
        _annotationEmphasisCancellationSource?.Dispose();
        _annotationEmphasisCancellationSource = null;
        _emphasizedAnnotationId = null;
    }

    private async Task RenderAfterViewportChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await RenderCurrentPageAsync();
            await RenderVisiblePagesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<DocumentAnnotationViewModel> LoadAnnotationsAsync(
        DocumentRecord? document,
        CancellationToken cancellationToken)
    {
        if (document is null || _annotationService is null)
        {
            return new DocumentAnnotationViewModel(
                isAvailable: false,
                "本地阅读数据不可用，当前文档不能创建或恢复批注。");
        }

        try
        {
            var annotations = await _annotationService.GetByDocumentAsync(
                document.DocumentId,
                cancellationToken);
            return new DocumentAnnotationViewModel(
                isAvailable: true,
                "批注会先保存到本地，之后可写入 PDF 文件。",
                annotations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Annotations could not be loaded for a PDF document.");
            return new DocumentAnnotationViewModel(
                isAvailable: false,
                "批注加载失败；PDF 阅读仍可继续，请查看本地日志。");
        }
    }

    private async Task ReloadAnnotationsAsync(
        DocumentTabItemViewModel tab,
        CancellationToken cancellationToken)
    {
        if (tab.PersistenceRecord is not { } document || _annotationService is null)
        {
            tab.Annotations.SetAvailability(
                isAvailable: false,
                "本地阅读数据不可用，当前文档不能创建或恢复批注。");
            return;
        }

        try
        {
            var annotations = await _annotationService.GetByDocumentAsync(
                document.DocumentId,
                cancellationToken);
            tab.Annotations.ReplaceAll(annotations);
            tab.Annotations.SetAvailability(
                isAvailable: true,
                "批注会先保存到本地，之后可写入 PDF 文件。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Annotations could not be reloaded after PDF worker recovery.");
            tab.Annotations.SetAvailability(
                isAvailable: false,
                "Worker 已恢复，但批注重新加载失败；现有页面仍可阅读。");
        }
    }

    private async Task RefreshRecentDocumentsAsync(CancellationToken cancellationToken)
    {
        if (_documentHistoryService is null)
        {
            return;
        }

        try
        {
            var recent = await _documentHistoryService.GetRecentAsync(cancellationToken);
            RecentDocuments.Clear();
            foreach (var item in recent)
            {
                RecentDocuments.Add(new RecentDocumentItemViewModel(
                    item.Document.DocumentId,
                    item.Document.FileName,
                    item.Document.LastKnownPath,
                    item.LastOpenedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    item.LastPageIndex is { } pageIndex ? $"第 {pageIndex + 1} 页" : "尚无阅读位置",
                    item.Document.IsMissing));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Recent documents could not be refreshed.");
        }
    }

    private async Task RemoveMissingRecentDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_documentHistoryService is null)
        {
            return;
        }

        try
        {
            await _documentHistoryService.MarkMissingAndRemoveFromRecentAsync(documentId, cancellationToken);
            await RefreshRecentDocumentsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "A missing document could not be removed from recent history.");
        }
    }

    private void ScheduleReadingStateSave()
    {
        if (ActiveDocumentTab?.PersistenceRecord is null || _documentHistoryService is null)
        {
            return;
        }

        _readingStateCoordinator.Schedule(SaveReadingStateCoreAsync);
    }

    private async Task SaveReadingStateCoreAsync(CancellationToken cancellationToken)
    {
        if (ActiveDocumentTab is not { } tab)
        {
            return;
        }

        CaptureActiveTabState();
        await SaveReadingStateForTabAsync(tab, cancellationToken);
    }

    private async Task SaveReadingStateForTabAsync(
        DocumentTabItemViewModel tab,
        CancellationToken cancellationToken)
    {
        if (tab.PersistenceRecord is not { } document ||
            _documentHistoryService is null ||
            !tab.ReaderState.HasDocument)
        {
            return;
        }

        try
        {
            await _documentHistoryService.SaveReadingStateAsync(
                document.DocumentId,
                tab.ReaderState.CaptureViewState(),
                tab.HorizontalScrollOffset,
                tab.VerticalScrollOffset,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Reading state could not be persisted.");
        }
    }

    private async Task SaveSessionAsync(CancellationToken cancellationToken)
    {
        if (_documentSessionService is null)
        {
            return;
        }

        try
        {
            var persistedTabs = DocumentTabs
                .Where(tab => tab.PersistenceRecord is not null)
                .ToArray();
            var activeTabIndex = Array.IndexOf(persistedTabs, ActiveDocumentTab!);
            var snapshot = new DocumentSessionSnapshot(
                persistedTabs.Select(tab => new DocumentSessionTab(
                    tab.PersistenceRecord!.DocumentId,
                    tab.FullPath,
                    IsMissing: false)).ToArray(),
                activeTabIndex < 0 ? 0 : activeTabIndex,
                DateTimeOffset.UtcNow);
            await _documentSessionService.SaveAsync(snapshot, cancellationToken);
            HasRestorableSession = snapshot.Tabs.Count > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "The current document session could not be saved.");
        }
    }

    private async Task UpdateRestorableSessionAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (_documentSessionService is null)
        {
            return;
        }

        try
        {
            HasRestorableSession = (await _documentSessionService.GetAsync(cancellationToken)) is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "The saved document session could not be inspected.");
        }
    }

    private void ClearDocumentCaches(DocumentId documentId)
    {
        _pageRenderCoordinator.ClearDocument(documentId);
        foreach (var tab in DocumentTabs.Where(tab => tab.ReaderState.DocumentInfo?.DocumentId == documentId))
        {
            foreach (var page in tab.Pages)
            {
                page.ClearImage();
            }

            tab.Navigation.ClearThumbnailImages();
        }

        foreach (var key in _pageTextCache.Keys.Where(key => key.DocumentId == documentId).ToArray())
        {
            _pageTextCache.Remove(key);
        }

        var node = _pageTextCacheOrder.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.DocumentId == documentId)
            {
                _pageTextCacheOrder.Remove(node);
            }

            node = next;
        }
    }

    private void ResetToHomePage()
    {
        CancelReadingStateSave();
        _viewportRenderCancellationSource?.Cancel();
        CancelTextSelectionRequest();
        ActiveDocumentTab = null;
        _readerState = new ReaderState();
        _homeViewportState.ResetScrollOffsets();
        OnPropertyChanged(nameof(HorizontalScrollOffset));
        OnPropertyChanged(nameof(VerticalScrollOffset));
        ClearSelection(preserveTranslationContext: true);
        Translation.RestoreSnapshot(Translation.CreateEmptySnapshot());
        PageImage = null;
        SearchHighlightRectangles.Clear();
        AnnotationHighlightRectangles.Clear();
        CancelAnnotationEmphasis();
        StatusText = "请选择一个 PDF 文件。";
        NotifyReaderStateChanged();
    }

    private void CancelReadingStateSave()
    {
        _readingStateCoordinator.Cancel();
    }

    private void SetReadyStatus(PdfDocumentInfo documentInfo, bool isCached)
    {
        var cacheStatus = isCached ? "，来自内存缓存" : string.Empty;
        StatusText = $"{documentInfo.FileName}：第 {_readerState.CurrentPageNumber}/{_readerState.PageCount} 页，缩放 {_readerState.ZoomFactor:P0}，旋转 {(int)_readerState.Rotation}°{cacheStatus}。";
    }

    private void SetRendering(bool isStarting)
    {
        var wasRendering = IsRendering;
        _activeRenderCount += isStarting ? 1 : -1;

        if (wasRendering != IsRendering)
        {
            OnPropertyChanged(nameof(IsRendering));
        }
    }

    private static Brush CreateFrozenBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static bool TryParseZoomText(string text, out double zoomFactor)
    {
        zoomFactor = 0;
        var normalized = (text ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.EndsWith('%'))
        {
            normalized = normalized[..^1].Trim();
            if (!double.TryParse(
                    normalized,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out var percent))
            {
                return false;
            }

            zoomFactor = percent / 100d;
        }
        else if (!double.TryParse(
                     normalized,
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.CurrentCulture,
                     out zoomFactor))
        {
            return false;
        }
        else if (zoomFactor > ReaderState.MaximumZoomFactor)
        {
            zoomFactor /= 100d;
        }

        return double.IsFinite(zoomFactor)
            && zoomFactor >= ReaderState.MinimumZoomFactor
            && zoomFactor <= ReaderState.MaximumZoomFactor;
    }

    private static string FormatZoomText(double zoomFactor) => $"{zoomFactor:P0}";

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RaiseCommandCanExecuteChanged()
    {
        foreach (var command in _readerCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

}

public sealed record SelectionRectangleViewModel(int PageIndex, double X, double Y, double Width, double Height);

public sealed record ScrollOffsetRequestedEventArgs(double HorizontalOffset, double VerticalOffset);

public sealed record AnnotationRectangleViewModel(
    double X,
    double Y,
    double Width,
    double Height,
    Brush Fill,
    bool IsEmphasized);

public sealed record RecentDocumentItemViewModel(
    Guid DocumentId,
    string FileName,
    string FullPath,
    string LastOpenedText,
    string PageText,
    bool IsMissing)
{
    public string AvailabilityText => IsMissing ? "文件不存在" : string.Empty;
}

public sealed record RecentlyClosedTabItemViewModel(string FileName, string FullPath, Guid? DocumentId);

public enum OpenDocumentResult
{
    Opened,
    ActivatedExisting,
    PasswordRequiredOrInvalid,
    Cancelled,
    Failed
}

public sealed class ReaderErrorEventArgs(UserFacingError error) : EventArgs
{
    public UserFacingError Error { get; } = error;
}
