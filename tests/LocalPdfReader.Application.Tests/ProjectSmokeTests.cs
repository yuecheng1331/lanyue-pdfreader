using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Tests;

public class ProjectSmokeTests
{
    [Fact]
    public void ApplicationProjectIsAvailable()
    {
        Assert.True(true);
    }

    [Fact]
    public void PageChangesStayWithinDocumentBounds()
    {
        var state = CreateOpenState(pageCount: 3);

        Assert.False(state.MoveToPreviousPage());
        Assert.True(state.MoveToNextPage());
        Assert.True(state.MoveToNextPage());
        Assert.False(state.MoveToNextPage());
        Assert.Equal(3, state.CurrentPageNumber);
        Assert.False(state.SetPageNumber(0));
        Assert.False(state.SetPageNumber(4));
        Assert.True(state.SetPageNumber(2));
        Assert.Equal(2, state.CurrentPageNumber);
    }

    [Fact]
    public void ZoomChangesAreClampedToSupportedRange()
    {
        var state = CreateOpenState(pageCount: 1);

        Assert.True(state.SetZoomFactor(0));
        Assert.Equal(ReaderState.MinimumZoomFactor, state.ZoomFactor);
        Assert.False(state.ZoomOut());

        Assert.True(state.SetZoomFactor(100));
        Assert.Equal(ReaderState.MaximumZoomFactor, state.ZoomFactor);
        Assert.False(state.ZoomIn());
    }

    [Fact]
    public void ClockwiseRotationReturnsToZeroAfterFourSteps()
    {
        var state = CreateOpenState(pageCount: 1);

        Assert.True(state.RotateClockwise());
        Assert.Equal(PageRotation.Rotate90, state.Rotation);
        Assert.True(state.RotateClockwise());
        Assert.True(state.RotateClockwise());
        Assert.True(state.RotateClockwise());
        Assert.Equal(PageRotation.Rotate0, state.Rotation);
    }

    [Fact]
    public void OnlyTheLatestRenderGenerationIsCurrent()
    {
        var state = CreateOpenState(pageCount: 2);
        var firstGeneration = state.RenderGeneration;

        Assert.True(state.MoveToNextPage());

        Assert.False(state.IsLatestRender(firstGeneration));
        Assert.True(state.IsLatestRender(state.RenderGeneration));
    }

    [Fact]
    public void ReaderViewStateCanBeRestoredForANewWorkerDocumentId()
    {
        var state = CreateOpenState(pageCount: 4);
        state.SetPageNumber(3);
        state.SetZoomFactor(1.75);
        state.RotateClockwise();
        var viewState = state.CaptureViewState();
        var replacementDocumentId = new DocumentId(Guid.NewGuid());

        state.Restore(
            new PdfDocumentInfo(
                replacementDocumentId,
                "reopened.pdf",
                4,
                IsEncrypted: false,
                HasTextLayer: true,
                Title: null,
                Author: null),
            viewState);

        Assert.Equal(replacementDocumentId, state.DocumentInfo?.DocumentId);
        Assert.Equal(3, state.CurrentPageNumber);
        Assert.Equal(1.75, state.ZoomFactor);
        Assert.Equal(PageRotation.Rotate90, state.Rotation);
        Assert.Equal(ReaderZoomMode.ActualZoom, state.ZoomMode);
    }

    [Fact]
    public void ReaderViewStateRestoreKeepsFitWidthMode()
    {
        var state = CreateOpenState(pageCount: 2);
        state.SetCurrentPageSize(new PdfSize(612, 792));
        state.UpdateViewportSize(width: 816, height: 528);
        state.FitWidth();
        var viewState = state.CaptureViewState();

        state.Restore(
            new PdfDocumentInfo(
                new DocumentId(Guid.NewGuid()),
                "reopened.pdf",
                2,
                IsEncrypted: false,
                HasTextLayer: true,
                Title: null,
                Author: null),
            viewState);

        Assert.Equal(ReaderZoomMode.FitWidth, state.ZoomMode);
        Assert.Equal(1, state.ZoomFactor, precision: 3);
    }

    [Fact]
    public void PdfiumGeneratedLineBreakSupportsEnglishDehyphenation()
    {
        var service = new TextSelectionService();

        var normalized = service.NormalizeText("An inter\u0002national example");

        Assert.Equal("An international example", normalized);
    }

    [Fact]
    public void FitPageUsesTheSmallerWidthAndHeightScale()
    {
        var state = CreateOpenState(pageCount: 1);
        state.SetCurrentPageSize(new PdfSize(612, 792));
        state.UpdateViewportSize(width: 816, height: 528);

        Assert.True(state.FitPage());

        Assert.Equal(0.5, state.ZoomFactor, precision: 3);
        Assert.Equal(ReaderZoomMode.FitPage, state.ZoomMode);

        Assert.True(state.UpdateViewportSize(width: 816, height: 264));
        Assert.Equal(0.25, state.ZoomFactor, precision: 3);
    }

