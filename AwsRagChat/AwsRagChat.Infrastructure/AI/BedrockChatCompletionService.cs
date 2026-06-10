using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Services;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.AI;

public sealed class BedrockChatCompletionService : IChatProvider
{
    private readonly IAmazonBedrockRuntime _bedrockRuntime;
    private readonly BedrockOptions _bedrockOptions;
    private readonly ILogger<BedrockChatCompletionService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;

    public BedrockChatCompletionService(
        IAmazonBedrockRuntime bedrockRuntime,
        IOptions<BedrockOptions> bedrockOptions,
        ILogger<BedrockChatCompletionService> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _bedrockRuntime = bedrockRuntime;
        _bedrockOptions = bedrockOptions.Value;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("BedrockChatPipeline");
    }

    public async Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<DocumentChunk> relevantChunks,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat,
        CancellationToken cancellationToken = default)
    {
        var prompt = PromptBuilder.BuildGroundedPrompt(
            question,
            relevantChunks,
            conversationHistory,
            conversationSummary,
            outputFormat);

        var responseJson = await InvokeNovaAsync(
            prompt,
            "GroundedAnswer",
            cancellationToken,
            ("ChunkCount", relevantChunks.Count.ToString()),
            ("OutputFormat", outputFormat));

        using var document = JsonDocument.Parse(responseJson);

        if (TryReadNovaText(document.RootElement, out var answer))
        {
            var trimmedAnswer = answer.Trim();

            return string.IsNullOrWhiteSpace(trimmedAnswer)
                ? "I do not know based on the uploaded documents."
                : trimmedAnswer;
        }

        throw new InvalidOperationException("Could not parse Bedrock chat response.");
    }

    public async Task<string> GenerateGeneralAnswerAsync(
        string question,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat,
        CancellationToken cancellationToken = default)
    {
        var prompt = PromptBuilder.BuildGeneralAssistantPrompt(
            question,
            conversationHistory,
            conversationSummary,
            outputFormat);

        var responseJson = await InvokeNovaAsync(
            prompt,
            "GeneralAnswer",
            cancellationToken,
            ("OutputFormat", outputFormat));

        using var document = JsonDocument.Parse(responseJson);

        if (TryReadNovaText(document.RootElement, out var answer))
        {
            var trimmedAnswer = answer.Trim();

            return string.IsNullOrWhiteSpace(trimmedAnswer)
                ? "I could not generate a useful answer for that."
                : trimmedAnswer;
        }

        throw new InvalidOperationException("Could not parse Bedrock chat response.");
    }

    public async Task<string> GenerateKnowledgeOverviewAsync(
        IReadOnlyList<string> knowledgeSignals,
        CancellationToken cancellationToken = default)
    {
        var prompt = PromptBuilder.BuildKnowledgeOverviewPrompt(
            knowledgeSignals);

        var responseJson = await InvokeNovaAsync(
            prompt,
            "KnowledgeOverview",
            cancellationToken,
            ("SignalCount", knowledgeSignals.Count.ToString()));

        using var document = JsonDocument.Parse(responseJson);

        if (TryReadNovaText(document.RootElement, out var answer))
        {
            var trimmedAnswer = answer.Trim();

            return string.IsNullOrWhiteSpace(trimmedAnswer)
                ? "No enterprise knowledge is available yet. Please contact an admin to upload and index documents."
                : trimmedAnswer;
        }

        throw new InvalidOperationException("Could not parse Bedrock chat response.");
    }

    private async Task<string> InvokeNovaAsync(
        string prompt,
        string operation,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] dimensions)
    {
        using var activity = AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.Source.StartActivity(
            "Bedrock.CompleteChat",
            ActivityKind.Internal);

        activity?.SetTag("llm.model", _bedrockOptions.ChatModelId);
        activity?.SetTag("llm.operation", operation);

        var payload = new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            text = prompt
                        }
                    }
                }
            },
            inferenceConfig = new
            {
                max_new_tokens = operation == "KnowledgeOverview" ? 700 : 4096,
                temperature = operation == "GeneralAnswer" ? 0.2 : 0.1
            }
        };

        var json = JsonSerializer.Serialize(payload);

        var request = new InvokeModelRequest
        {
            ModelId = _bedrockOptions.ChatModelId,
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
            "Bedrock chat completed. ModelId={ModelId}, Operation={Operation}, DurationMs={DurationMs}, PromptLength={PromptLength}, Details={Details}",
            _bedrockOptions.ChatModelId,
            operation,
            stopwatch.ElapsedMilliseconds,
            prompt.Length,
            string.Join(",", dimensions.Select(dimension => $"{dimension.Name}={dimension.Value}")));

        using var reader = new StreamReader(response.Body);
        var responseJson = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("usage", out var usageElement))
            {
                var inputTokens = usageElement.TryGetProperty("inputTokens", out var inputVal) ? inputVal.GetInt64() : 0;
                var outputTokens = usageElement.TryGetProperty("outputTokens", out var outputVal) ? outputVal.GetInt64() : 0;
                var totalTokens = usageElement.TryGetProperty("totalTokens", out var totalVal) ? totalVal.GetInt64() : 0;

                activity?.SetTag("llm.usage.input_tokens", inputTokens);
                activity?.SetTag("llm.usage.output_tokens", outputTokens);
                activity?.SetTag("llm.usage.total_tokens", totalTokens);

                AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.LlmTokenCounter.Add(inputTokens,
                    new KeyValuePair<string, object?>("model", _bedrockOptions.ChatModelId),
                    new KeyValuePair<string, object?>("provider", "AWSBedrock"),
                    new KeyValuePair<string, object?>("type", "input"));

                AwsRagChat.Infrastructure.Telemetry.ApplicationTelemetry.LlmTokenCounter.Add(outputTokens,
                    new KeyValuePair<string, object?>("model", _bedrockOptions.ChatModelId),
                    new KeyValuePair<string, object?>("provider", "AWSBedrock"),
                    new KeyValuePair<string, object?>("type", "output"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Bedrock usage tokens.");
        }

        return responseJson;
    }

    private static bool TryReadNovaText(JsonElement root, out string answer)
    {
        answer = string.Empty;

        if (!root.TryGetProperty("output", out var output))
            return false;

        if (!output.TryGetProperty("message", out var message))
            return false;

        if (!message.TryGetProperty("content", out var content))
            return false;

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
            {
                answer = text.GetString() ?? string.Empty;
                return true;
            }
        }

        return false;
    }
}
