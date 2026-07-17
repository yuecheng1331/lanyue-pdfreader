using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.PdfProtocol;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.Infrastructure.PdfWorker;

public sealed class PdfWorkerClient(string executablePath, ILogger<PdfWorkerClient> logger) : IPdfWorkerClient
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    private readonly object _processLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly PendingRequestRegistry _pendingRequests = new();
    private readonly PendingSearchRegistry _pendingSearches = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _receiveLoopCancellationSource;
    private Task? _receiveLoop;
    private int _stopRequested;
    private int _disconnectReported;

    public event EventHandler<PdfWorkerDisconnectedEventArgs>? Disconnected;

    public int? WorkerProcessId
    {
        get
        {
            lock (_processLock)
            {
                return _process is { HasExited: false } process ? process.Id : null;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _stopRequested, 0);
        Interlocked.Exchange(ref _disconnectReported, 0);

        var pipeName = $"LocalPdfReader.Pipe.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var process = StartWorkerProcess(pipeName);
        NamedPipeClientStream? pipe = null;

        try
        {
            using var startupCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCancellationSource.CancelAfter(StartupTimeout);

            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(startupCancellationSource.Token);

            StartReceiveLoop(pipe);
            var responseEnvelope = await SendRequestAsync(
                PipeMessageTypes.HandshakeRequest,
                PipeMessageSerializer.SerializePayload(new HandshakeRequest()),
                documentId: null,
                startupCancellationSource.Token);
            var response = PipeMessageSerializer.DeserializePayload<HandshakeResponse>(responseEnvelope.Payload);

            if (responseEnvelope.MessageType != PipeMessageTypes.HandshakeResponse ||
                !response.IsAccepted ||
                response.WorkerProtocolVersion != PdfWorkerProtocol.CurrentVersion)
            {
                throw new InvalidOperationException($"PDF worker protocol version {response.WorkerProtocolVersion} is not compatible with version {PdfWorkerProtocol.CurrentVersion}.");
            }

            pipe = null;
            logger.LogInformation(new EventId(3001, "PdfWorkerHandshakeSucceeded"), "PDF worker handshake succeeded for process {ProcessId}.", process.Id);
        }
        catch
        {
            pipe?.Dispose();
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        Process? process;
        NamedPipeClientStream? pipe;
        CancellationTokenSource? receiveLoopCancellationSource;
        Task? receiveLoop;

        lock (_processLock)
        {
            process = _process;
            pipe = _pipe;
            receiveLoopCancellationSource = _receiveLoopCancellationSource;
            receiveLoop = _receiveLoop;
            _process = null;
            _pipe = null;
            _receiveLoopCancellationSource = null;
            _receiveLoop = null;
        }

        receiveLoopCancellationSource?.Cancel();
        pipe?.Dispose();
        _pendingRequests.FailAll(new OperationCanceledException("The PDF worker connection was stopped."));
        _pendingSearches.FailAll(new OperationCanceledException("The PDF worker connection was stopped."));

        if (receiveLoop is not null)
        {
            try
            {
                await receiveLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        receiveLoopCancellationSource?.Dispose();

        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= OnWorkerProcessExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }

            logger.LogInformation(new EventId(3002, "PdfWorkerStopped"), "Stopped PDF worker process.");
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task CancelRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.CancelRequest,
            PipeMessageSerializer.SerializePayload(new CancelRequest(requestId)),
            documentId: null,
            cancellationToken);

        if (responseEnvelope.MessageType != PipeMessageTypes.CancelResponse)
        {
            throw new InvalidDataException($"Expected {PipeMessageTypes.CancelResponse} but received {responseEnvelope.MessageType}.");
        }

        var response = PipeMessageSerializer.DeserializePayload<CancelResponse>(responseEnvelope.Payload);

        if (response.TargetRequestId != requestId)
        {
            throw new InvalidDataException("The cancellation response does not match the requested target.");
        }

        logger.LogInformation(
            new EventId(3004, "PdfWorkerCancelAcknowledged"),
            "PDF worker acknowledged cancellation for request {RequestId}; canceled: {WasCanceled}.",
            requestId,
            response.WasCanceled);
    }

    public async Task<Domain.PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.OpenDocumentRequest,
            PipeMessageSerializer.SerializePayload(new OpenDocumentRequest(filePath, password)),
            documentId: null,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.OpenDocumentResponse);
        return PipeMessageSerializer.DeserializePayload<OpenDocumentResponse>(responseEnvelope.Payload).DocumentInfo;
    }

    public async Task CloseDocumentAsync(Domain.DocumentId documentId, CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.CloseDocumentRequest,
            PipeMessageSerializer.SerializePayload(new CloseDocumentRequest()),
            documentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.CloseDocumentResponse);
    }

    public async Task<Domain.RenderedPageDescriptor> RenderPageAsync(Domain.PageRenderRequest request, CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.RenderPageRequest,
            PipeMessageSerializer.SerializePayload(new RenderPageRequest(request)),
            request.DocumentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.RenderPageResponse);
        return PipeMessageSerializer.DeserializePayload<RenderPageResponse>(responseEnvelope.Payload).PageDescriptor;
    }

    public async Task<Domain.PageTextData> GetPageTextAsync(
        Domain.DocumentId documentId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.GetPageTextRequest,
            PipeMessageSerializer.SerializePayload(new GetPageTextRequest(pageIndex)),
            documentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.GetPageTextResponse);
        return PipeMessageSerializer.DeserializePayload<GetPageTextResponse>(responseEnvelope.Payload).PageText;
    }

    public async Task<Domain.TextHitTestResult?> HitTestTextAsync(
        Domain.DocumentId documentId,
        int pageIndex,
        Domain.PdfPoint point,
        double tolerance,
        CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.HitTestTextRequest,
            PipeMessageSerializer.SerializePayload(new HitTestTextRequest(pageIndex, point, tolerance)),
            documentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.HitTestTextResponse);
        return PipeMessageSerializer.DeserializePayload<HitTestTextResponse>(responseEnvelope.Payload).Hit;
    }

    public async Task<IReadOnlyList<Domain.PdfOutlineItem>> GetOutlineAsync(
        Domain.DocumentId documentId,
        CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.GetOutlineRequest,
            PipeMessageSerializer.SerializePayload(new GetOutlineRequest()),
            documentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.GetOutlineResponse);
        return PipeMessageSerializer.DeserializePayload<GetOutlineResponse>(responseEnvelope.Payload).Items;
    }

    public async Task<IReadOnlyList<Domain.PdfStandardAnnotation>> GetPdfAnnotationsAsync(
        Domain.DocumentId documentId,
        CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.GetPdfAnnotationsRequest,
            PipeMessageSerializer.SerializePayload(new GetPdfAnnotationsRequest()),
            documentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.GetPdfAnnotationsResponse);
        return PipeMessageSerializer.DeserializePayload<GetPdfAnnotationsResponse>(responseEnvelope.Payload).Annotations;
    }

    public async Task<Domain.PdfAnnotationSaveResult> SavePdfAnnotationsAsync(
        Domain.DocumentId documentId,
        IReadOnlyList<Domain.PdfAnnotationWriteOperation> operations,
        string destinationFilePath,
        Domain.PdfAnnotationSaveMode saveMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.SavePdfAnnotationsRequest,
            PipeMessageSerializer.SerializePayload(new SavePdfAnnotationsRequest(operations, destinationFilePath, saveMode)),
            documentId,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.SavePdfAnnotationsResponse);
        return PipeMessageSerializer.DeserializePayload<SavePdfAnnotationsResponse>(responseEnvelope.Payload).Result;
    }

    public async IAsyncEnumerable<Domain.SearchUpdate> SearchDocumentAsync(
        Domain.DocumentId documentId,
        Guid searchSessionId,
        string query,
        bool matchCase,
        bool wholeWord,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var searchRequest = new SearchRequest(searchSessionId, query, matchCase, wholeWord, batchSize);
        SearchProtocolLimits.Validate(searchRequest);
        var requestId = Guid.NewGuid();
        var responses = _pendingSearches.Register(requestId);
        var requestEnvelope = new PipeMessageEnvelope(
            PdfWorkerProtocol.CurrentVersion,
            PipeMessageTypes.SearchRequest,
            requestId,
            documentId,
            PipeMessageSerializer.SerializePayload(searchRequest));
        var receivedTerminalResponse = false;

        try
        {
            try
            {
                await WriteRequestAsync(requestEnvelope, cancellationToken);
            }
            catch (Exception exception)
            {
                _pendingSearches.Fail(requestId, exception);
                throw;
            }

            await foreach (var response in responses.ReadAllAsync(cancellationToken))
            {
                EnsureNotError(response);

                switch (response.MessageType)
                {
                    case PipeMessageTypes.SearchStartedResponse:
                    {
                        var payload = PipeMessageSerializer.DeserializePayload<SearchStartedResponse>(response.Payload);
                        EnsureSearchSession(searchSessionId, payload.SearchSessionId);
                        yield return new Domain.SearchStartedUpdate(payload.SearchSessionId, payload.TotalPages);
                        break;
                    }

                    case PipeMessageTypes.SearchProgressResponse:
                    {
                        var payload = PipeMessageSerializer.DeserializePayload<SearchProgressResponse>(response.Payload);
                        EnsureSearchSession(searchSessionId, payload.SearchSessionId);
                        yield return new Domain.SearchProgressUpdate(
                            payload.SearchSessionId,
                            payload.PagesSearched,
                            payload.TotalPages,
                            payload.ResultsFound);
                        break;
                    }

                    case PipeMessageTypes.SearchResultBatchResponse:
                    {
                        var payload = PipeMessageSerializer.DeserializePayload<SearchResultBatchResponse>(response.Payload);
                        EnsureSearchSession(searchSessionId, payload.SearchSessionId);
                        yield return new Domain.SearchResultsUpdate(payload.SearchSessionId, payload.Results);
                        break;
                    }

                    case PipeMessageTypes.SearchCompletedResponse:
                    {
                        var payload = PipeMessageSerializer.DeserializePayload<SearchCompletedResponse>(response.Payload);
                        EnsureSearchSession(searchSessionId, payload.SearchSessionId);
                        receivedTerminalResponse = true;
                        yield return new Domain.SearchCompletedUpdate(payload.SearchSessionId, payload.TotalResults);
                        break;
                    }

                    case PipeMessageTypes.SearchCancelledResponse:
                    {
                        var payload = PipeMessageSerializer.DeserializePayload<SearchCancelledResponse>(response.Payload);
                        EnsureSearchSession(searchSessionId, payload.SearchSessionId);
                        receivedTerminalResponse = true;
                        yield return new Domain.SearchCancelledUpdate(payload.SearchSessionId);
                        break;
                    }

                    case PipeMessageTypes.SearchFailedResponse:
                    {
                        var payload = PipeMessageSerializer.DeserializePayload<SearchFailedResponse>(response.Payload);
                        EnsureSearchSession(searchSessionId, payload.SearchSessionId);
                        receivedTerminalResponse = true;
                        throw new InvalidOperationException($"{payload.ErrorCode}: {payload.UserMessage}");
                    }

                    default:
                        throw new InvalidDataException($"Unexpected PDF search response type {response.MessageType}.");
                }
            }
        }
        finally
        {
            _pendingSearches.Remove(requestId);
            if (!receivedTerminalResponse)
            {
                try
                {
                    await CancelRequestAsync(requestId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        new EventId(3006, "PdfWorkerSearchCancelFailed"),
                        exception,
                        "Could not send cancellation for PDF search request {RequestId}.",
                        requestId);
                }
            }
        }
    }

    public async Task ReleaseSharedMemoryAsync(string memoryMapName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryMapName);

        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.ReleaseSharedMemoryRequest,
            PipeMessageSerializer.SerializePayload(new ReleaseSharedMemoryRequest(memoryMapName)),
            documentId: null,
            cancellationToken);

        EnsureNotError(responseEnvelope);
        EnsureMessageType(responseEnvelope, PipeMessageTypes.ReleaseSharedMemoryResponse);
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        var responseEnvelope = await SendRequestAsync(
            PipeMessageTypes.PingRequest,
            PipeMessageSerializer.SerializePayload(new PingRequest()),
            documentId: null,
            cancellationToken);

        if (responseEnvelope.MessageType != PipeMessageTypes.PingResponse)
        {
            throw new InvalidDataException($"Expected {PipeMessageTypes.PingResponse} but received {responseEnvelope.MessageType}.");
        }

        _ = PipeMessageSerializer.DeserializePayload<PingResponse>(responseEnvelope.Payload);
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private void StartReceiveLoop(NamedPipeClientStream pipe)
    {
        lock (_processLock)
        {
            _pipe = pipe;
            _receiveLoopCancellationSource = new CancellationTokenSource();
            _receiveLoop = ReceiveLoopAsync(pipe, _receiveLoopCancellationSource.Token);
        }
    }

    private async Task<PipeMessageEnvelope> SendRequestAsync(
        string messageType,
        string payload,
        Domain.DocumentId? documentId,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var responseTask = _pendingRequests.Register(requestId, cancellationToken);
        var request = new PipeMessageEnvelope(
            PdfWorkerProtocol.CurrentVersion,
            messageType,
            requestId,
            documentId,
            payload);

        try
        {
            await WriteRequestAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            _pendingRequests.Fail(requestId, exception);
            throw;
        }

        return await responseTask;
    }

    private async Task WriteRequestAsync(
        PipeMessageEnvelope request,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var pipe = GetConnectedPipe();
            await PipeMessageSerializer.WriteAsync(pipe, request, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void EnsureMessageType(PipeMessageEnvelope responseEnvelope, string expectedMessageType)
    {
        if (responseEnvelope.MessageType != expectedMessageType)
        {
            throw new InvalidDataException($"Expected {expectedMessageType} but received {responseEnvelope.MessageType}.");
        }
    }

    private static void EnsureNotError(PipeMessageEnvelope responseEnvelope)
    {
        if (responseEnvelope.MessageType != PipeMessageTypes.ErrorResponse)
        {
            return;
        }

        var error = PipeMessageSerializer.DeserializePayload<ErrorResponse>(responseEnvelope.Payload);
        throw new PdfWorkerException(error.ErrorCode, error.UserMessage);
    }

    private async Task ReceiveLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var response = await PipeMessageSerializer.ReadAsync(pipe, cancellationToken);

                if (!_pendingRequests.TryComplete(response) &&
                    !_pendingSearches.TryPublish(response))
                {
                    logger.LogWarning(new EventId(3003, "PdfWorkerResponseIgnored"), "Ignored response with unknown request ID {RequestId}.", response.RequestId);
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _pendingRequests.FailAll(exception);
            logger.LogError(exception, "The PDF worker response loop stopped unexpectedly.");
            ReportDisconnected(
                PdfWorkerDisconnectReason.PipeDisconnected,
                exitCode: null,
                exception);
        }
    }

    private static void EnsureSearchSession(Guid expectedSessionId, Guid actualSessionId)
    {
        if (expectedSessionId != actualSessionId)
        {
            throw new InvalidDataException("The PDF search response belongs to a different search session.");
        }
    }

    private NamedPipeClientStream GetConnectedPipe()
    {
        lock (_processLock)
        {
            return _pipe ?? throw new InvalidOperationException("The PDF worker pipe is not connected.");
        }
    }

    private Process StartWorkerProcess(string pipeName)
    {
        lock (_processLock)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("The PDF worker is already running.");
            }

            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("The PDF worker executable was not found.", executablePath);
            }

            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(pipeName);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The PDF worker process could not be started.");

            _process.EnableRaisingEvents = true;
            _process.Exited += OnWorkerProcessExited;

            logger.LogInformation(new EventId(3000, "PdfWorkerStarted"), "Started PDF worker process {ProcessId}.", _process.Id);
            return _process;
        }
    }

    private void OnWorkerProcessExited(object? sender, EventArgs e)
    {
        var process = (Process?)sender;
        int? exitCode = null;

        try
        {
            exitCode = process?.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        ReportDisconnected(PdfWorkerDisconnectReason.ProcessExited, exitCode, exception: null);
    }

    private void ReportDisconnected(
        PdfWorkerDisconnectReason reason,
        int? exitCode,
        Exception? exception)
    {
        if (Volatile.Read(ref _stopRequested) != 0 ||
            Interlocked.Exchange(ref _disconnectReported, 1) != 0)
        {
            return;
        }

        logger.LogError(
            new EventId(3005, "PdfWorkerDisconnected"),
            exception,
            "PDF worker disconnected unexpectedly. Reason: {Reason}; exit code: {ExitCode}.",
            reason,
            exitCode);
        Disconnected?.Invoke(this, new PdfWorkerDisconnectedEventArgs(reason, exitCode, exception));
    }
}