    [Fact]
    public void FitPageRemainsStableWhenViewportAndPageSizeDoNotChange()
    {
        var state = CreateOpenState(pageCount: 1);
        var pageSize = new PdfSize(612, 792);
        state.SetCurrentPageSize(pageSize);
        state.UpdateViewportSize(width: 816, height: 528);
        Assert.True(state.FitPage());
        var stableGeneration = state.RenderGeneration;
        var stableZoom = state.ZoomFactor;

        Assert.False(state.UpdateViewportSize(width: 816, height: 528));
        Assert.False(state.SetCurrentPageSize(pageSize));

        Assert.Equal(stableGeneration, state.RenderGeneration);
        Assert.Equal(stableZoom, state.ZoomFactor);
    }

    [Fact]
    public void FitWidthAccountsForQuarterTurnRotation()
    {
        var state = CreateOpenState(pageCount: 1);
        state.SetCurrentPageSize(new PdfSize(612, 792));
        state.UpdateViewportSize(width: 408, height: 1000);
        Assert.True(state.FitWidth());
        Assert.Equal(0.5, state.ZoomFactor, precision: 3);

        Assert.True(state.RotateClockwise());

        Assert.Equal(0.386, state.ZoomFactor, precision: 3);
        Assert.Equal(PageRotation.Rotate90, state.Rotation);
    }

    [Fact]
    public void ReadingViewportStateTracksOneVisibleArea()
    {
        var state = new ReadingViewportState();

        Assert.True(state.UpdateViewportSize(width: 800, height: 600));
        Assert.True(state.UpdateScrollOffsets(horizontalOffset: 40, verticalOffset: 120));
        Assert.False(state.UpdateScrollOffsets(horizontalOffset: double.NaN, verticalOffset: 0));
        Assert.False(state.UpdateViewportSize(width: 0, height: 600));

        Assert.Equal(ReadingMode.SinglePage, state.Mode);
        Assert.Equal(new ViewRect(40, 120, 800, 600), state.VisibleArea);

        state.ResetScrollOffsets();

        Assert.Equal(0, state.HorizontalOffset);
        Assert.Equal(0, state.VerticalOffset);
    }

    [Fact]
    public void ReadingViewLayoutUsesPdfLogicalSizeForPageBounds()
    {
        var portrait = ReadingViewLayout.CalculatePageBounds(
            new PdfSize(612, 792),
            PageRotation.Rotate0,
            zoomFactor: 1.0);
        var rotated = ReadingViewLayout.CalculatePageBounds(
            new PdfSize(612, 792),
            PageRotation.Rotate90,
            zoomFactor: 1.0);

        Assert.Equal(816, portrait.Width, precision: 3);
        Assert.Equal(1056, portrait.Height, precision: 3);
        Assert.Equal(1056, rotated.Width, precision: 3);
        Assert.Equal(816, rotated.Height, precision: 3);
    }

    [Fact]
    public void DominantVisiblePageComesFromViewportIntersection()
    {
        var pages = new[]
        {
            new ReadingPageLayout(0, new PdfSize(100, 100), PageRotation.Rotate0, new ViewRect(0, 0, 100, 100)),
            new ReadingPageLayout(1, new PdfSize(100, 100), PageRotation.Rotate0, new ViewRect(0, 120, 100, 100))
        };

        var pageIndex = ReadingViewLayout.GetDominantVisiblePageIndex(
            pages,
            new ViewRect(0, 90, 100, 90),
            fallbackPageIndex: 0);

        Assert.Equal(1, pageIndex);
    }

    [Fact]
    public void PageRenderCacheEvictsTheLeastRecentlyUsedEntry()
    {
        var documentId = new DocumentId(Guid.NewGuid());
        var cache = new PageRenderCache<string>(capacity: 2);
        var first = CreateCacheKey(documentId, pageIndex: 0);
        var second = CreateCacheKey(documentId, pageIndex: 1);
        var third = CreateCacheKey(documentId, pageIndex: 2);
        cache.Set(first, "first");
        cache.Set(second, "second");
        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(first with { DpiScaleX = 1.25 }, out _));

