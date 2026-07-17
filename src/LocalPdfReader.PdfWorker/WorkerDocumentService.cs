using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using LocalPdfReader.Domain;

namespace LocalPdfReader.PdfWorker;

internal sealed class WorkerDocumentService(IPdfNativeAdapter nativeAdapter) : IDisposable
{
    private readonly ConcurrentDictionary<DocumentId, DocumentEntry> _documents = new();
    private readonly ConcurrentDictionary<string, MemoryMappedFile> _sharedMemory = new(StringComparer.Ordinal);
    private bool _disposed;

    public PdfDocumentInfo Open(string filePath, string? password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_documents.Count >= PdfWorkerResourceLimits.MaximumOpenDocuments)
        {
            throw new InvalidOperationException(
                $"The PDF worker cannot keep more than {PdfWorkerResourceLimits.MaximumOpenDocuments} documents open.");
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The PDF file does not exist.", filePath);
        }

        var nativeDocument = nativeAdapter.Open(filePath, password);
        var documentId = new DocumentId(Guid.NewGuid());

        try
        {
            var info = nativeAdapter.GetDocumentInfo(nativeDocument, documentId, Path.GetFileName(filePath)).DocumentInfo;
            if (info.PageCount <= 0)
            {
                throw new InvalidDataException("The PDF does not contain any pages.");
            }

            var entry = new DocumentEntry(nativeDocument, info);
            if (!_documents.TryAdd(documentId, entry))
            {
                throw new InvalidOperationException("The PDF document identifier is already in use.");
            }

            return info;
        }
        catch
        {
            nativeAdapter.Close(nativeDocument);
            throw;
        }
    }

    public void Close(DocumentId documentId)
    {
        if (_documents.TryRemove(documentId, out var entry))
        {
            lock (entry.SyncRoot)
            {
                nativeAdapter.Close(entry.NativeDocument);
            }
        }
    }

    public RenderedPageDescriptor Render(PageRenderRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sharedMemory.Count >= PdfWorkerResourceLimits.MaximumUnreleasedSharedBitmaps)
        {
            throw new InvalidOperationException(
                $"The PDF worker has {PdfWorkerResourceLimits.MaximumUnreleasedSharedBitmaps} unreleased rendered pages.");
        }

        if (!_documents.TryGetValue(request.DocumentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        NativeRenderedPage renderedPage;
        lock (entry.SyncRoot)
        {
            renderedPage = nativeAdapter.RenderPage(entry.NativeDocument, request);
        }
        PdfWorkerResourceLimits.ValidateRenderedPage(
            renderedPage.PixelWidth,
            renderedPage.PixelHeight,
            renderedPage.Stride,
            renderedPage.Pixels.LongLength);

        var memoryMapName = $"LocalPdfReader.Bitmap.{Environment.ProcessId}.{request.RequestId:N}";
        var memoryMap = MemoryMappedFile.CreateNew(memoryMapName, renderedPage.Pixels.LongLength, MemoryMappedFileAccess.ReadWrite);

        try
        {
            using (var view = memoryMap.CreateViewAccessor(0, renderedPage.Pixels.LongLength, MemoryMappedFileAccess.Write))
            {
                view.WriteArray(0, renderedPage.Pixels, 0, renderedPage.Pixels.Length);
            }

            if (!_sharedMemory.TryAdd(memoryMapName, memoryMap))
            {
                throw new InvalidOperationException("The rendered page memory map name is already in use.");
            }

            return new RenderedPageDescriptor(
                request.RequestId,
                request.DocumentId,
                request.PageIndex,
                renderedPage.PixelWidth,
                renderedPage.PixelHeight,
                renderedPage.Stride,
                "BGRA32",
                memoryMapName,
                renderedPage.Pixels.LongLength,
                renderedPage.OriginalPageSize);
        }
        catch
        {
            memoryMap.Dispose();
            throw;
        }
    }

    public PageTextData GetPageText(DocumentId documentId, int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        lock (entry.SyncRoot)
        {
            return nativeAdapter.ExtractPageText(entry.NativeDocument, documentId, pageIndex);
        }
    }

    public int GetPageCount(DocumentId documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        return entry.DocumentInfo.PageCount;
    }

    public TextHitTestResult? HitTestText(
        DocumentId documentId,
        int pageIndex,
        PdfPoint point,
        double tolerance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        lock (entry.SyncRoot)
        {
            return nativeAdapter.HitTestText(entry.NativeDocument, pageIndex, point, tolerance);
        }
    }

    public IReadOnlyList<PdfOutlineItem> GetOutline(DocumentId documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        lock (entry.SyncRoot)
        {
            return nativeAdapter.GetOutline(entry.NativeDocument);
        }
    }

    public IReadOnlyList<PdfStandardAnnotation> GetPdfAnnotations(DocumentId documentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        lock (entry.SyncRoot)
        {
            return nativeAdapter.GetPdfAnnotations(entry.NativeDocument);
        }
    }

    public PdfAnnotationSaveResult SavePdfAnnotations(
        DocumentId documentId,
        IReadOnlyList<PdfAnnotationWriteOperation> operations,
        string destinationFilePath,
        PdfAnnotationSaveMode saveMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException("The requested PDF document is not open.");
        }

        lock (entry.SyncRoot)
        {
            return nativeAdapter.SavePdfAnnotations(
                entry.NativeDocument,
                operations,
                destinationFilePath,
                saveMode);
        }
    }

    public bool ReleaseSharedMemory(string memoryMapName)
    {
        if (!_sharedMemory.TryRemove(memoryMapName, out var memoryMap))
        {
            return false;
        }

        memoryMap.Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var documentId in _documents.Keys)
        {
            Close(documentId);
        }

        foreach (var memoryMapName in _sharedMemory.Keys)
        {
            ReleaseSharedMemory(memoryMapName);
        }

        nativeAdapter.Dispose();
    }

    private sealed record DocumentEntry(
        NativePdfDocument NativeDocument,
        PdfDocumentInfo DocumentInfo)
    {
        public object SyncRoot { get; } = new();
    }
}
