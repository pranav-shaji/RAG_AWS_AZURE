namespace AwsRagChat.Application.Interfaces;

public interface IStorageService
{
    Task<string> UploadAsync(Stream stream, string key, CancellationToken cancellationToken = default);

    Task<string> CreateReadUrlAsync(string key, TimeSpan expiresIn, CancellationToken cancellationToken = default);
}