        cache.Set(third, "third");

        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(first, out var firstValue));
        Assert.Equal("first", firstValue);
        Assert.True(cache.TryGet(third, out var thirdValue));
        Assert.Equal("third", thirdValue);
    }

    [Fact]
    public void PageRenderCacheCanClearOneDocument()
    {
        var firstDocumentId = new DocumentId(Guid.NewGuid());
        var secondDocumentId = new DocumentId(Guid.NewGuid());
        var cache = new PageRenderCache<string>(capacity: 3);
        var first = CreateCacheKey(firstDocumentId, pageIndex: 0);
        var second = CreateCacheKey(secondDocumentId, pageIndex: 0);
        cache.Set(first, "first");
        cache.Set(second, "second");

        cache.ClearDocument(firstDocumentId);

        Assert.False(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(second, out var value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void LargeDocumentPageSweepKeepsRenderCacheBounded()
    {
        var documentId = new DocumentId(Guid.NewGuid());
        var cache = new PageRenderCache<string>(capacity: 3);

        for (var pageIndex = 0; pageIndex < 1000; pageIndex++)
        {
            cache.Set(CreateCacheKey(documentId, pageIndex), $"page-{pageIndex}");
        }

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet(CreateCacheKey(documentId, 0), out _));
        Assert.True(cache.TryGet(CreateCacheKey(documentId, 999), out var newest));
        Assert.Equal("page-999", newest);
    }

    [Theory]
    [InlineData(PageRotation.Rotate0)]
    [InlineData(PageRotation.Rotate90)]
    [InlineData(PageRotation.Rotate180)]
    [InlineData(PageRotation.Rotate270)]
    public void CoordinateConversionRoundTripsForEveryRotation(PageRotation rotation)
    {
        var transformer = new CoordinateTransformer();
        var context = new PageTransformContext(
            new PdfSize(600, 800),
            ZoomFactor: 1.5,
            rotation,
            DpiScaleX: 1,
            DpiScaleY: 1,
            PageOffsetX: 12,
            PageOffsetY: 18);
        var pdfPoint = new PdfPoint(125, 640);

        var viewPoint = transformer.PdfToView(pdfPoint, context);
        var result = transformer.ViewToPdf(viewPoint, context);

        Assert.Equal(pdfPoint.X, result.X, precision: 6);
        Assert.Equal(pdfPoint.Y, result.Y, precision: 6);
    }

    [Fact]
    public void TextSelectionMergesAdjacentGlyphsByLine()
    {
        var service = new TextSelectionService();
        var documentId = new DocumentId(Guid.NewGuid());
        var pageText = new PageTextData(documentId, 0, "abc\ndef", new[]
        {
            new TextGlyph(0, "a", new PdfRect(0, 10, 5, 20), 0, 0),
            new TextGlyph(1, "b", new PdfRect(5, 10, 10, 20), 0, 0),
            new TextGlyph(2, "c", new PdfRect(10, 10, 15, 20), 0, 0),
            new TextGlyph(3, "\n", new PdfRect(0, 0, 0, 0), 1, 0),
            new TextGlyph(4, "d", new PdfRect(0, 0, 5, 9), 1, 0),
            new TextGlyph(5, "e", new PdfRect(5, 0, 10, 9), 1, 0),
            new TextGlyph(6, "f", new PdfRect(10, 0, 15, 9), 1, 0)
        });

        var selection = service.CreateSelection(pageText, 0, 6);

        Assert.NotNull(selection);
        Assert.Equal(2, selection.HighlightRectangles.Count);
        Assert.Equal("abc\ndef", selection.RawText);
    }

    [Fact]
    public void TextNormalizationJoinsEnglishLineEndHyphenation()
    {
        var service = new TextSelectionService();

        var normalized = service.NormalizeText("A  well-\nknown method\n\nNext paragraph");

        Assert.Equal("A wellknown method\n\nNext paragraph", normalized);
    }

    [Fact]
    public void TextHitTestingHonorsPdfPointTolerance()
    {
        var service = new TextSelectionService();
        var pageText = new PageTextData(
            new DocumentId(Guid.NewGuid()),
            0,
            "A",
            new[] { new TextGlyph(0, "A", new PdfRect(10, 10, 20, 20), 0, 0) });

        var nearby = service.HitTest(pageText, new PdfPoint(21.5, 15), tolerance: 2);
        var tooFar = service.HitTest(pageText, new PdfPoint(23, 15), tolerance: 2);

        Assert.NotNull(nearby);
        Assert.Null(tooFar);
    }

    private static ReaderState CreateOpenState(int pageCount)
    {
        var state = new ReaderState();
        state.Open(new PdfDocumentInfo(
            new DocumentId(Guid.NewGuid()),
            "sample.pdf",
            pageCount,
            IsEncrypted: false,
            HasTextLayer: true,
            Title: null,
            Author: null));
        return state;
    }

    private static PageRenderCacheKey CreateCacheKey(DocumentId documentId, int pageIndex) => new(
        documentId,
        pageIndex,
        ZoomFactor: 1,
        PageRotation.Rotate0,
        DpiScaleX: 1,
        DpiScaleY: 1,
        RenderQuality.Normal);
}
