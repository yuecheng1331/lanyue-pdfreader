using System.IO;
using LocalPdfReader.Infrastructure.Persistence;

namespace LocalPdfReader.IntegrationTests;

public sealed class DocumentFingerprintTests
{
    [Fact]
    public async Task MovingAndRenamingAFileKeepsTheSameFingerprint()
    {
        var directoryPath = CreateTemporaryDirectory();
        var originalPath = Path.Combine(directoryPath, "original.pdf");
        var movedDirectory = Path.Combine(directoryPath, "archive");
        var movedPath = Path.Combine(movedDirectory, "renamed.pdf");
        Directory.CreateDirectory(movedDirectory);
        await File.WriteAllBytesAsync(originalPath, CreateDocumentBytes(180_000, 17));
        var service = new Sha256DocumentFingerprintService();

        try
        {
            var original = await service.ComputeAsync(originalPath, CancellationToken.None);
            File.Move(originalPath, movedPath);

            var moved = await service.ComputeAsync(movedPath, CancellationToken.None);

            Assert.Equal(original, moved);
            Assert.Equal(Path.GetFullPath(movedPath), service.NormalizePath(movedPath));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacingContentsAtTheSamePathCreatesANewFingerprint()
    {
        var directoryPath = CreateTemporaryDirectory();
        var filePath = Path.Combine(directoryPath, "replaced.pdf");
        var service = new Sha256DocumentFingerprintService();

        try
        {
            await File.WriteAllBytesAsync(filePath, CreateDocumentBytes(180_000, 23));
            File.SetLastWriteTimeUtc(filePath, new DateTime(2026, 7, 14, 1, 0, 0, DateTimeKind.Utc));
            var original = await service.ComputeAsync(filePath, CancellationToken.None);

            await File.WriteAllBytesAsync(filePath, CreateDocumentBytes(180_000, 91));
            File.SetLastWriteTimeUtc(filePath, new DateTime(2026, 7, 14, 1, 0, 1, DateTimeKind.Utc));
            var replacement = await service.ComputeAsync(filePath, CancellationToken.None);

            Assert.Equal(original.FileSize, replacement.FileSize);
            Assert.NotEqual(original.FastFingerprint, replacement.FastFingerprint);
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static byte[] CreateDocumentBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)(seed + index * 31));
        }

        return bytes;
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.Fingerprint.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }
}
