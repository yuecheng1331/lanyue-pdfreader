using System.Buffers.Binary;
using System.Text.Json;

namespace LocalPdfReader.PdfProtocol;

public static class PipeMessageSerializer
{
    public const int MaximumControlMessageLength = 4 * 1024 * 1024;
    public const int MaximumPageTextMessageLength = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteAsync(Stream stream, PipeMessageEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);

        var maximumLength = GetMaximumMessageLength(message.MessageType);
        if (payload.Length > maximumLength)
        {
            throw new InvalidDataException($"Messages of type {message.MessageType} cannot exceed {maximumLength} bytes.");
        }

        var lengthPrefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payload.Length);

        await stream.WriteAsync(lengthPrefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public static async Task<PipeMessageEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthPrefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthPrefix, cancellationToken);

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);

        if (payloadLength <= 0 || payloadLength > MaximumPageTextMessageLength)
        {
            throw new InvalidDataException($"Control message length {payloadLength} is outside the allowed range.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken);

        var message = JsonSerializer.Deserialize<PipeMessageEnvelope>(payload, SerializerOptions)
            ?? throw new InvalidDataException("Control message JSON is invalid.");
        if (payloadLength > GetMaximumMessageLength(message.MessageType))
        {
            throw new InvalidDataException($"Messages of type {message.MessageType} cannot exceed {MaximumControlMessageLength} bytes.");
        }

        return message;
    }

    public static string SerializePayload<TPayload>(TPayload payload) => JsonSerializer.Serialize(payload, SerializerOptions);

    public static TPayload DeserializePayload<TPayload>(string payload)
    {
        return JsonSerializer.Deserialize<TPayload>(payload, SerializerOptions)
            ?? throw new InvalidDataException("Message payload JSON is invalid.");
    }

    private static int GetMaximumMessageLength(string messageType) =>
        messageType == PipeMessageTypes.GetPageTextResponse
            ? MaximumPageTextMessageLength
            : MaximumControlMessageLength;
}
