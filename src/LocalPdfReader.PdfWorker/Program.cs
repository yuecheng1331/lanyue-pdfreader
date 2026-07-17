using System.IO.Pipes;
using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;
using LocalPdfReader.PdfWorker;

var pipeName = GetPipeName(args);

if (pipeName is null)
{
    Console.Error.WriteLine("Missing required --pipe-name argument.");
    return 1;
}

await using var pipe = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    maxNumberOfServerInstances: 1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);

await pipe.WaitForConnectionAsync();
await using var responseWriter = new PipeResponseWriter(pipe);

var handshakeRequest = await PipeMessageSerializer.ReadAsync(pipe, CancellationToken.None);
var isAccepted = handshakeRequest.MessageType == PipeMessageTypes.HandshakeRequest &&
    handshakeRequest.ProtocolVersion == PdfWorkerProtocol.CurrentVersion;
await responseWriter.WriteAsync(handshakeRequest, PipeMessageTypes.HandshakeResponse, null,
    new HandshakeResponse(isAccepted, PdfWorkerProtocol.CurrentVersion));

if (!isAccepted)
{
    return 2;
}

using var documents = new WorkerDocumentService(new PdfiumNativeAdapter());
await using var searches = new WorkerSearchManager(documents, responseWriter);

while (pipe.IsConnected)
{
    PipeMessageEnvelope request;

    try
    {
        request = await PipeMessageSerializer.ReadAsync(pipe, CancellationToken.None);
    }
    catch (EndOfStreamException)
    {
        break;
    }

    try
    {
        switch (request.MessageType)
        {
            case PipeMessageTypes.CancelRequest:
            {
                var cancellationRequest = PipeMessageSerializer.DeserializePayload<CancelRequest>(request.Payload);
                var wasCanceled = searches.Cancel(cancellationRequest.TargetRequestId);
                await responseWriter.WriteAsync(request, PipeMessageTypes.CancelResponse, null,
                    new CancelResponse(cancellationRequest.TargetRequestId, wasCanceled));
                break;
            }

            case PipeMessageTypes.PingRequest:
                await responseWriter.WriteAsync(request, PipeMessageTypes.PingResponse, null, new PingResponse());
                break;

            case PipeMessageTypes.OpenDocumentRequest:
            {
                var openRequest = PipeMessageSerializer.DeserializePayload<OpenDocumentRequest>(request.Payload);
                var info = documents.Open(openRequest.FilePath, openRequest.Password);
                await responseWriter.WriteAsync(request, PipeMessageTypes.OpenDocumentResponse, info.DocumentId,
                    new OpenDocumentResponse(info));
                break;
            }

            case PipeMessageTypes.CloseDocumentRequest:
            {
                var documentId = RequireDocumentId(request);
                await searches.CancelDocumentAsync(documentId);
                documents.Close(documentId);
                await responseWriter.WriteAsync(request, PipeMessageTypes.CloseDocumentResponse, documentId, new CloseDocumentResponse());
                break;
            }

            case PipeMessageTypes.RenderPageRequest:
            {
                var renderRequest = PipeMessageSerializer.DeserializePayload<RenderPageRequest>(request.Payload).RenderRequest;
                if (request.DocumentId != renderRequest.DocumentId)
                {
                    throw new InvalidDataException("The render request document identifier does not match its envelope.");
                }

                var descriptor = documents.Render(renderRequest);
                await responseWriter.WriteAsync(request, PipeMessageTypes.RenderPageResponse, renderRequest.DocumentId,
                    new RenderPageResponse(descriptor));
                break;
            }

            case PipeMessageTypes.GetPageTextRequest:
            {
                var documentId = RequireDocumentId(request);
                var textRequest = PipeMessageSerializer.DeserializePayload<GetPageTextRequest>(request.Payload);
                var pageText = documents.GetPageText(documentId, textRequest.PageIndex);
                await responseWriter.WriteAsync(request, PipeMessageTypes.GetPageTextResponse, documentId,
                    new GetPageTextResponse(pageText));
                break;
            }

            case PipeMessageTypes.HitTestTextRequest:
            {
                var documentId = RequireDocumentId(request);
                var hitRequest = PipeMessageSerializer.DeserializePayload<HitTestTextRequest>(request.Payload);
                var hit = documents.HitTestText(documentId, hitRequest.PageIndex, hitRequest.Point, hitRequest.Tolerance);
                await responseWriter.WriteAsync(request, PipeMessageTypes.HitTestTextResponse, documentId,
                    new HitTestTextResponse(hit));
                break;
            }

            case PipeMessageTypes.GetOutlineRequest:
            {
                var documentId = RequireDocumentId(request);
                var items = documents.GetOutline(documentId);
                await responseWriter.WriteAsync(request, PipeMessageTypes.GetOutlineResponse, documentId,
                    new GetOutlineResponse(items));
                break;
            }

            case PipeMessageTypes.GetPdfAnnotationsRequest:
            {
                var documentId = RequireDocumentId(request);
                var annotations = documents.GetPdfAnnotations(documentId);
                await responseWriter.WriteAsync(request, PipeMessageTypes.GetPdfAnnotationsResponse, documentId,
                    new GetPdfAnnotationsResponse(annotations));
                break;
            }

            case PipeMessageTypes.SavePdfAnnotationsRequest:
            {
                var documentId = RequireDocumentId(request);
                var saveRequest = PipeMessageSerializer.DeserializePayload<SavePdfAnnotationsRequest>(request.Payload);
                var result = documents.SavePdfAnnotations(
                    documentId,
                    saveRequest.Operations,
                    saveRequest.DestinationFilePath,
                    saveRequest.SaveMode);
                await responseWriter.WriteAsync(request, PipeMessageTypes.SavePdfAnnotationsResponse, documentId,
                    new SavePdfAnnotationsResponse(result));
                break;
            }

            case PipeMessageTypes.SearchRequest:
            {
                var documentId = RequireDocumentId(request);
                var searchRequest = PipeMessageSerializer.DeserializePayload<SearchRequest>(request.Payload);
                searches.Start(request, documentId, searchRequest);
                break;
            }

            case PipeMessageTypes.ReleaseSharedMemoryRequest:
            {
                var releaseRequest = PipeMessageSerializer.DeserializePayload<ReleaseSharedMemoryRequest>(request.Payload);
                var wasReleased = documents.ReleaseSharedMemory(releaseRequest.MemoryMapName);
                await responseWriter.WriteAsync(request, PipeMessageTypes.ReleaseSharedMemoryResponse, null,
                    new ReleaseSharedMemoryResponse(wasReleased));
                break;
            }

            default:
                throw new InvalidDataException($"Unsupported PDF worker message type {request.MessageType}.");
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        var errorCode = exception is PdfNativeException nativeException
            ? nativeException.ErrorCode
            : PdfWorkerErrorCodes.RequestFailed;
        await responseWriter.WriteAsync(request, PipeMessageTypes.ErrorResponse, request.DocumentId,
            new ErrorResponse(errorCode, exception.Message));
    }
}

return 0;

static DocumentId RequireDocumentId(PipeMessageEnvelope request) =>
    request.DocumentId ?? throw new InvalidDataException("The request does not include a document identifier.");

static string? GetPipeName(string[] arguments)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], "--pipe-name", StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    return null;
}
