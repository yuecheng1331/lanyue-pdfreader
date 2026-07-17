using System.Collections.Concurrent;
using LocalPdfReader.PdfProtocol;

namespace LocalPdfReader.Infrastructure.PdfWorker;

internal sealed class PendingRequestRegistry
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PipeMessageEnvelope>> _pendingRequests = new();

    public Task<PipeMessageEnvelope> Register(Guid requestId, CancellationToken cancellationToken)
    {
        var completionSource = new TaskCompletionSource<PipeMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(requestId, completionSource))
        {
            throw new InvalidOperationException($"A request with ID {requestId} is already pending.");
        }

        CancellationTokenRegistration cancellationRegistration = default;
        cancellationRegistration = cancellationToken.Register(() =>
        {
            if (_pendingRequests.TryRemove(requestId, out var pendingRequest))
            {
                pendingRequest.TrySetCanceled(cancellationToken);
            }
        });

        _ = completionSource.Task.ContinueWith(
            _ => cancellationRegistration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return completionSource.Task;
    }

    public bool TryComplete(PipeMessageEnvelope response)
    {
        if (!_pendingRequests.TryRemove(response.RequestId, out var pendingRequest))
        {
            return false;
        }

        return pendingRequest.TrySetResult(response);
    }

    public void Fail(Guid requestId, Exception exception)
    {
        if (_pendingRequests.TryRemove(requestId, out var pendingRequest))
        {
            pendingRequest.TrySetException(exception);
        }
    }

    public void FailAll(Exception exception)
    {
        foreach (var requestId in _pendingRequests.Keys)
        {
            Fail(requestId, exception);
        }
    }
}
