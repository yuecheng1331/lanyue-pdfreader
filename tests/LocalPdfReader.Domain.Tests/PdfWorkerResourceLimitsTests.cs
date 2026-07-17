namespace LocalPdfReader.Domain.Tests;

public sealed class PdfWorkerResourceLimitsTests
{
    [Fact]
    public void RenderedPageWithinLimitIsAccepted()
    {
        PdfWorkerResourceLimits.ValidateRenderedPage(
            pixelWidth: 2400,
            pixelHeight: 3200,
            stride: 2400 * 4,
            dataLength: 2400L * 4 * 3200);
    }

    [Fact]
    public void RenderedPageAboveByteLimitIsRejectedBeforeSharedMemoryAllocation()
    {
        var height = (int)(PdfWorkerResourceLimits.MaximumRenderedPageBytes / 4 / 4096) + 1;

        Assert.Throws<InvalidDataException>(() =>
            PdfWorkerResourceLimits.ValidateRenderedPage(
                pixelWidth: 4096,
                pixelHeight: height,
                stride: 4096 * 4,
                dataLength: 4096L * 4 * height));
    }

    [Fact]
    public void RenderedPageWithMismatchedStrideIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            PdfWorkerResourceLimits.ValidateRenderedPage(
                pixelWidth: 100,
                pixelHeight: 100,
                stride: 400,
                dataLength: 39999));
    }
}
