using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Reader;

public interface ICoordinateTransformer
{
    ViewPoint PdfToView(PdfPoint pdfPoint, PageTransformContext context);

    PdfPoint ViewToPdf(ViewPoint viewPoint, PageTransformContext context);

    ViewRect PdfToView(PdfRect pdfRect, PageTransformContext context);
}

public sealed class CoordinateTransformer : ICoordinateTransformer
{
    private const double PdfPointToDeviceIndependentPixel = 96d / 72d;

    public ViewPoint PdfToView(PdfPoint pdfPoint, PageTransformContext context)
    {
        var width = context.PdfPageSize.Width;
        var height = context.PdfPageSize.Height;
        var unrotatedX = pdfPoint.X;
        var unrotatedY = height - pdfPoint.Y;
        var rotated = context.Rotation switch
        {
            PageRotation.Rotate0 => new ViewPoint(unrotatedX, unrotatedY),
            PageRotation.Rotate90 => new ViewPoint(height - unrotatedY, unrotatedX),
            PageRotation.Rotate180 => new ViewPoint(width - unrotatedX, height - unrotatedY),
            PageRotation.Rotate270 => new ViewPoint(unrotatedY, width - unrotatedX),
            _ => throw new ArgumentOutOfRangeException(nameof(context), "Unsupported page rotation.")
        };

        return new ViewPoint(
            context.PageOffsetX + rotated.X * ScaleX(context),
            context.PageOffsetY + rotated.Y * ScaleY(context));
    }

    public PdfPoint ViewToPdf(ViewPoint viewPoint, PageTransformContext context)
    {
        var width = context.PdfPageSize.Width;
        var height = context.PdfPageSize.Height;
        var rotatedX = (viewPoint.X - context.PageOffsetX) / ScaleX(context);
        var rotatedY = (viewPoint.Y - context.PageOffsetY) / ScaleY(context);

        return context.Rotation switch
        {
            PageRotation.Rotate0 => new PdfPoint(rotatedX, height - rotatedY),
            PageRotation.Rotate90 => new PdfPoint(rotatedY, rotatedX),
            PageRotation.Rotate180 => new PdfPoint(width - rotatedX, rotatedY),
            PageRotation.Rotate270 => new PdfPoint(width - rotatedY, height - rotatedX),
            _ => throw new ArgumentOutOfRangeException(nameof(context), "Unsupported page rotation.")
        };
    }

    public ViewRect PdfToView(PdfRect pdfRect, PageTransformContext context)
    {
        var corners = new[]
        {
            PdfToView(new PdfPoint(pdfRect.Left, pdfRect.Bottom), context),
            PdfToView(new PdfPoint(pdfRect.Left, pdfRect.Top), context),
            PdfToView(new PdfPoint(pdfRect.Right, pdfRect.Bottom), context),
            PdfToView(new PdfPoint(pdfRect.Right, pdfRect.Top), context)
        };
        var left = corners.Min(point => point.X);
        var top = corners.Min(point => point.Y);
        var right = corners.Max(point => point.X);
        var bottom = corners.Max(point => point.Y);
        return new ViewRect(left, top, right - left, bottom - top);
    }

    private static double ScaleX(PageTransformContext context) =>
        context.ZoomFactor * PdfPointToDeviceIndependentPixel * context.DpiScaleX;

    private static double ScaleY(PageTransformContext context) =>
        context.ZoomFactor * PdfPointToDeviceIndependentPixel * context.DpiScaleY;
}
