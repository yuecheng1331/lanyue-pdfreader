using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Reader;

public sealed class ReaderState
{
    private const double PdfPointToDeviceIndependentPixel = 96d / 72d;

    public const double MinimumZoomFactor = 0.1;
    public const double MaximumZoomFactor = 8.0;
    public const double ZoomStep = 0.25;

    public PdfDocumentInfo? DocumentInfo { get; private set; }

    public int CurrentPageIndex { get; private set; }

    public double ZoomFactor { get; private set; } = 1;

    public PageRotation Rotation { get; private set; } = PageRotation.Rotate0;

    public ReaderZoomMode ZoomMode { get; private set; } = ReaderZoomMode.ActualZoom;

    public PdfSize? CurrentPageSize { get; private set; }

    public double ViewportWidth { get; private set; }

    public double ViewportHeight { get; private set; }

    public long RenderGeneration { get; private set; }

    public bool HasDocument => DocumentInfo is not null;

    public int PageCount => DocumentInfo?.PageCount ?? 0;

    public int CurrentPageNumber => HasDocument ? CurrentPageIndex + 1 : 0;

    public void Open(PdfDocumentInfo documentInfo)
    {
        ArgumentNullException.ThrowIfNull(documentInfo);

        if (documentInfo.PageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentInfo), "A PDF document must contain at least one page.");
        }

        DocumentInfo = documentInfo;
        CurrentPageIndex = 0;
        ZoomFactor = 1;
        Rotation = PageRotation.Rotate0;
        ZoomMode = ReaderZoomMode.ActualZoom;
        CurrentPageSize = null;
        RenderGeneration++;
    }

    public void Close()
    {
        DocumentInfo = null;
        CurrentPageIndex = 0;
        ZoomFactor = 1;
        Rotation = PageRotation.Rotate0;
        ZoomMode = ReaderZoomMode.ActualZoom;
        CurrentPageSize = null;
        RenderGeneration++;
    }

    public bool MoveToPreviousPage() => SetPageIndex(CurrentPageIndex - 1);

    public bool MoveToNextPage() => SetPageIndex(CurrentPageIndex + 1);

    public bool SetPageNumber(int pageNumber) => SetPageIndex(pageNumber - 1);

    public bool ZoomOut() => SetZoomFactor(ZoomFactor - ZoomStep);

    public bool ZoomIn() => SetZoomFactor(ZoomFactor + ZoomStep);

    public bool SetZoomFactor(double zoomFactor)
    {
        if (!HasDocument || double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor))
        {
            return false;
        }

        var modeChanged = ZoomMode != ReaderZoomMode.ActualZoom;
        ZoomMode = ReaderZoomMode.ActualZoom;
        if (SetCalculatedZoomFactor(zoomFactor))
        {
            return true;
        }

        if (modeChanged)
        {
            RenderGeneration++;
            return true;
        }

        return false;
    }

    public bool FitPage() => SetZoomMode(ReaderZoomMode.FitPage);

    public bool FitWidth() => SetZoomMode(ReaderZoomMode.FitWidth);

    public bool SetZoomModeFactor(ReaderZoomMode zoomMode, double zoomFactor)
    {
        if (!HasDocument || !Enum.IsDefined(zoomMode) ||
            double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor))
        {
            return false;
        }

        var modeChanged = ZoomMode != zoomMode;
        ZoomMode = zoomMode;
        if (SetCalculatedZoomFactor(zoomFactor))
        {
            return true;
        }

        if (modeChanged)
        {
            RenderGeneration++;
            return true;
        }

        return false;
    }

    public bool UpdateViewportSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return false;
        }

        ViewportWidth = width;
        ViewportHeight = height;
        return ZoomMode != ReaderZoomMode.ActualZoom && ApplyZoomMode();
    }

    public bool SetCurrentPageSize(PdfSize pageSize, bool applyZoomMode = true)
    {
        if (!double.IsFinite(pageSize.Width) || !double.IsFinite(pageSize.Height) ||
            pageSize.Width <= 0 || pageSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "The PDF page size must be positive and finite.");
        }

        CurrentPageSize = pageSize;
        return applyZoomMode && ZoomMode != ReaderZoomMode.ActualZoom && ApplyZoomMode();
    }

    public bool RotateClockwise()
    {
        if (!HasDocument)
        {
            return false;
        }

        Rotation = Rotation switch
        {
            PageRotation.Rotate0 => PageRotation.Rotate90,
            PageRotation.Rotate90 => PageRotation.Rotate180,
            PageRotation.Rotate180 => PageRotation.Rotate270,
            _ => PageRotation.Rotate0
        };
        RenderGeneration++;

        if (ZoomMode != ReaderZoomMode.ActualZoom)
        {
            ApplyZoomMode();
        }

        return true;
    }

    public bool IsLatestRender(long renderGeneration) => renderGeneration == RenderGeneration;

    public ReaderViewState CaptureViewState() => new(
        CurrentPageIndex,
        ZoomFactor,
        Rotation,
        ZoomMode,
        CurrentPageSize);

    public void Restore(PdfDocumentInfo documentInfo, ReaderViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(documentInfo);
        ArgumentNullException.ThrowIfNull(viewState);

        if (documentInfo.PageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentInfo), "A PDF document must contain at least one page.");
        }

        DocumentInfo = documentInfo;
        CurrentPageIndex = Math.Clamp(viewState.PageIndex, 0, documentInfo.PageCount - 1);
        ZoomFactor = double.IsFinite(viewState.ZoomFactor)
            ? Math.Clamp(viewState.ZoomFactor, MinimumZoomFactor, MaximumZoomFactor)
            : 1;
        Rotation = Enum.IsDefined(viewState.Rotation) ? viewState.Rotation : PageRotation.Rotate0;
        ZoomMode = Enum.IsDefined(viewState.ZoomMode) ? viewState.ZoomMode : ReaderZoomMode.ActualZoom;
        CurrentPageSize = viewState.PageSize;
        RenderGeneration++;

        if (ZoomMode != ReaderZoomMode.ActualZoom && CurrentPageSize is not null &&
            ViewportWidth > 0 && ViewportHeight > 0)
        {
            ApplyZoomMode();
        }
    }

    private bool SetPageIndex(int pageIndex)
    {
        if (!HasDocument || pageIndex < 0 || pageIndex >= PageCount || pageIndex == CurrentPageIndex)
        {
            return false;
        }

        CurrentPageIndex = pageIndex;
        CurrentPageSize = null;
        RenderGeneration++;
        return true;
    }

    private bool SetZoomMode(ReaderZoomMode zoomMode)
    {
        if (!HasDocument || CurrentPageSize is null || ViewportWidth <= 0 || ViewportHeight <= 0)
        {
            return false;
        }

        var modeChanged = ZoomMode != zoomMode;
        ZoomMode = zoomMode;
        if (ApplyZoomMode())
        {
            return true;
        }

        if (modeChanged)
        {
            RenderGeneration++;
            return true;
        }

        return false;
    }

    private bool ApplyZoomMode()
    {
        if (CurrentPageSize is not { } pageSize)
        {
            return false;
        }

        var isQuarterTurn = Rotation is PageRotation.Rotate90 or PageRotation.Rotate270;
        var rotatedWidth = isQuarterTurn ? pageSize.Height : pageSize.Width;
        var rotatedHeight = isQuarterTurn ? pageSize.Width : pageSize.Height;
        var widthZoom = ViewportWidth / (rotatedWidth * PdfPointToDeviceIndependentPixel);
        var calculatedZoom = ZoomMode == ReaderZoomMode.FitWidth
            ? widthZoom
            : Math.Min(widthZoom, ViewportHeight / (rotatedHeight * PdfPointToDeviceIndependentPixel));
        return SetCalculatedZoomFactor(calculatedZoom);
    }

    private bool SetCalculatedZoomFactor(double zoomFactor)
    {
        var boundedZoomFactor = Math.Clamp(zoomFactor, MinimumZoomFactor, MaximumZoomFactor);
        if (Math.Abs(boundedZoomFactor - ZoomFactor) < 0.0001)
        {
            return false;
        }

        ZoomFactor = boundedZoomFactor;
        RenderGeneration++;
        return true;
    }
}

public enum ReaderZoomMode
{
    ActualZoom,
    FitPage,
    FitWidth
}

public sealed record ReaderViewState(
    int PageIndex,
    double ZoomFactor,
    PageRotation Rotation,
    ReaderZoomMode ZoomMode,
    PdfSize? PageSize);
