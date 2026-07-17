using System.Collections.Concurrent;
using System.Threading.Channels;
using LocalPdfReader.PdfProtocol;

namespace LocalPdfReader.Infrastructure.PdfWorker;

internal sealed class PendingSearchRegistry
{
    private readonly ConcurrentDictionary<Guid, Channel<PipeMessageEnvelope>> _pendingSearches = new();

    public ChannelReader<PipeMessageEnvelope> Register(Guid requestId)
    {
        var channel = Channel.CreateUnbounded<PipeMessageEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        if (!_pendingSearches.TryAdd(requestId, channel))
        {
            throw new InvalidOperationException($"A search with request ID {requestId} is already pending.");
        }

        return channel.Reader;
    }

    public bool TryPublish(PipeMessageEnvelope response)
    {
        if (!_pendingSearches.TryGetValue(response.RequestId, out var channel))
        {
            return false;
        }

        if (!channel.Writer.TryWrite(response))
        {
            return false;
        }

        if (IsTerminal(response.MessageType) &&
            _pendingSearches.TryRemove(response.RequestId, out var completedChannel))
        {
            completedChannel.Writer.TryComplete();
        }

        return true;
    }

    public void Remove(Guid requestId)
    {
        if (_pendingSearches.TryRemove(requestId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Fail(Guid requestId, Exception exception)
    {
        if (_pendingSearches.TryRemove(requestId, out var channel))
        {
            channel.Writer.TryComplete(exception);
        }
    }

    public void FailAll(Exception exception)
    {
        foreach (var requestId in _pendingSearches.Keys)
        {
            Fail(requestId, exception);
        }
    }

    private static bool IsTerminal(string messageType) =>
        messageType is
            PipeMessageTypes.SearchCompletedResponse or
            PipeMessageTypes.SearchCancelledResponse or
            PipeMessageTypes.SearchFailedResponse or
            PipeMessageTypes.ErrorResponse;
}
