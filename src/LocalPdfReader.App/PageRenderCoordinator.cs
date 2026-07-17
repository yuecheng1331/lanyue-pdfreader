using System.IO;
using System.IO.MemoryMappedFiles;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;

namespace LocalPdfReader.App;

/// <summary>
/// Owns page bitmap caching and the one-time copy from worker-owned shared memory.
/// The reader view model remains responsible for deciding which page to render and
/// for applying the returned bitmap to the active document view.
/// </summary>
internal sealed class PageRenderCoordinator
{
    private const long MaximumCachedBitmapBytes = 64L * 1024 * 1024;
    private readonly PageRenderCache<CachedPageBitmap> _pageCache = new(capacity: 3);

    public bool TryGet(PageRenderCacheKey key, out CachedPageBitmap? page) =>
        _pageCache.TryGet(key, out page);

    public CachedPageBitmap Read(RenderedPageDescriptor descriptor)
    {
        if (descriptor.PixelFormat != "BGRA32")
        {
            throw new InvalidDataException($"Unsupported pixel format {descriptor.PixelFormat}.");
        }

        PdfWorkerResourceLimits.ValidateRenderedPage(
            descriptor.PixelWidth,
            descriptor.PixelHeight,
            descriptor.Stride,
            descriptor.DataLength);

        using var memoryMap = MemoryMappedFile.OpenExisting(descriptor.MemoryMapName, MemoryMappedFileRights.Read);
        using var view = memoryMap.CreateViewAccessor(0, descriptor.DataLength, MemoryMappedFileAccess.Read);
        var pixels = new byte[checked((int)descriptor.DataLength)];
        view.ReadArray(0, pixels, 0, pixels.Length);

        var bitmap = BitmapSource.Create(
            descriptor.PixelWidth,
            descriptor.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            descriptor.Stride);
        bitmap.Freeze();

        return new CachedPageBitmap(bitmap, descriptor.OriginalPageSize);
    }

    public void Cache(PageRenderCacheKey key, CachedPageBitmap page, long sourceByteLength)
    {
        if (sourceByteLength <= MaximumCachedBitmapBytes)
        {
            _pageCache.Set(key, page);
        }
    }

    public void ClearDocument(DocumentId documentId) => _pageCache.ClearDocument(documentId);
}

internal sealed record CachedPageBitmap(BitmapSource Bitmap, PdfSize OriginalPageSize);
