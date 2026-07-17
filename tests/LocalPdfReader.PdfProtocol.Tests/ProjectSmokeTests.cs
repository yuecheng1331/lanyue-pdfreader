namespace LocalPdfReader.PdfProtocol.Tests;

public class ProjectSmokeTests
{
    [Fact]
    public void PdfProtocolProjectIsAvailable()
    {
        Assert.True(true);
    }

    [Fact]
    public async Task MessageRoundTripPreservesEnvelopeAndPayload()
    {
        var requestId = Guid.NewGuid();
        var message = new PipeMessageEnvelope(
            PdfWorkerProtocol.CurrentVersion,
            PipeMessageTypes.HandshakeRequest,
            requestId,
            null,
            PipeMessageSerializer.SerializePayload(new HandshakeRequest()));
        await using var stream = new MemoryStream();

        await PipeMessageSerializer.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        var result = await PipeMessageSerializer.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(message, result);
        Assert.Equal(new HandshakeRequest(), PipeMessageSerializer.DeserializePayload<HandshakeRequest>(result.Payload));
    }

    [Fact]
    public async Task MessageWriterRejectsControlMessagesAboveTheControlLimit()
    {
        await using var stream = new MemoryStream();
        var message = new PipeMessageEnvelope(
            PdfWorkerProtocol.CurrentVersion,
            PipeMessageTypes.HandshakeRequest,
            Guid.NewGuid(),
            null,
            new string('x', PipeMessageSerializer.MaximumControlMessageLength));

        await Assert.ThrowsAsync<InvalidDataException>(() => PipeMessageSerializer.WriteAsync(stream, message, CancellationToken.None));
    }

    [Fact]
    public void PageTextPayloadRoundTripPreservesGlyphCoordinates()
    {
        var documentId = new LocalPdfReader.Domain.DocumentId(Guid.NewGuid());
        var pageText = new LocalPdfReader.Domain.PageTextData(
            documentId,
            0,
            "A",
            new[]
            {
                new LocalPdfReader.Domain.TextGlyph(
                    0,
                    "A",
                    new LocalPdfReader.Domain.PdfRect(1, 2, 3, 4),
                    0,
                    0)
            });

        var payload = PipeMessageSerializer.SerializePayload(new GetPageTextResponse(pageText));
        var result = PipeMessageSerializer.DeserializePayload<GetPageTextResponse>(payload);

        Assert.Equal(pageText.DocumentId, result.PageText.DocumentId);
        Assert.Equal(pageText.PageIndex, result.PageText.PageIndex);
        Assert.Equal(pageText.RawText, result.PageText.RawText);
        Assert.Equal(pageText.Glyphs, result.PageText.Glyphs);
    }
}
