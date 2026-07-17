namespace LocalPdfReader.Domain.Tests;

public sealed class SearchTextMatcherTests
{
    [Fact]
    public void FindMatchesUsesCaseInsensitiveComparisonByDefaultOption()
    {
        var matches = SearchTextMatcher.FindMatches("PDF pdf Pdf", "pdf", matchCase: false, wholeWord: false);

        Assert.Equal([0, 4, 8], matches.Select(match => match.CharacterStart));
        Assert.Equal(["PDF", "pdf", "Pdf"], matches.Select(match => match.MatchedText));
    }

    [Fact]
    public void FindMatchesCanRequireMatchingCase()
    {
        var matches = SearchTextMatcher.FindMatches("PDF pdf Pdf", "pdf", matchCase: true, wholeWord: false);

        var match = Assert.Single(matches);
        Assert.Equal(4, match.CharacterStart);
        Assert.Equal(3, match.CharacterCount);
    }

    [Fact]
    public void FindMatchesCanRequireWholeWords()
    {
        var matches = SearchTextMatcher.FindMatches(
            "cat concatenate cat_ cat-cat",
            "cat",
            matchCase: false,
            wholeWord: true);

        Assert.Equal([0, 21, 25], matches.Select(match => match.CharacterStart));
    }

    [Fact]
    public void FindMatchesReturnsNonOverlappingResults()
    {
        var matches = SearchTextMatcher.FindMatches("aaaa", "aa", matchCase: true, wholeWord: false);

        Assert.Equal([0, 2], matches.Select(match => match.CharacterStart));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindMatchesRejectsAnEmptyQuery(string query)
    {
        Assert.Throws<ArgumentException>(() =>
            SearchTextMatcher.FindMatches("text", query, matchCase: false, wholeWord: false));
    }

    [Fact]
    public void PageResultBuilderCreatesContextAndOneRectanglePerMatchedLine()
    {
        var documentId = new DocumentId(Guid.NewGuid());
        var glyphs = new[]
        {
            new TextGlyph(0, "P", new PdfRect(10, 20, 15, 30), 0, 0),
            new TextGlyph(1, "D", new PdfRect(15, 20, 20, 30), 0, 0),
            new TextGlyph(2, "F", new PdfRect(20, 20, 25, 30), 0, 0),
            new TextGlyph(3, "\n", new PdfRect(0, 0, 0, 0), 1, 0),
            new TextGlyph(4, "R", new PdfRect(10, 8, 15, 18), 1, 0)
        };
        var pageText = new PageTextData(documentId, 2, "PDF\nR", glyphs);
        var sessionId = Guid.NewGuid();

        var result = Assert.Single(PageSearchResultBuilder.Build(
            pageText,
            sessionId,
            "PDF\nR",
            matchCase: true,
            wholeWord: false,
            firstResultIndex: 7));

        Assert.Equal(7, result.ResultIndex);
        Assert.Equal(2, result.PageIndex);
        Assert.Equal("PDF R", result.ContextText);
        Assert.Equal(
            [new PdfRect(10, 20, 25, 30), new PdfRect(10, 8, 15, 18)],
            result.HighlightRectangles);
    }
}
