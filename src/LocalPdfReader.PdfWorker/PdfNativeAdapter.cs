using System.Runtime.InteropServices;
using System.Text;
using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;
using PDFiumCore;

namespace LocalPdfReader.PdfWorker;

internal interface IPdfNativeAdapter : IDisposable
{
    NativePdfDocument Open(string filePath, string? password);

    void Close(NativePdfDocument document);

    NativePdfDocumentInfo GetDocumentInfo(NativePdfDocument document, DocumentId documentId, string fileName);

    NativeRenderedPage RenderPage(NativePdfDocument document, PageRenderRequest request);

    PageTextData ExtractPageText(NativePdfDocument document, DocumentId documentId, int pageIndex);

    TextHitTestResult? HitTestText(NativePdfDocument document, int pageIndex, PdfPoint point, double tolerance);

    IReadOnlyList<PdfOutlineItem> GetOutline(NativePdfDocument document);

    IReadOnlyList<PdfStandardAnnotation> GetPdfAnnotations(NativePdfDocument document);

    PdfAnnotationSaveResult SavePdfAnnotations(
        NativePdfDocument document,
        IReadOnlyList<PdfAnnotationWriteOperation> operations,
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode);
}

internal sealed class NativePdfDocument(FpdfDocumentT handle, string filePath)
{
    public FpdfDocumentT Handle { get; } = handle;

    public string FilePath { get; } = filePath;
}

internal sealed record NativePdfDocumentInfo(PdfDocumentInfo DocumentInfo);

internal sealed record NativeRenderedPage(
    byte[] Pixels,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    PdfSize OriginalPageSize);

internal sealed class PdfiumNativeAdapter : IPdfNativeAdapter
{
    private const int FpdfAnnotText = 1;
    private const int FpdfAnnotHighlight = 9;
    private const int FpdfAnnotUnderline = 10;
    private const int FpdfAnnotStrikeOut = 12;
    private const ulong FpdfIncremental = 1;
    private const ulong FpdfNoIncremental = 2;

    private bool _disposed;

    public PdfiumNativeAdapter()
    {
        fpdfview.FPDF_InitLibrary();
    }

    public NativePdfDocument Open(string filePath, string? password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var document = fpdfview.FPDF_LoadDocument(filePath, password);
        if (document == null)
        {
            var lastError = fpdfview.FPDF_GetLastError();
            if (lastError == 4)
            {
                throw new PdfNativeException(
                    PdfWorkerErrorCodes.PasswordRequiredOrInvalid,
                    "The PDF requires a password or the supplied password is incorrect.");
            }

            throw new InvalidOperationException($"PDFium could not open the document (error {lastError}).");
        }

        return new NativePdfDocument(document, filePath);
    }

    public void Close(NativePdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        fpdfview.FPDF_CloseDocument(document.Handle);
    }

