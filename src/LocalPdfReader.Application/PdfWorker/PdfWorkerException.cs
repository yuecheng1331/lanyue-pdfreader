namespace LocalPdfReader.Application.PdfWorker;

public sealed class PdfWorkerException(string errorCode, string userMessage)
    : InvalidOperationException($"{errorCode}: {userMessage}")
{
    public string ErrorCode { get; } = errorCode;

    public string UserMessage { get; } = userMessage;
}
