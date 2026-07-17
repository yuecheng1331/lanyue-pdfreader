using System.Text;
using System.Text.RegularExpressions;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Reader;

public sealed partial class TextSelectionService
{
    public TextGlyph? HitTest(PageTextData pageText, PdfPoint point, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        TextGlyph? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var glyph in pageText.Glyphs)
        {
            var bounds = glyph.Bounds;
            if (bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom || char.IsWhiteSpace(glyph.Text, 0))
            {
                continue;
            }

            var deltaX = point.X < bounds.Left
                ? bounds.Left - point.X
                : point.X > bounds.Right ? point.X - bounds.Right : 0;
            var deltaY = point.Y < bounds.Bottom
                ? bounds.Bottom - point.Y
                : point.Y > bounds.Top ? point.Y - bounds.Top : 0;
            var distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (distance < nearestDistance)
            {
                nearest = glyph;
                nearestDistance = distance;
            }
        }

        return nearestDistance <= tolerance ? nearest : null;
    }

    public TextSelection? CreateSelection(
        PageTextData pageText,
        int startCharacterIndex,
        int endCharacterIndex)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        var firstIndex = Math.Min(startCharacterIndex, endCharacterIndex);
        var lastIndex = Math.Max(startCharacterIndex, endCharacterIndex);
        var selectedGlyphs = pageText.Glyphs
            .Where(glyph => glyph.CharacterIndex >= firstIndex && glyph.CharacterIndex <= lastIndex)
            .OrderBy(glyph => glyph.CharacterIndex)
            .ToArray();

        if (selectedGlyphs.Length == 0)
        {
            return null;
        }

        var rawText = string.Concat(selectedGlyphs.Select(glyph => glyph.Text));
        return new TextSelection(
            pageText.DocumentId,
            pageText.PageIndex,
            firstIndex,
            lastIndex,
            rawText,
            NormalizeText(rawText),
            MergeHighlightRectangles(selectedGlyphs));
    }

    public string NormalizeText(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        // PDFium can expose a generated line-end hyphen as U+0002 instead of "-" plus a line break.
        var normalized = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\u0002", "-\n", StringComparison.Ordinal);
        normalized = LineEndHyphenRegex().Replace(normalized, string.Empty);
        normalized = HorizontalWhitespaceRegex().Replace(normalized, " ");
        normalized = SpaceAroundNewlineRegex().Replace(normalized, "\n");
        normalized = ExcessiveNewlineRegex().Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    private static IReadOnlyList<PdfRect> MergeHighlightRectangles(IReadOnlyList<TextGlyph> glyphs)
    {
        var rectangles = new List<PdfRect>();
        PdfRect? current = null;
        var currentLine = -1;
        var widths = new List<double>();

        foreach (var glyph in glyphs)
        {
            var bounds = glyph.Bounds;
            if (bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom || glyph.Text is "\r" or "\n")
            {
                continue;
            }

            var width = bounds.Right - bounds.Left;
            if (current is { } currentRect && glyph.LineIndex == currentLine)
            {
                var averageWidth = widths.Count == 0 ? width : widths.Average();
                var gap = bounds.Left - currentRect.Right;
                if (gap <= averageWidth * 0.75)
                {
                    current = new PdfRect(
                        Math.Min(currentRect.Left, bounds.Left),
                        Math.Min(currentRect.Bottom, bounds.Bottom),
                        Math.Max(currentRect.Right, bounds.Right),
                        Math.Max(currentRect.Top, bounds.Top));
                    widths.Add(width);
                    continue;
                }
            }

            if (current is { } completed)
            {
                rectangles.Add(completed);
            }

            current = bounds;
            currentLine = glyph.LineIndex;
            widths.Clear();
            widths.Add(width);
        }

        if (current is { } last)
        {
            rectangles.Add(last);
        }

        return rectangles;
    }

    [GeneratedRegex(@"(?<=[A-Za-z])-\s*\n\s*(?=[a-z])")]
    private static partial Regex LineEndHyphenRegex();

    [GeneratedRegex(@"[\t\f\v ]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpaceAroundNewlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlineRegex();
}
