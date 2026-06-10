using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using OpenAI.Embeddings;
using Azure.Identity;
using System.ClientModel;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.AI;

public sealed class AzureOpenAiEmbeddingService : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly AzureOpenAiOptions _options;
    private readonly ILogger<AzureOpenAiEmbeddingService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public AzureOpenAiEmbeddingService(
        IOptions<AzureOpenAiOptions> options,
        ILogger<AzureOpenAiEmbeddingService> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _options = options.Value;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("AzureOpenAiEmbedPipeline");

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new InvalidOperationException("Azure OpenAI Endpoint is missing.");

        if (string.IsNullOrWhiteSpace(_options.EmbeddingDeploymentName))
            throw new InvalidOperationException("Azure OpenAI Embedding Deployment Name is missing.");

        AzureOpenAIClient openAiClient;

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            openAiClient = new AzureOpenAIClient(new Uri(_options.Endpoint), new ApiKeyCredential(_options.ApiKey));
        }
        else
        {
            openAiClient = new AzureOpenAIClient(new Uri(_options.Endpoint), new DefaultAzureCredential());
        }

        _client = openAiClient.GetEmbeddingClient(_options.EmbeddingDeploymentName);
    }

    public async Task<List<float>> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required for embedding.", nameof(text));

        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "AzureOpenAi.GenerateEmbedding",
            ActivityKind.Internal);
        activity?.SetTag("llm.deployment", _options.EmbeddingDeploymentName);
        activity?.SetTag("text.length", text.Length);

        var stopwatch = Stopwatch.StartNew();
        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await _client.GenerateEmbeddingAsync(text, cancellationToken: token),
            cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "Azure OpenAI embedding completed. DeploymentName={DeploymentName}, DurationMs={DurationMs}, InputLength={InputLength}",
            _options.EmbeddingDeploymentName,
            stopwatch.ElapsedMilliseconds,
            text.Length);

        var embedding = response.Value.ToFloats().ToArray().ToList();

        if (embedding.Count == 0)
            throw new InvalidOperationException("Azure OpenAI returned an empty embedding.");

        long estimatedTokens = text.Length / 4;
        AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.LlmTokenCounter.Add(estimatedTokens,
            new KeyValuePair<string, object?>("model", _options.EmbeddingDeploymentName),
            new KeyValuePair<string, object?>("provider", "AzureOpenAI"),
            new KeyValuePair<string, object?>("type", "embedding"));

        activity?.SetTag("llm.usage.total_tokens", estimatedTokens);

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
