using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;

namespace LocalPdfReader.IntegrationTests;

public sealed class PdfAnnotationTechnologyValidationTests
{
    private const int FpdfAnnotText = 1;
    private const int FpdfAnnotSquare = 5;
    private const int FpdfAnnotHighlight = 9;
    private const int FpdfAnnotUnderline = 10;
    private const int FpdfAnnotStrikeOut = 12;
    private const ulong FpdfIncremental = 1;
    private const ulong FpdfNoIncremental = 2;

    [Fact]
    public void PdfiumCoreExposesAnnotationApisNeededByV1()
    {
        AssertSupported(FpdfAnnotHighlight);
        AssertSupported(FpdfAnnotUnderline);
        AssertSupported(FpdfAnnotStrikeOut);
        AssertSupported(FpdfAnnotText);

        Assert.True(typeof(fpdf_annot).GetMethod(nameof(fpdf_annot.FPDFPageCreateAnnot)) is not null);
        Assert.True(typeof(fpdf_annot).GetMethod(nameof(fpdf_annot.FPDFAnnotAppendAttachmentPoints)) is not null);
        Assert.True(typeof(fpdf_annot).GetMethod(nameof(fpdf_annot.FPDFAnnotSetColor)) is not null);
        Assert.True(typeof(fpdf_annot).GetMethod(nameof(fpdf_annot.FPDFAnnotSetStringValue)) is not null);
        Assert.True(typeof(fpdf_annot).GetMethod(nameof(fpdf_annot.FPDFPageRemoveAnnot)) is not null);
        Assert.True(typeof(fpdf_save).GetMethod(nameof(fpdf_save.FPDF_SaveAsCopy)) is not null);
        Assert.True(typeof(fpdf_save).GetMethod(nameof(fpdf_save.FPDF_SaveWithVersion)) is not null);
    }

    [Fact]
    public void PdfiumCoreCanCreateDeleteSaveAndReloadTargetAnnotations()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.PdfAnnotTech.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var sourcePath = Path.Combine(directoryPath, "source.pdf");
        var incrementalPath = Path.Combine(directoryPath, "incremental.pdf");
        var fullPath = Path.Combine(directoryPath, "full.pdf");

