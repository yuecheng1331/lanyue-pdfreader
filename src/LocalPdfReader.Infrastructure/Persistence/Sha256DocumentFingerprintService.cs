using System.Buffers.Binary;
using System.Security.Cryptography;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Domain;

namespace LocalPdfReader.Infrastructure.Persistence;

public sealed class Sha256DocumentFingerprintService : IDocumentFingerprintService
{
    private const int SampleSize = 64 * 1024;

    public async Task<DocumentFingerprint> ComputeAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(filePath);
        var before = new FileInfo(normalizedPath);
        if (!before.Exists)
        {
            throw new FileNotFoundException("The PDF file does not exist.", normalizedPath);
        }

        var fileSize = before.Length;
        var lastWriteTimeUtc = before.LastWriteTimeUtc;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> metadata = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(metadata[..8], fileSize);
        BinaryPrimitives.WriteInt64LittleEndian(metadata[8..], lastWriteTimeUtc.Ticks);
        hash.AppendData(metadata);

        await using (var stream = new FileStream(
            normalizedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            SampleSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var firstLength = checked((int)Math.Min(SampleSize, fileSize));
            var firstBuffer = new byte[firstLength];
            await stream.ReadExactlyAsync(firstBuffer, cancellationToken);
            hash.AppendData(firstBuffer);

            var lastLength = checked((int)Math.Min(SampleSize, fileSize));
            stream.Seek(Math.Max(0, fileSize - lastLength), SeekOrigin.Begin);
            var lastBuffer = new byte[lastLength];
            await stream.ReadExactlyAsync(lastBuffer, cancellationToken);
            hash.AppendData(lastBuffer);
        }

        var after = new FileInfo(normalizedPath);
        after.Refresh();
        if (!after.Exists || after.Length != fileSize || after.LastWriteTimeUtc != lastWriteTimeUtc)
        {
            throw new IOException("The PDF file changed while its fingerprint was being calculated.");
        }

        return new DocumentFingerprint(
            Convert.ToHexString(hash.GetHashAndReset()),
            fileSize,
            new DateTimeOffset(lastWriteTimeUtc, TimeSpan.Zero));
    }

    public string NormalizePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetFullPath(filePath);
    }

    public bool FileExists(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
}
