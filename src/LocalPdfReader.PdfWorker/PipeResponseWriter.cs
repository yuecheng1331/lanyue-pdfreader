using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;

namespace LocalPdfReader.PdfWorker;

internal sealed class PipeResponseWriter(Stream stream) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync<TPayload>(
        PipeMessageEnvelope request,
        string messageType,
        DocumentId? documentId,
        TPayload payload)
    {
        var response = new PipeMessageEnvelope(
            PdfWorkerProtocol.CurrentVersion,
            messageType,
            request.RequestId,
            documentId,
            PipeMessageSerializer.SerializePayload(payload));

        await _writeLock.WaitAsync();
        try
        {
            await PipeMessageSerializer.WriteAsync(stream, response, CancellationToken.None);
            await stream.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
