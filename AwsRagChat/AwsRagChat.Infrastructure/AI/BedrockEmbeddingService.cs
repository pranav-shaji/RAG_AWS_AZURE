using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.AI;

public sealed class BedrockEmbeddingService : IEmbeddingProvider
{
    private readonly IAmazonBedrockRuntime _bedrockRuntime;
    private readonly BedrockOptions _bedrockOptions;
    private readonly ILogger<BedrockEmbeddingService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public BedrockEmbeddingService(
        IAmazonBedrockRuntime bedrockRuntime,
        IOptions<BedrockOptions> bedrockOptions,
        ILogger<BedrockEmbeddingService> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _bedrockRuntime = bedrockRuntime;
        _bedrockOptions = bedrockOptions.Value;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("BedrockEmbedPipeline");
    }

    public async Task<List<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required for embedding.", nameof(text));

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

        var stopwatch = Stopwatch.StartNew();
        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _bedrockRuntime.InvokeModelAsync(request, token),
            cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "Bedrock embedding completed. ModelId={ModelId}, DurationMs={DurationMs}, InputLength={InputLength}",
            _bedrockOptions.EmbeddingModelId,
            stopwatch.ElapsedMilliseconds,
            text.Length);

        using var reader = new StreamReader(response.Body);
        var responseJson = await reader.ReadToEndAsync(cancellationToken);

        using var document = JsonDocument.Parse(responseJson);

        if (!document.RootElement.TryGetProperty("embedding", out var embeddingElement))
        {
            throw new InvalidOperationException("Bedrock embedding response did not contain 'embedding'.");
        }

        var embedding = embeddingElement
            .EnumerateArray()
            .Select(x => x.GetSingle())
            .ToList();

        if (embedding.Count == 0)
            throw new InvalidOperationException("Bedrock returned an empty embedding.");

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
