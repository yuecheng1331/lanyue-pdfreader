namespace LocalPdfReader.Application.Persistence;

public interface ILocalDataStore
{
    bool IsAvailable { get; }

    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
}

public sealed class LocalDataStoreUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
