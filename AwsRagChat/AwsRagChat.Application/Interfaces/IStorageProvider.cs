using AwsRagChat.Application.Models;

namespace AwsRagChat.Application.Interfaces;

public interface IStorageProvider : IStorageService
{
    Task<StorageObjectReadResult> OpenReadAsync(
        StorageObjectReadRequest request,
        CancellationToken cancellationToken = default);
}
