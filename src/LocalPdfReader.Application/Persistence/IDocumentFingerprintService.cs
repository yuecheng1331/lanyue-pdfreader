using LocalPdfReader.Domain;

namespace LocalPdfReader.Application.Persistence;

public interface IDocumentFingerprintService
{
    Task<DocumentFingerprint> ComputeAsync(string filePath, CancellationToken cancellationToken);

    string NormalizePath(string filePath);

    bool FileExists(string filePath);
}
