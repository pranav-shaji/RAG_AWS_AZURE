using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Ingestion.Options;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Ingestion.Services;

public sealed class EmbeddingBatchService : IEmbeddingProvider
{
    private readonly IAmazonBedrockRuntime _bedrockRuntime;
    private readonly BedrockOptions _bedrockOptions;
    private readonly ResiliencePipeline _resiliencePipeline;

    public EmbeddingBatchService(
        IAmazonBedrockRuntime bedrockRuntime,
        IOptions<BedrockOptions> bedrockOptions,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _bedrockRuntime = bedrockRuntime;
        _bedrockOptions = bedrockOptions.Value;
        _resiliencePipeline = pipelineProvider.GetPipeline("BedrockEmbedPipeline");
    }

    public async Task AddEmbeddingsAsync(
        List<DocumentChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            chunk.Embedding = await GenerateEmbeddingAsync(chunk.Text, cancellationToken);

            if (chunk.Embedding.Count == 0)
                throw new InvalidOperationException($"Empty embedding generated for chunk {chunk.ChunkId}.");
        }
    }

    public async Task<List<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            inputText = text
        };

        var json = JsonSerializer.Serialize(payload);

        var request = new InvokeModelRequest
        {
            ModelId = _bedrockOptions.EmbeddingModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json))
        };

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _bedrockRuntime.InvokeModelAsync(request, token),
            cancellationToken);

        using var reader = new StreamReader(response.Body);
        var responseJson = await reader.ReadToEndAsync(cancellationToken);

        using var document = JsonDocument.Parse(responseJson);

        if (!document.RootElement.TryGetProperty("embedding", out var embeddingElement))
            throw new InvalidOperationException("Embedding response did not contain 'embedding'.");

        var embedding = embeddingElement
            .EnumerateArray()
            .Select(x => x.GetSingle())
            .ToList();

        return NormalizeEmbedding(embedding);
    }

    private static List<float> NormalizeEmbedding(List<float> embedding)
    {
        var magnitude = Math.Sqrt(embedding.Sum(value => value * value));

        if (magnitude <= 0)
            return embedding;

        return embedding
            .Select(value => (float)(value / magnitude))
            .ToList();
    }
}
