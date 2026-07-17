namespace LocalPdfReader.Application.Configuration;

public interface ICredentialStore
{
    Task SaveSecretAsync(string key, string secret, CancellationToken cancellationToken);

    Task<string?> ReadSecretAsync(string key, CancellationToken cancellationToken);

    Task DeleteSecretAsync(string key, CancellationToken cancellationToken);
}
