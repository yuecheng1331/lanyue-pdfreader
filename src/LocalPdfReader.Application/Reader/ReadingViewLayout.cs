using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Reader;

public enum ReadingMode
{
    SinglePage,
    DoublePage
}

public sealed class ReadingViewportState
{
    private const double OffsetTolerance = 0.1;

    public ReadingMode Mode { get; private set; } = ReadingMode.SinglePage;

    public double ViewportWidth { get; private set; }

    public double ViewportHeight { get; private set; }

    public double HorizontalOffset { get; private set; }

    public double VerticalOffset { get; private set; }

    public ViewRect VisibleArea => new(HorizontalOffset, VerticalOffset, ViewportWidth, ViewportHeight);

    public bool SetMode(ReadingMode mode)
    {
        if (!Enum.IsDefined(mode) || Mode == mode)
        {
            return false;
        }

        Mode = mode;
        return true;
    }

    public bool UpdateViewportSize(double width, double height)
    {
        if (!IsPositiveFinite(width) || !IsPositiveFinite(height))
        {
            return false;
        }

        if (Math.Abs(ViewportWidth - width) < OffsetTolerance &&
            Math.Abs(ViewportHeight - height) < OffsetTolerance)
        {
            return false;
        }

        ViewportWidth = width;
        ViewportHeight = height;
        return true;
    }

    public bool UpdateScrollOffsets(double horizontalOffset, double verticalOffset)
    {
        if (!IsNonNegativeFinite(horizontalOffset) || !IsNonNegativeFinite(verticalOffset))
        {
            return false;
        }

        if (Math.Abs(HorizontalOffset - horizontalOffset) < OffsetTolerance &&
            Math.Abs(VerticalOffset - verticalOffset) < OffsetTolerance)
        {
            return false;
        }

        HorizontalOffset = horizontalOffset;
        VerticalOffset = verticalOffset;
        return true;
    }

    public void ResetScrollOffsets()
    {
        HorizontalOffset = 0;
        VerticalOffset = 0;
    }

    private static bool IsPositiveFinite(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool IsNonNegativeFinite(double value) =>
        double.IsFinite(value) && value >= 0;
}

public sealed record ReadingPageLayout(
    int PageIndex,
    PdfSize PdfPageSize,
    PageRotation Rotation,
    ViewRect Bounds);

public static class ReadingViewLayout
{
    private const double PdfPointToDeviceIndependentPixel = 96d / 72d;

    public static ViewRect CalculatePageBounds(
        PdfSize pageSize,
        PageRotation rotation,
        double zoomFactor,
        double pageOffsetX = 0,
        double pageOffsetY = 0)
    {
        if (!double.IsFinite(pageSize.Width) || !double.IsFinite(pageSize.Height) ||
            pageSize.Width <= 0 || pageSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "The PDF page size must be positive and finite.");
        }

        if (!double.IsFinite(zoomFactor) || zoomFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoomFactor), "The zoom factor must be positive and finite.");
        }

        var isQuarterTurn = rotation is PageRotation.Rotate90 or PageRotation.Rotate270;
        var rotatedWidth = isQuarterTurn ? pageSize.Height : pageSize.Width;
        var rotatedHeight = isQuarterTurn ? pageSize.Width : pageSize.Height;
        var scale = zoomFactor * PdfPointToDeviceIndependentPixel;

        return new ViewRect(
            pageOffsetX,
            pageOffsetY,
            rotatedWidth * scale,
            rotatedHeight * scale);
    }

    public static PageTransformContext CreateTransformContext(
        PdfSize pageSize,
        double zoomFactor,
        PageRotation rotation,
        double pageOffsetX = 0,
        double pageOffsetY = 0) =>
        new(
            pageSize,
            zoomFactor,
            rotation,
            DpiScaleX: 1,
            DpiScaleY: 1,
            pageOffsetX,
            pageOffsetY);

    public static int GetDominantVisiblePageIndex(
        IReadOnlyList<ReadingPageLayout> pages,
        ViewRect visibleArea,
        int fallbackPageIndex)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            return fallbackPageIndex;
        }

        var bestPageIndex = fallbackPageIndex;
        var bestVisibleArea = 0d;
        foreach (var page in pages)
        {
            var visibleAreaForPage = CalculateIntersectionArea(page.Bounds, visibleArea);
            if (visibleAreaForPage > bestVisibleArea)
            {
                bestVisibleArea = visibleAreaForPage;
                bestPageIndex = page.PageIndex;
            }
        }

        return bestVisibleArea > 0 ? bestPageIndex : fallbackPageIndex;
    }

    private static double CalculateIntersectionArea(ViewRect first, ViewRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        return width * height;
    }
}
