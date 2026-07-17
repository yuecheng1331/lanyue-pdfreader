using LocalPdfReader.Domain;

namespace LocalPdfReader.PdfProtocol.Tests;

public sealed class SearchProtocolTests
{
    [Fact]
    public async Task SearchMessageRoundTripKeepsRequestDocumentAndSessionIdentitiesSeparate()
    {
        var requestId = Guid.NewGuid();
        var documentId = new DocumentId(Guid.NewGuid());
        var request = new SearchRequest(Guid.NewGuid(), "pdf", false, false);
        var message = new PipeMessageEnvelope(
            PdfWorkerProtocol.CurrentVersion,
            PipeMessageTypes.SearchRequest,
            requestId,
            documentId,
            PipeMessageSerializer.SerializePayload(request));
        await using var stream = new MemoryStream();

        await PipeMessageSerializer.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        var result = await PipeMessageSerializer.ReadAsync(stream, CancellationToken.None);
        var resultRequest = PipeMessageSerializer.DeserializePayload<SearchRequest>(result.Payload);

        Assert.Equal(requestId, result.RequestId);
        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(request.SearchSessionId, resultRequest.SearchSessionId);
    }

    [Fact]
    public void SearchRequestPayloadRoundTripPreservesOptions()
    {
        var request = new SearchRequest(
            Guid.NewGuid(),
            "PDF reader",
            MatchCase: true,
            WholeWord: true,
            BatchSize: 40);

        var payload = PipeMessageSerializer.SerializePayload(request);
        var result = PipeMessageSerializer.DeserializePayload<SearchRequest>(payload);

        Assert.Equal(request, result);
    }

    [Fact]
    public void SearchResultBatchPayloadRoundTripPreservesCoordinates()
    {
        var searchSessionId = Guid.NewGuid();
        var response = new SearchResultBatchResponse(
            searchSessionId,
            [
                new SearchResult(
                    searchSessionId,
                    ResultIndex: 3,
                    PageIndex: 2,
                    CharacterStart: 10,
                    CharacterCount: 6,
                    MatchedText: "PDFium",
                    ContextText: "uses PDFium to render",
                    HighlightRectangles: [new PdfRect(1, 2, 3, 4)])
            ]);

        var payload = PipeMessageSerializer.SerializePayload(response);
        var result = PipeMessageSerializer.DeserializePayload<SearchResultBatchResponse>(payload);

        Assert.Equal(searchSessionId, result.SearchSessionId);
        var searchResult = Assert.Single(result.Results);
        var expected = response.Results[0];
        Assert.Equal(expected.SearchSessionId, searchResult.SearchSessionId);
        Assert.Equal(expected.ResultIndex, searchResult.ResultIndex);
        Assert.Equal(expected.PageIndex, searchResult.PageIndex);
        Assert.Equal(expected.CharacterStart, searchResult.CharacterStart);
        Assert.Equal(expected.CharacterCount, searchResult.CharacterCount);
        Assert.Equal(expected.MatchedText, searchResult.MatchedText);
        Assert.Equal(expected.ContextText, searchResult.ContextText);
        Assert.Equal(new PdfRect(1, 2, 3, 4), Assert.Single(searchResult.HighlightRectangles));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SearchProtocolLimits.MaximumBatchSize + 1)]
    public void SearchRequestValidationRejectsInvalidBatchSizes(int batchSize)
    {
        var request = new SearchRequest(Guid.NewGuid(), "pdf", false, false, batchSize);

        Assert.Throws<ArgumentOutOfRangeException>(() => SearchProtocolLimits.Validate(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void SearchRequestValidationRejectsEmptyQueries(string query)
    {
        var request = new SearchRequest(Guid.NewGuid(), query, false, false);

        Assert.Throws<ArgumentException>(() => SearchProtocolLimits.Validate(request));
    }

    [Fact]
    public void SearchRequestValidationAcceptsTheSupportedBoundaries()
    {
        SearchProtocolLimits.Validate(new SearchRequest(Guid.NewGuid(), "pdf", false, false, 1));
        SearchProtocolLimits.Validate(new SearchRequest(
            Guid.NewGuid(),
            "pdf",
            false,
            false,
            SearchProtocolLimits.MaximumBatchSize));
    }

    [Fact]
    public void SearchRequestValidationRejectsAnEmptySessionIdentifier()
    {
        var request = new SearchRequest(Guid.Empty, "pdf", false, false);

        Assert.Throws<ArgumentException>(() => SearchProtocolLimits.Validate(request));
    }
}
