using System.Buffers;
using System.Text;

namespace LocalPdfReader.Domain;

public sealed record SearchResult(
    Guid SearchSessionId,
    int ResultIndex,
    int PageIndex,
    int CharacterStart,
    int CharacterCount,
    string MatchedText,
    string ContextText,
    IReadOnlyList<PdfRect> HighlightRectangles);

public readonly record struct SearchTextMatch(
    int CharacterStart,
    int CharacterCount,
    string MatchedText);

public abstract record SearchUpdate(Guid SearchSessionId);

public sealed record SearchStartedUpdate(Guid SessionId, int TotalPages)
    : SearchUpdate(SessionId);

public sealed record SearchProgressUpdate(
    Guid SessionId,
    int PagesSearched,
    int TotalPages,
    int ResultsFound)
    : SearchUpdate(SessionId);

public sealed record SearchResultsUpdate(
    Guid SessionId,
    IReadOnlyList<SearchResult> Results)
    : SearchUpdate(SessionId);

public sealed record SearchCompletedUpdate(Guid SessionId, int TotalResults)
    : SearchUpdate(SessionId);

public sealed record SearchCancelledUpdate(Guid SessionId)
    : SearchUpdate(SessionId);

public static class SearchTextMatcher
{
    public static IReadOnlyList<SearchTextMatch> FindMatches(
        string text,
        string query,
        bool matchCase,
        bool wholeWord)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (text.Length == 0 || query.Length > text.Length)
        {
            return [];
        }

        var comparison = matchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var matches = new List<SearchTextMatch>();
        var searchStart = 0;

        while (searchStart <= text.Length - query.Length)
        {
            var matchStart = text.IndexOf(query, searchStart, comparison);
            if (matchStart < 0)
            {
                break;
            }

            var matchEnd = matchStart + query.Length;
            if (!wholeWord || IsWholeWord(text, matchStart, matchEnd))
            {
                matches.Add(new SearchTextMatch(
                    matchStart,
                    query.Length,
                    text.Substring(matchStart, query.Length)));
            }

            // Search results do not overlap. Advancing by the query length also guarantees progress.
            searchStart = matchEnd;
        }

        return matches;
    }

    private static bool IsWholeWord(string text, int matchStart, int matchEnd) =>
        !IsWordCharacterBefore(text, matchStart) &&
        !IsWordCharacterAt(text, matchEnd);

    private static bool IsWordCharacterBefore(string text, int boundary)
    {
        if (boundary <= 0)
        {
            return false;
        }

        var status = Rune.DecodeLastFromUtf16(text.AsSpan(0, boundary), out var rune, out _);
        return status == OperationStatus.Done && IsWordCharacter(rune);
    }

    private static bool IsWordCharacterAt(string text, int boundary)
    {
        if (boundary >= text.Length)
        {
            return false;
        }

        var status = Rune.DecodeFromUtf16(text.AsSpan(boundary), out var rune, out _);
        return status == OperationStatus.Done && IsWordCharacter(rune);
    }

    private static bool IsWordCharacter(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || rune.Value == '_';
}

public static class PageSearchResultBuilder
{
    private const int ContextRadius = 40;

    public static IReadOnlyList<SearchResult> Build(
        PageTextData pageText,
        Guid searchSessionId,
        string query,
        bool matchCase,
        bool wholeWord,
        int firstResultIndex)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        if (searchSessionId == Guid.Empty)
        {
            throw new ArgumentException("A search session identifier is required.", nameof(searchSessionId));
        }

        if (firstResultIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstResultIndex));
        }

        var textMatches = SearchTextMatcher.FindMatches(pageText.RawText, query, matchCase, wholeWord);
        var results = new List<SearchResult>(textMatches.Count);

        for (var index = 0; index < textMatches.Count; index++)
        {
            var match = textMatches[index];
            results.Add(new SearchResult(
                searchSessionId,
                firstResultIndex + index,
                pageText.PageIndex,
                match.CharacterStart,
                match.CharacterCount,
                match.MatchedText,
                BuildContext(pageText.RawText, match.CharacterStart, match.CharacterCount),
                BuildHighlightRectangles(pageText.Glyphs, match.CharacterStart, match.CharacterCount)));
        }

        return results;
    }

    private static IReadOnlyList<PdfRect> BuildHighlightRectangles(
        IReadOnlyList<TextGlyph> glyphs,
        int matchStart,
        int matchLength)
    {
        var matchEnd = matchStart + matchLength;
        var textOffset = 0;
        var lineRectangles = new List<(int BlockIndex, int LineIndex, PdfRect Bounds)>();

        foreach (var glyph in glyphs)
        {
            var glyphStart = textOffset;
            var glyphEnd = glyphStart + glyph.Text.Length;
            textOffset = glyphEnd;

            if (glyphEnd <= matchStart || glyphStart >= matchEnd || !HasArea(glyph.Bounds))
            {
                continue;
            }

            if (lineRectangles.Count > 0 &&
                lineRectangles[^1].BlockIndex == glyph.BlockIndex &&
                lineRectangles[^1].LineIndex == glyph.LineIndex)
            {
                var current = lineRectangles[^1];
                lineRectangles[^1] = (current.BlockIndex, current.LineIndex, Union(current.Bounds, glyph.Bounds));
            }
            else
            {
                lineRectangles.Add((glyph.BlockIndex, glyph.LineIndex, glyph.Bounds));
            }
        }

        return lineRectangles.Select(item => item.Bounds).ToArray();
    }

    private static string BuildContext(string text, int matchStart, int matchLength)
    {
        var contextStart = Math.Max(0, matchStart - ContextRadius);
        var contextEnd = Math.Min(text.Length, matchStart + matchLength + ContextRadius);
        var context = text[contextStart..contextEnd];
        var builder = new StringBuilder(context.Length);
        var previousWasWhitespace = false;

        foreach (var character in context)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool HasArea(PdfRect rectangle) =>
        rectangle.Right > rectangle.Left && rectangle.Top > rectangle.Bottom;

    private static PdfRect Union(PdfRect first, PdfRect second) => new(
        Math.Min(first.Left, second.Left),
        Math.Min(first.Bottom, second.Bottom),
        Math.Max(first.Right, second.Right),
        Math.Max(first.Top, second.Top));
}