        File.WriteAllBytes(sourcePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Annotation technology validation")));

        try
        {
            SaveWithAnnotations(sourcePath, incrementalPath, FpdfIncremental);
            SaveWithAnnotations(incrementalPath, fullPath, FpdfNoIncremental);
            CopyStageArtifactsIfRequested(incrementalPath, fullPath);

            var incrementalAnnotations = ReadAnnotations(incrementalPath);
            var fullAnnotations = ReadAnnotations(fullPath);

            AssertAnnotationRoundTrip(incrementalAnnotations);
            AssertAnnotationRoundTrip(fullAnnotations);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public void PdfiumNativeAnnotationHandlesCanBeReleasedRepeatedly()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.PdfAnnotResources.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var sourcePath = Path.Combine(directoryPath, "source.pdf");
        var savedPath = Path.Combine(directoryPath, "saved.pdf");

        File.WriteAllBytes(sourcePath, PdfTestDocumentFactory.Create(
            new PdfTestPage(612, 792, "Native resource release validation")));

        try
        {
            SaveWithAnnotations(sourcePath, savedPath, FpdfNoIncremental);

            for (var iteration = 0; iteration < 25; iteration++)
            {
                var annotations = ReadAnnotations(savedPath);
                Assert.Contains(annotations, annotation => annotation.Subtype == FpdfAnnotHighlight);
                Assert.Contains(annotations, annotation => annotation.UniqueName == "lpr-tech-highlight");
            }
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static void SaveWithAnnotations(string sourcePath, string destinationPath, ulong saveFlags)
    {
        using var scope = new PdfiumLibraryScope();
        var document = fpdfview.FPDF_LoadDocument(sourcePath, null);
        Assert.NotNull(document);

        try
        {
            var page = fpdfview.FPDF_LoadPage(document, 0);
            Assert.NotNull(page);

            try
            {
                var existingNames = ReadPageAnnotations(page).Select(annotation => annotation.UniqueName).ToHashSet();
                if (!existingNames.Contains("lpr-tech-highlight"))
                {
                    CreateMarkupAnnotation(
                        page,
                        FpdfAnnotHighlight,
                        "lpr-tech-highlight",
                        "Highlight note from LocalPdfReader technology validation.",
                        CreateQuad(72, 720, 252, 696),
                        (255, 214, 64, 160));
                    CreateMarkupAnnotation(
                        page,
                        FpdfAnnotUnderline,
                        "lpr-tech-underline",
                        "Underline note from LocalPdfReader technology validation.",
                        CreateQuad(72, 684, 252, 660),
                        (42, 125, 225, 255));
                    CreateMarkupAnnotation(
                        page,
                        FpdfAnnotStrikeOut,
                        "lpr-tech-strikeout",
                        "Strikeout note from LocalPdfReader technology validation.",
                        CreateQuad(72, 648, 252, 624),
                        (226, 76, 76, 255));
                    CreateTextAnnotation(
                        page,
                        "lpr-tech-text",
                        "Text note from LocalPdfReader technology validation.",
                        CreateRect(288, 720, 318, 690),
                        (64, 160, 88, 255));

                    CreateTextAnnotation(
                        page,
                        "lpr-tech-delete-me",
                        "This temporary note must be removed before saving.",
                        CreateRect(324, 720, 354, 690),
                        (128, 128, 128, 255));
                    RemoveAnnotationByUniqueName(page, "lpr-tech-delete-me");

                    CreateNonTargetAnnotation(page);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }

            Assert.True(SaveDocument(document, destinationPath, saveFlags));
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static void CreateMarkupAnnotation(
        FpdfPageT page,
        int subtype,
        string uniqueName,
        string contents,
        FS_QUADPOINTSF quadPoints,
        (uint Red, uint Green, uint Blue, uint Alpha) color)
    {
        var annotation = fpdf_annot.FPDFPageCreateAnnot(page, subtype);
        Assert.NotNull(annotation);

        try
        {
            Assert.Equal(subtype, fpdf_annot.FPDFAnnotGetSubtype(annotation));
            Assert.True(fpdf_annot.FPDFAnnotHasAttachmentPoints(annotation) != 0);
            Assert.True(fpdf_annot.FPDFAnnotAppendAttachmentPoints(annotation, quadPoints) != 0);
            Assert.Equal(1UL, fpdf_annot.FPDFAnnotCountAttachmentPoints(annotation));
            Assert.True(fpdf_annot.FPDFAnnotSetRect(annotation, RectFromQuad(quadPoints)) != 0);
            SetCommonAnnotationValues(annotation, uniqueName, contents, color);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annotation);
        }
    }

    private static void CreateTextAnnotation(
        FpdfPageT page,
        string uniqueName,
        string contents,
        FS_RECTF_ rect,
        (uint Red, uint Green, uint Blue, uint Alpha) color)
    {
        var annotation = fpdf_annot.FPDFPageCreateAnnot(page, FpdfAnnotText);
        Assert.NotNull(annotation);

        try
        {
            Assert.Equal(FpdfAnnotText, fpdf_annot.FPDFAnnotGetSubtype(annotation));
            Assert.True(fpdf_annot.FPDFAnnotSetRect(annotation, rect) != 0);
            SetCommonAnnotationValues(annotation, uniqueName, contents, color);
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annotation);
        }
    }

    private static void CreateNonTargetAnnotation(FpdfPageT page)
    {
        var annotation = fpdf_annot.FPDFPageCreateAnnot(page, FpdfAnnotSquare);
        Assert.NotNull(annotation);

        try
        {
            Assert.Equal(FpdfAnnotSquare, fpdf_annot.FPDFAnnotGetSubtype(annotation));
            Assert.True(fpdf_annot.FPDFAnnotSetRect(annotation, CreateRect(384, 720, 456, 648)) != 0);
            SetCommonAnnotationValues(
                annotation,
                "lpr-tech-non-target-square",
                "This non-target annotation must survive LocalPdfReader saves.",
                (120, 72, 170, 255));
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annotation);
        }
    }

    private static void SetCommonAnnotationValues(
        FpdfAnnotationT annotation,
        string uniqueName,
        string contents,
        (uint Red, uint Green, uint Blue, uint Alpha) color)
    {
        Assert.True(fpdf_annot.FPDFAnnotSetColor(
            annotation,
            FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
            color.Red,
            color.Green,
            color.Blue,
            color.Alpha) != 0);
        SetStringValue(annotation, "Contents", contents);
        SetStringValue(annotation, "NM", uniqueName);
    }

    private static void RemoveAnnotationByUniqueName(FpdfPageT page, string uniqueName)
    {
        var annotations = ReadPageAnnotations(page);
        var index = annotations.FindIndex(annotation => annotation.UniqueName == uniqueName);
        Assert.True(index >= 0);
        Assert.True(fpdf_annot.FPDFPageRemoveAnnot(page, index) != 0);
        Assert.DoesNotContain(ReadPageAnnotations(page), annotation => annotation.UniqueName == uniqueName);
    }

    private static List<PdfAnnotationSnapshot> ReadAnnotations(string filePath)
    {
        using var scope = new PdfiumLibraryScope();
        var document = fpdfview.FPDF_LoadDocument(filePath, null);
        Assert.NotNull(document);

        try
        {
            var page = fpdfview.FPDF_LoadPage(document, 0);
            Assert.NotNull(page);

            try
            {
                return ReadPageAnnotations(page);
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static List<PdfAnnotationSnapshot> ReadPageAnnotations(FpdfPageT page)
    {
        var annotations = new List<PdfAnnotationSnapshot>();
        var count = fpdf_annot.FPDFPageGetAnnotCount(page);
        for (var index = 0; index < count; index++)
        {
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, index);
            Assert.NotNull(annotation);

            try
            {
                annotations.Add(ReadAnnotation(annotation));
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annotation);
            }
        }

        return annotations;
    }

    private static PdfAnnotationSnapshot ReadAnnotation(FpdfAnnotationT annotation)
    {
        uint red = 0;
        uint green = 0;
        uint blue = 0;
        uint alpha = 0;
        Assert.True(fpdf_annot.FPDFAnnotGetColor(
            annotation,
            FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
            ref red,
            ref green,
            ref blue,
            ref alpha) != 0);

        FS_QUADPOINTSF? quadPoints = null;
        if (fpdf_annot.FPDFAnnotCountAttachmentPoints(annotation) > 0)
        {
            var readQuadPoints = new FS_QUADPOINTSF();
            Assert.True(fpdf_annot.FPDFAnnotGetAttachmentPoints(annotation, 0, readQuadPoints) != 0);
            quadPoints = readQuadPoints;
        }

        return new PdfAnnotationSnapshot(
            fpdf_annot.FPDFAnnotGetSubtype(annotation),
            GetStringValue(annotation, "NM"),
            GetStringValue(annotation, "Contents"),
            (red, green, blue, alpha),
            quadPoints);
    }

    private static void AssertAnnotationRoundTrip(IReadOnlyList<PdfAnnotationSnapshot> annotations)
    {
        Assert.Contains(annotations, annotation => annotation.Subtype == FpdfAnnotHighlight);
        Assert.Contains(annotations, annotation => annotation.Subtype == FpdfAnnotUnderline);
        Assert.Contains(annotations, annotation => annotation.Subtype == FpdfAnnotStrikeOut);
        Assert.Contains(annotations, annotation => annotation.Subtype == FpdfAnnotText);
        Assert.Contains(annotations, annotation => annotation.Subtype == FpdfAnnotSquare);
        Assert.DoesNotContain(annotations, annotation => annotation.UniqueName == "lpr-tech-delete-me");

        var highlight = Assert.Single(annotations, annotation => annotation.UniqueName == "lpr-tech-highlight");
        Assert.Equal(FpdfAnnotHighlight, highlight.Subtype);
        Assert.Equal("Highlight note from LocalPdfReader technology validation.", highlight.Contents);
        Assert.Equal((255U, 214U, 64U, 160U), highlight.Color);
        Assert.NotNull(highlight.QuadPoints);
        AssertQuadNear(CreateQuad(72, 720, 252, 696), highlight.QuadPoints);

        var underline = Assert.Single(annotations, annotation => annotation.UniqueName == "lpr-tech-underline");
        Assert.Equal(FpdfAnnotUnderline, underline.Subtype);
        Assert.NotNull(underline.QuadPoints);

        var strikeOut = Assert.Single(annotations, annotation => annotation.UniqueName == "lpr-tech-strikeout");
        Assert.Equal(FpdfAnnotStrikeOut, strikeOut.Subtype);
        Assert.NotNull(strikeOut.QuadPoints);

        var text = Assert.Single(annotations, annotation => annotation.UniqueName == "lpr-tech-text");
        Assert.Equal(FpdfAnnotText, text.Subtype);
        Assert.Equal("Text note from LocalPdfReader technology validation.", text.Contents);
        Assert.Equal((64U, 160U, 88U, 255U), text.Color);
        Assert.Null(text.QuadPoints);

        var nonTarget = Assert.Single(annotations, annotation => annotation.UniqueName == "lpr-tech-non-target-square");
        Assert.Equal(FpdfAnnotSquare, nonTarget.Subtype);
        Assert.Equal("This non-target annotation must survive LocalPdfReader saves.", nonTarget.Contents);
    }

    private static bool SaveDocument(FpdfDocumentT document, string destinationPath, ulong flags)
    {
        using var stream = File.Create(destinationPath);
        var writer = new FPDF_FILEWRITE_
        {
            Version = 1,
            WriteBlock = (_, data, size) =>
            {
                var length = checked((int)size);
                var buffer = new byte[length];
                Marshal.Copy(data, buffer, 0, length);
                stream.Write(buffer, 0, buffer.Length);
                return 1;
            }
        };

        return flags == FpdfNoIncremental
            ? fpdf_save.FPDF_SaveWithVersion(document, writer, flags, 14) != 0
            : fpdf_save.FPDF_SaveAsCopy(document, writer, flags) != 0;
    }

    private static void CopyStageArtifactsIfRequested(string incrementalPath, string fullPath)
    {
        var artifactDirectory = Environment.GetEnvironmentVariable("LOCALPDFREADER_STAGE_E_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(artifactDirectory))
        {
            return;
        }

        Directory.CreateDirectory(artifactDirectory);
        File.Copy(incrementalPath, Path.Combine(artifactDirectory, "stage-e-incremental-save.pdf"), overwrite: true);
        File.Copy(fullPath, Path.Combine(artifactDirectory, "stage-e-full-save.pdf"), overwrite: true);
    }

    private static void AssertSupported(int subtype)
    {
        Assert.True(fpdf_annot.FPDFAnnotIsSupportedSubtype(subtype) != 0, $"PDFium subtype {subtype} should be supported.");
    }

    private static void SetStringValue(FpdfAnnotationT annotation, string key, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        var wide = new ushort[bytes.Length / sizeof(ushort)];
        Buffer.BlockCopy(bytes, 0, wide, 0, bytes.Length);
        Assert.True(fpdf_annot.FPDFAnnotSetStringValue(annotation, key, ref wide[0]) != 0);
    }

    private static string GetStringValue(FpdfAnnotationT annotation, string key)
    {
        ushort first = 0;
        var byteLength = fpdf_annot.FPDFAnnotGetStringValue(annotation, key, ref first, 0);
        if (byteLength <= sizeof(ushort))
        {
            return string.Empty;
        }

        var wide = new ushort[checked((int)byteLength / sizeof(ushort))];
        _ = fpdf_annot.FPDFAnnotGetStringValue(annotation, key, ref wide[0], byteLength);
        var bytes = new byte[checked((int)byteLength)];
        Buffer.BlockCopy(wide, 0, bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static FS_QUADPOINTSF CreateQuad(float left, float top, float right, float bottom) => new()
    {
        X1 = left,
        Y1 = top,
        X2 = right,
        Y2 = top,
        X3 = left,
        Y3 = bottom,
        X4 = right,
        Y4 = bottom
    };

    private static FS_RECTF_ CreateRect(float left, float top, float right, float bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom
    };

    private static FS_RECTF_ RectFromQuad(FS_QUADPOINTSF quadPoints) => CreateRect(
        Math.Min(quadPoints.X1, quadPoints.X3),
        Math.Max(quadPoints.Y1, quadPoints.Y2),
        Math.Max(quadPoints.X2, quadPoints.X4),
        Math.Min(quadPoints.Y3, quadPoints.Y4));

    private static void AssertQuadNear(FS_QUADPOINTSF expected, FS_QUADPOINTSF? actual)
    {
        Assert.NotNull(actual);
        AssertNear(expected.X1, actual.X1);
        AssertNear(expected.Y1, actual.Y1);
        AssertNear(expected.X2, actual.X2);
        AssertNear(expected.Y2, actual.Y2);
        AssertNear(expected.X3, actual.X3);
        AssertNear(expected.Y3, actual.Y3);
        AssertNear(expected.X4, actual.X4);
        AssertNear(expected.Y4, actual.Y4);
    }

    private static void AssertNear(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.01f, expected + 0.01f);

    private sealed record PdfAnnotationSnapshot(
        int Subtype,
        string UniqueName,
        string Contents,
        (uint Red, uint Green, uint Blue, uint Alpha) Color,
        FS_QUADPOINTSF? QuadPoints);

    private sealed class PdfiumLibraryScope : IDisposable
    {
        public PdfiumLibraryScope()
        {
            fpdfview.FPDF_InitLibrary();
        }

        public void Dispose()
        {
            fpdfview.FPDF_DestroyLibrary();
        }
    }
}