    public NativePdfDocumentInfo GetDocumentInfo(NativePdfDocument document, DocumentId documentId, string fileName)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pageCount = fpdfview.FPDF_GetPageCount(document.Handle);
        var hasTextLayer = pageCount > 0 && PageHasText(document.FilePath);
        var securityRevision = fpdfview.FPDF_GetSecurityHandlerRevision(document.Handle);
        return new NativePdfDocumentInfo(new PdfDocumentInfo(
            documentId,
            fileName,
            pageCount,
            IsEncrypted: securityRevision >= 0,
            hasTextLayer,
            Title: GetMetadata(document.Handle, "Title"),
            Author: GetMetadata(document.Handle, "Author")));
    }

    public NativeRenderedPage RenderPage(NativePdfDocument document, PageRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);

        var page = fpdfview.FPDF_LoadPage(document.Handle, request.PageIndex);
        if (page == null)
        {
            throw new InvalidOperationException($"PDFium could not load page {request.PageIndex} (error {fpdfview.FPDF_GetLastError()}).");
        }

        FpdfBitmapT? bitmap = null;
        try
        {
            double pageWidth = 0;
            double pageHeight = 0;
            if (fpdfview.FPDF_GetPageSizeByIndex(document.Handle, request.PageIndex, ref pageWidth, ref pageHeight) == 0)
            {
                throw new InvalidOperationException("PDFium could not obtain the page size.");
            }

            var originalSize = new PdfSize(pageWidth, pageHeight);
            var scale = request.ZoomFactor * 96d / 72d;
            var unrotatedWidth = CheckedPixelLength(pageWidth * scale * request.DpiScaleX);
            var unrotatedHeight = CheckedPixelLength(pageHeight * scale * request.DpiScaleY);
            var isQuarterTurn = request.Rotation is PageRotation.Rotate90 or PageRotation.Rotate270;
            var pixelWidth = isQuarterTurn ? unrotatedHeight : unrotatedWidth;
            var pixelHeight = isQuarterTurn ? unrotatedWidth : unrotatedHeight;
            bitmap = fpdfview.FPDFBitmapCreateEx(pixelWidth, pixelHeight, (int)FPDFBitmapFormat.BGRA, IntPtr.Zero, 0);
            if (bitmap == null)
            {
                throw new InvalidOperationException("PDFium could not allocate a bitmap for the page.");
            }

            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(
                bitmap,
                page,
                0,
                0,
                pixelWidth,
                pixelHeight,
                ToPdfiumRotation(request.Rotation),
                (int)RenderFlags.RenderAnnotations);

            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var pixels = new byte[checked(stride * pixelHeight)];
            Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bitmap), pixels, 0, pixels.Length);
            return new NativeRenderedPage(pixels, pixelWidth, pixelHeight, stride, originalSize);
        }
        finally
        {
            if (bitmap is not null)
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }

            fpdfview.FPDF_ClosePage(page);
        }
    }

    public PageTextData ExtractPageText(NativePdfDocument document, DocumentId documentId, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        var page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
        if (page == null)
        {
            throw new InvalidOperationException($"PDFium could not load page {pageIndex} for text extraction.");
        }

        var textPage = fpdf_text.FPDFTextLoadPage(page);
        if (textPage == null)
        {
            fpdfview.FPDF_ClosePage(page);
            throw new InvalidOperationException("PDFium could not load the page text layer.");
        }

        try
        {
            var characterCount = fpdf_text.FPDFTextCountChars(textPage);
            if (characterCount < 0)
            {
                throw new InvalidOperationException("PDFium could not count page characters.");
            }

            var rawText = new System.Text.StringBuilder(characterCount);
            var glyphs = new List<TextGlyph>(characterCount);
            var lineIndex = 0;
            double? previousLineCenter = null;
            double previousLineHeight = 0;

            for (var index = 0; index < characterCount; index++)
            {
                var text = ToUnicodeText(fpdf_text.FPDFTextGetUnicode(textPage, index));
                if (text.Length == 0)
                {
                    continue;
                }

                rawText.Append(text);
                double left = 0;
                double right = 0;
                double bottom = 0;
                double top = 0;
                var hasBox = fpdf_text.FPDFTextGetCharBox(textPage, index, ref left, ref right, ref bottom, ref top) != 0;
                var bounds = hasBox
                    ? new PdfRect(left, bottom, right, top)
                    : new PdfRect(0, 0, 0, 0);

                if (text is "\r" or "\n")
                {
                    lineIndex++;
                    previousLineCenter = null;
                    previousLineHeight = 0;
                }
                else if (hasBox)
                {
                    var center = (bottom + top) / 2;
                    var height = Math.Max(0.1, top - bottom);
                    if (previousLineCenter is { } previousCenter &&
                        Math.Abs(center - previousCenter) > Math.Max(previousLineHeight, height) * 0.6)
                    {
                        lineIndex++;
                    }

                    previousLineCenter = center;
                    previousLineHeight = height;
                }

                glyphs.Add(new TextGlyph(index, text, bounds, lineIndex, BlockIndex: 0));
            }

            return new PageTextData(documentId, pageIndex, rawText.ToString(), glyphs);
        }
        finally
        {
            // The text page borrows the PDF page; release it first, then release the page handle.
            fpdf_text.FPDFTextClosePage(textPage);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    public TextHitTestResult? HitTestText(
        NativePdfDocument document,
        int pageIndex,
        PdfPoint point,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(document);
        var page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
        if (page == null)
        {
            throw new InvalidOperationException($"PDFium could not load page {pageIndex} for text hit testing.");
        }

        var textPage = fpdf_text.FPDFTextLoadPage(page);
        if (textPage == null)
        {
            fpdfview.FPDF_ClosePage(page);
            throw new InvalidOperationException("PDFium could not load the page text layer.");
        }

        try
        {
            var index = fpdf_text.FPDFTextGetCharIndexAtPos(textPage, point.X, point.Y, tolerance, tolerance);
            if (index < 0)
            {
                return null;
            }

            double left = 0;
            double right = 0;
            double bottom = 0;
            double top = 0;
            if (fpdf_text.FPDFTextGetCharBox(textPage, index, ref left, ref right, ref bottom, ref top) == 0)
            {
                return null;
            }

            return new TextHitTestResult(
                index,
                ToUnicodeText(fpdf_text.FPDFTextGetUnicode(textPage, index)),
                new PdfRect(left, bottom, right, top));
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(textPage);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    public IReadOnlyList<PdfOutlineItem> GetOutline(NativePdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var firstChild = fpdf_doc.FPDFBookmarkGetFirstChild(document.Handle, null);
        return ReadOutlineSiblings(document.Handle, firstChild, depth: 0);
    }

    public IReadOnlyList<PdfStandardAnnotation> GetPdfAnnotations(NativePdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ReadPdfAnnotations(document.Handle);
    }

    public PdfAnnotationSaveResult SavePdfAnnotations(
        NativePdfDocument document,
        IReadOnlyList<PdfAnnotationWriteOperation> operations,
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        foreach (var operation in operations)
        {
            ApplyPdfAnnotationOperation(document.Handle, operation);
        }

        var finalPath = Path.GetFullPath(destinationFilePath);
        var replacesCurrentFile = string.Equals(
            Path.GetFullPath(document.FilePath),
            finalPath,
            StringComparison.OrdinalIgnoreCase);
        var writePath = replacesCurrentFile
            ? Path.Combine(
                Path.GetDirectoryName(finalPath) ?? ".",
                $".localpdfreader-annotations-{Guid.NewGuid():N}.tmp.pdf")
            : finalPath;

        if (!SaveDocument(document.Handle, writePath, saveMode))
        {
            throw new IOException("PDFium could not save the annotated PDF.");
        }

        var verificationDocument = fpdfview.FPDF_LoadDocument(writePath, null);
        if (verificationDocument == null)
        {
            throw new IOException("The saved annotated PDF could not be reopened for verification.");
        }

        IReadOnlyList<PdfStandardAnnotation> verifiedAnnotations;
        try
        {
            verifiedAnnotations = ReadPdfAnnotations(verificationDocument);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(verificationDocument);
        }

        if (replacesCurrentFile)
        {
            try
            {
                File.Copy(writePath, finalPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(writePath))
                {
                    File.Delete(writePath);
                }
            }
        }

        return new PdfAnnotationSaveResult(finalPath, verifiedAnnotations);
    }

    private static string ToUnicodeText(uint codePoint)
    {
        if (codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return string.Empty;
        }

        return char.ConvertFromUtf32((int)codePoint);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        fpdfview.FPDF_DestroyLibrary();
    }

    private static int CheckedPixelLength(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The requested page dimensions are outside the supported range.");
        }

        return checked((int)Math.Ceiling(value));
    }

    private static int ToPdfiumRotation(PageRotation rotation) => rotation switch
    {
        PageRotation.Rotate0 => 0,
        PageRotation.Rotate90 => 1,
        PageRotation.Rotate180 => 2,
        PageRotation.Rotate270 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(rotation))
    };

    private static string? GetMetadata(FpdfDocumentT document, string tag)
    {
        var byteLength = fpdf_doc.FPDF_GetMetaText(document, tag, IntPtr.Zero, 0);
        if (byteLength <= sizeof(char))
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)byteLength));
        try
        {
            _ = fpdf_doc.FPDF_GetMetaText(document, tag, buffer, byteLength);
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<PdfStandardAnnotation> ReadPdfAnnotations(FpdfDocumentT document)
    {
        var annotations = new List<PdfStandardAnnotation>();
        var pageCount = fpdfview.FPDF_GetPageCount(document);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = fpdfview.FPDF_LoadPage(document, pageIndex);
            if (page == null)
            {
                continue;
            }

            try
            {
                var annotationCount = fpdf_annot.FPDFPageGetAnnotCount(page);
                for (var annotationIndex = 0; annotationIndex < annotationCount; annotationIndex++)
                {
                    var annotation = fpdf_annot.FPDFPageGetAnnot(page, annotationIndex);
                    if (annotation == null)
                    {
                        continue;
                    }

                    try
                    {
                        annotations.Add(ReadPdfAnnotation(annotation, pageIndex));
                    }
                    finally
                    {
                        fpdf_annot.FPDFPageCloseAnnot(annotation);
                    }
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }

        return annotations;
    }

    private static PdfStandardAnnotation ReadPdfAnnotation(FpdfAnnotationT annotation, int pageIndex)
    {
        var subtype = fpdf_annot.FPDFAnnotGetSubtype(annotation);
        var type = ToStandardAnnotationType(subtype);
        var rect = new FS_RECTF_();
        var pdfRect = fpdf_annot.FPDFAnnotGetRect(annotation, rect) != 0
            ? new PdfRect(rect.Left, rect.Bottom, rect.Right, rect.Top)
            : new PdfRect(0, 0, 0, 0);
        var quadPoints = new List<PdfQuad>();
        var quadCount = fpdf_annot.FPDFAnnotCountAttachmentPoints(annotation);
        for (ulong quadIndex = 0; quadIndex < quadCount; quadIndex++)
        {
            var quad = new FS_QUADPOINTSF();
            if (fpdf_annot.FPDFAnnotGetAttachmentPoints(annotation, quadIndex, quad) != 0)
            {
                quadPoints.Add(ToPdfQuad(quad));
            }
        }

        return new PdfStandardAnnotation(
            GetStringValue(annotation, "NM"),
            type,
            pageIndex,
            TryGetAnnotationColor(annotation, out var color) ? color : null,
            quadPoints,
            pdfRect,
            GetStringValue(annotation, "Contents"),
            type != PdfStandardAnnotationType.Unsupported);
    }

    private static void ApplyPdfAnnotationOperation(FpdfDocumentT document, PdfAnnotationWriteOperation operation)
    {
        ValidatePdfAnnotationOperation(operation);
        var page = fpdfview.FPDF_LoadPage(document, operation.PageIndex);
        if (page == null)
        {
            throw new InvalidOperationException($"PDFium could not load page {operation.PageIndex} for annotation writing.");
        }

        try
        {
            RemoveAnnotationById(page, operation.PdfAnnotationId);
            if (operation.Kind == PdfAnnotationWriteKind.Delete)
            {
                return;
            }

            CreatePdfAnnotation(page, operation);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static void CreatePdfAnnotation(FpdfPageT page, PdfAnnotationWriteOperation operation)
    {
        var subtype = ToPdfiumAnnotationSubtype(operation.Type);
        var annotation = fpdf_annot.FPDFPageCreateAnnot(page, subtype);
        if (annotation == null)
        {
            throw new InvalidOperationException("PDFium could not create the requested annotation.");
        }

        try
        {
            if (operation.Type is PdfStandardAnnotationType.Highlight or
                PdfStandardAnnotationType.Underline or
                PdfStandardAnnotationType.StrikeOut)
            {
                if (operation.QuadPoints.Count == 0)
                {
                    throw new InvalidOperationException("Text markup annotations require QuadPoints.");
                }

                foreach (var quad in operation.QuadPoints)
                {
                    if (fpdf_annot.FPDFAnnotAppendAttachmentPoints(annotation, ToPdfiumQuad(quad)) == 0)
                    {
                        throw new InvalidOperationException("PDFium could not set annotation QuadPoints.");
                    }
                }
            }

            if (fpdf_annot.FPDFAnnotSetRect(annotation, ToPdfiumRect(operation.Rect)) == 0)
            {
                throw new InvalidOperationException("PDFium could not set the annotation rectangle.");
            }

            var (red, green, blue, alpha) = ToPdfiumColor(operation.Color);
            if (fpdf_annot.FPDFAnnotSetColor(
                annotation,
                FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                red,
                green,
                blue,
                alpha) == 0)
            {
                throw new InvalidOperationException("PDFium could not set the annotation color.");
            }

            SetStringValue(annotation, "NM", operation.PdfAnnotationId);
            if (!string.IsNullOrWhiteSpace(operation.Contents))
            {
                SetStringValue(annotation, "Contents", operation.Contents);
            }
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annotation);
        }
    }

    private static void RemoveAnnotationById(FpdfPageT page, string pdfAnnotationId)
    {
        for (var index = fpdf_annot.FPDFPageGetAnnotCount(page) - 1; index >= 0; index--)
        {
            var annotation = fpdf_annot.FPDFPageGetAnnot(page, index);
            if (annotation == null)
            {
                continue;
            }

            try
            {
                if (string.Equals(GetStringValue(annotation, "NM"), pdfAnnotationId, StringComparison.Ordinal))
                {
                    if (fpdf_annot.FPDFPageRemoveAnnot(page, index) == 0)
                    {
                        throw new InvalidOperationException("PDFium could not remove an existing annotation.");
                    }
                }
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annotation);
            }
        }
    }

    private static bool SaveDocument(FpdfDocumentT document, string destinationFilePath, PdfAnnotationSaveMode saveMode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath) ?? ".");
        using var stream = File.Create(destinationFilePath);
        var writer = new FPDF_FILEWRITE_
        {
            Version = 1,
            WriteBlock = (_, data, size) =>
            {
                var buffer = new byte[checked((int)size)];
                Marshal.Copy(data, buffer, 0, buffer.Length);
                stream.Write(buffer, 0, buffer.Length);
                return 1;
            }
        };

        return saveMode switch
        {
            PdfAnnotationSaveMode.Incremental => fpdf_save.FPDF_SaveAsCopy(document, writer, FpdfIncremental) != 0,
            PdfAnnotationSaveMode.Full => fpdf_save.FPDF_SaveWithVersion(document, writer, FpdfNoIncremental, 14) != 0,
            _ => throw new ArgumentOutOfRangeException(nameof(saveMode))
        };
    }

    private static void ValidatePdfAnnotationOperation(PdfAnnotationWriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(operation.PdfAnnotationId) ||
            operation.PageIndex < 0 ||
            !Enum.IsDefined(operation.Kind) ||
            !Enum.IsDefined(operation.Type) ||
            operation.Type == PdfStandardAnnotationType.Unsupported ||
            !Enum.IsDefined(operation.Color))
        {
            throw new ArgumentException("The PDF annotation operation contains invalid values.", nameof(operation));
        }

        if (operation.Kind == PdfAnnotationWriteKind.CreateOrUpdate &&
            (operation.Rect.Right < operation.Rect.Left || operation.Rect.Top < operation.Rect.Bottom))
        {
            throw new ArgumentException("The PDF annotation rectangle is invalid.", nameof(operation));
        }
    }

    private static PdfStandardAnnotationType ToStandardAnnotationType(int subtype) => subtype switch
    {
        FpdfAnnotHighlight => PdfStandardAnnotationType.Highlight,
        FpdfAnnotUnderline => PdfStandardAnnotationType.Underline,
        FpdfAnnotStrikeOut => PdfStandardAnnotationType.StrikeOut,
        FpdfAnnotText => PdfStandardAnnotationType.Text,
        _ => PdfStandardAnnotationType.Unsupported
    };

    private static int ToPdfiumAnnotationSubtype(PdfStandardAnnotationType type) => type switch
    {
        PdfStandardAnnotationType.Highlight => FpdfAnnotHighlight,
        PdfStandardAnnotationType.Underline => FpdfAnnotUnderline,
        PdfStandardAnnotationType.StrikeOut => FpdfAnnotStrikeOut,
        PdfStandardAnnotationType.Text => FpdfAnnotText,
        _ => throw new ArgumentOutOfRangeException(nameof(type), "Unsupported annotations cannot be written.")
    };

    private static (uint Red, uint Green, uint Blue, uint Alpha) ToPdfiumColor(AnnotationColor color) => color switch
    {
        AnnotationColor.Yellow => (255, 213, 79, 160),
        AnnotationColor.Green => (102, 187, 106, 160),
        AnnotationColor.Blue => (66, 165, 245, 160),
        AnnotationColor.Pink => (236, 124, 181, 160),
        _ => throw new ArgumentOutOfRangeException(nameof(color))
    };

    private static bool TryGetAnnotationColor(FpdfAnnotationT annotation, out AnnotationColor color)
    {
        color = AnnotationColor.Yellow;
        uint red = 0;
        uint green = 0;
        uint blue = 0;
        uint alpha = 0;
        if (fpdf_annot.FPDFAnnotGetColor(
            annotation,
            FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
            ref red,
            ref green,
            ref blue,
            ref alpha) == 0)
        {
            return false;
        }

        color = NearestAnnotationColor(red, green, blue);
        return true;
    }

    private static AnnotationColor NearestAnnotationColor(uint red, uint green, uint blue)
    {
        var candidates = Enum.GetValues<AnnotationColor>();
        return candidates
            .OrderBy(candidate =>
            {
                var (candidateRed, candidateGreen, candidateBlue, _) = ToPdfiumColor(candidate);
                return Math.Pow(red - candidateRed, 2) +
                    Math.Pow(green - candidateGreen, 2) +
                    Math.Pow(blue - candidateBlue, 2);
            })
            .First();
    }

    private static PdfQuad ToPdfQuad(FS_QUADPOINTSF quad) => new(
        quad.X1,
        quad.Y1,
        quad.X2,
        quad.Y2,
        quad.X3,
        quad.Y3,
        quad.X4,
        quad.Y4);

    private static FS_QUADPOINTSF ToPdfiumQuad(PdfQuad quad) => new()
    {
        X1 = CheckedSingle(quad.X1),
        Y1 = CheckedSingle(quad.Y1),
        X2 = CheckedSingle(quad.X2),
        Y2 = CheckedSingle(quad.Y2),
        X3 = CheckedSingle(quad.X3),
        Y3 = CheckedSingle(quad.Y3),
        X4 = CheckedSingle(quad.X4),
        Y4 = CheckedSingle(quad.Y4)
    };

    private static FS_RECTF_ ToPdfiumRect(PdfRect rect) => new()
    {
        Left = CheckedSingle(rect.Left),
        Top = CheckedSingle(rect.Top),
        Right = CheckedSingle(rect.Right),
        Bottom = CheckedSingle(rect.Bottom)
    };

    private static float CheckedSingle(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The PDF coordinate is outside the supported range.");
        }

        return (float)value;
    }

    private static void SetStringValue(FpdfAnnotationT annotation, string key, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        var wide = new ushort[bytes.Length / sizeof(ushort)];
        Buffer.BlockCopy(bytes, 0, wide, 0, bytes.Length);
        if (fpdf_annot.FPDFAnnotSetStringValue(annotation, key, ref wide[0]) == 0)
        {
            throw new InvalidOperationException($"PDFium could not set annotation string value '{key}'.");
        }
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

    private static IReadOnlyList<PdfOutlineItem> ReadOutlineSiblings(
        FpdfDocumentT document,
        FpdfBookmarkT? firstBookmark,
        int depth)
    {
        if (firstBookmark is null || depth > 16)
        {
            return [];
        }

        var items = new List<PdfOutlineItem>();
        var bookmark = firstBookmark;
        while (bookmark is not null)
        {
            var title = GetBookmarkTitle(bookmark);
            var pageIndex = GetBookmarkPageIndex(document, bookmark);
            var firstChild = fpdf_doc.FPDFBookmarkGetFirstChild(document, bookmark);
            var children = ReadOutlineSiblings(document, firstChild, depth + 1);
            if (!string.IsNullOrWhiteSpace(title) || children.Count > 0)
            {
                items.Add(new PdfOutlineItem(
                    string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                    pageIndex >= 0 ? pageIndex : null,
                    children));
            }

            bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(document, bookmark);
        }

        return items;
    }

    private static string GetBookmarkTitle(FpdfBookmarkT bookmark)
    {
        var byteLength = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (byteLength <= sizeof(char))
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)byteLength));
        try
        {
            _ = fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, byteLength);
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetBookmarkPageIndex(FpdfDocumentT document, FpdfBookmarkT bookmark)
    {
        var destination = fpdf_doc.FPDFBookmarkGetDest(document, bookmark);
        if (destination is null)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (action is not null)
            {
                destination = fpdf_doc.FPDFActionGetDest(document, action);
            }
        }

        return destination is null
            ? -1
            : fpdf_doc.FPDFDestGetDestPageIndex(document, destination);
    }

    private static bool PageHasText(string filePath)
    {
        // PDFiumCore's current low-level package does not expose FPDFText_*.
        // For C-stage metadata, conservatively detect an embedded font declaration;
        // full page text extraction is deliberately owned by the later text service.
        var pdfBytes = File.ReadAllBytes(filePath);
        return pdfBytes.AsSpan().IndexOf("/Font"u8) >= 0;
    }
}

internal sealed class PdfNativeException(string errorCode, string userMessage) : InvalidOperationException(userMessage)
{
    public string ErrorCode { get; } = errorCode;
}
