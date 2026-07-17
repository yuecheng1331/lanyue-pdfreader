using LocalPdfReader.Domain;

namespace LocalPdfReader.PdfProtocol;

public sealed record PipeMessageEnvelope(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    DocumentId? DocumentId,
    string Payload);
