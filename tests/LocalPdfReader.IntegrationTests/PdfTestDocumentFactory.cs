using System.Globalization;
using System.Text;

namespace LocalPdfReader.IntegrationTests;

internal static class PdfTestDocumentFactory
{
    public static byte[] Create(params PdfTestPage[] pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Length == 0)
        {
            throw new ArgumentException("At least one page is required.", nameof(pages));
        }

        var hasText = pages.Any(page => page.Lines.Count > 0);
        var fontObjectNumber = 3 + pages.Length * 2;
        var infoObjectNumber = hasText ? fontObjectNumber + 1 : fontObjectNumber;
        var pageReferences = Enumerable.Range(0, pages.Length)
            .Select(index => $"{3 + index * 2} 0 R");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pageReferences)}] /Count {pages.Length} >>"
        };

        for (var index = 0; index < pages.Length; index++)
        {
            var page = pages[index];
            var contentObjectNumber = 4 + index * 2;
            var content = CreatePageContent(page);
            var resources = hasText
                ? $"<< /Font << /F1 {fontObjectNumber} 0 R >> >>"
                : "<< >>";
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Format(page.Width)} {Format(page.Height)}] " +
                $"/Resources {resources} /Contents {contentObjectNumber} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }

        if (hasText)
        {
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        }
        objects.Add("<< /Title (LocalPdfReader fixed test corpus) /Author (LocalPdfReader tests) >>");
        return WritePdf(objects, infoObjectNumber);
    }

    public static byte[] CreateDamaged() => Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 99 0 R >>\nendobj\n%%EOF\n");

    private static string CreatePageContent(PdfTestPage page)
    {
        if (page.Lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("BT /F1 18 Tf 72 ")
            .Append(Format(Math.Max(36, page.Height - 72)))
            .Append(" Td ");
        for (var index = 0; index < page.Lines.Count; index++)
        {
            if (index > 0)
            {
                builder.Append("0 -24 Td ");
            }

            builder.Append('(').Append(EscapeLiteral(page.Lines[index])).Append(") Tj ");
        }

        builder.Append("ET");
        return builder.ToString();
    }

    private static byte[] WritePdf(IReadOnlyList<string> objects, int infoObjectNumber)
    {
        var builder = new StringBuilder("%PDF-1.4\n%LPR1\n");
        var offsets = new List<int> { 0 };

        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var crossReferenceOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1)
            .Append(" /Root 1 0 R /Info ").Append(infoObjectNumber).Append(" 0 R >>\nstartxref\n")
            .Append(crossReferenceOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string EscapeLiteral(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

internal sealed record PdfTestPage(double Width, double Height, params string[] TextLines)
{
    public IReadOnlyList<string> Lines { get; } = TextLines;
}
