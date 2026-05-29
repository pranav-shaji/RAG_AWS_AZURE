namespace AwsRagChat.Application.Interfaces;

public interface IEmbeddingService
{
    Task<List<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
}