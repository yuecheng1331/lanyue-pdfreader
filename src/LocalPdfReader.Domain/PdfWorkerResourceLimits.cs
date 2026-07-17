namespace LocalPdfReader.Domain;

public static class PdfWorkerResourceLimits
{
    public const int MaximumOpenDocuments = 32;
    public const int MaximumUnreleasedSharedBitmaps = 32;
    public const int MaximumRenderedPagePixelsPerSide = 16_384;
    public const long MaximumRenderedPageBytes = 256L * 1024 * 1024;

    public static void ValidateRenderedPage(
        int pixelWidth,
        int pixelHeight,
        int stride,
        long dataLength)
    {
        if (pixelWidth is < 1 or > MaximumRenderedPagePixelsPerSide)
        {
            throw new InvalidDataException(
                $"Rendered page width must be between 1 and {MaximumRenderedPagePixelsPerSide} pixels.");
        }

        if (pixelHeight is < 1 or > MaximumRenderedPagePixelsPerSide)
        {
            throw new InvalidDataException(
                $"Rendered page height must be between 1 and {MaximumRenderedPagePixelsPerSide} pixels.");
        }

        if (stride <= 0)
        {
            throw new InvalidDataException("Rendered page stride must be positive.");
        }

        if (dataLength != (long)stride * pixelHeight)
        {
            throw new InvalidDataException("The rendered page byte length does not match its dimensions.");
        }

        if (dataLength > MaximumRenderedPageBytes)
        {
            throw new InvalidDataException(
                $"Rendered page bitmap cannot exceed {MaximumRenderedPageBytes} bytes.");
        }
    }
}
