using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using Azure.Identity;
using System.ClientModel;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Services;
using AwsRagChat.Domain.Entities;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace AwsRagChat.Infrastructure.AI;

public sealed class AzureOpenAiChatService : IChatProvider
{
    private readonly ChatClient _client;
    private readonly AzureOpenAiOptions _options;
    private readonly ILogger<AzureOpenAiChatService> _logger;

    public AzureOpenAiChatService(
        IOptions<AzureOpenAiOptions> options,
        ILogger<AzureOpenAiChatService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new InvalidOperationException("Azure OpenAI Endpoint is missing.");

        if (string.IsNullOrWhiteSpace(_options.ChatDeploymentName))
            throw new InvalidOperationException("Azure OpenAI Chat Deployment Name is missing.");

        AzureOpenAIClient openAiClient;

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            openAiClient = new AzureOpenAIClient(new Uri(_options.Endpoint), new ApiKeyCredential(_options.ApiKey));
        }
        else
        {
            openAiClient = new AzureOpenAIClient(new Uri(_options.Endpoint), new DefaultAzureCredential());
        }

        _client = openAiClient.GetChatClient(_options.ChatDeploymentName);
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

        var answer = await CompleteChatAsync(
            prompt,
            "GroundedAnswer",
            cancellationToken,
            ("ChunkCount", relevantChunks.Count.ToString()),
            ("OutputFormat", outputFormat));

        return string.IsNullOrWhiteSpace(answer)
            ? "I do not know based on the uploaded documents."
            : answer;
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

        var answer = await CompleteChatAsync(
            prompt,
            "GeneralAnswer",
            cancellationToken,
            ("OutputFormat", outputFormat));

        return string.IsNullOrWhiteSpace(answer)
            ? "I could not generate a useful answer for that."
            : answer;
    }

    public async Task<string> GenerateKnowledgeOverviewAsync(
        IReadOnlyList<string> knowledgeSignals,
        CancellationToken cancellationToken = default)
    {
        var prompt = PromptBuilder.BuildKnowledgeOverviewPrompt(
            knowledgeSignals);

        var answer = await CompleteChatAsync(
            prompt,
            "KnowledgeOverview",
            cancellationToken,
            ("SignalCount", knowledgeSignals.Count.ToString()));

        return string.IsNullOrWhiteSpace(answer)
            ? "No enterprise knowledge is available yet. Please contact an admin to upload and index documents."
            : answer;
    }

    private async Task<string> CompleteChatAsync(
        string prompt,
        string operation,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] dimensions)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = operation == "KnowledgeOverview" ? 700 : 4096,
            Temperature = operation == "GeneralAnswer" ? 0.2f : 0.1f
        };

        var messages = new ChatMessage[]
        {
            new UserChatMessage(prompt)
        };

        var stopwatch = Stopwatch.StartNew();
        var response = await _client.CompleteChatAsync(messages, options, cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "Azure OpenAI chat completed. DeploymentName={DeploymentName}, Operation={Operation}, DurationMs={DurationMs}, PromptLength={PromptLength}, Details={Details}",
            _options.ChatDeploymentName,
            operation,
            stopwatch.ElapsedMilliseconds,
            prompt.Length,
            string.Join(",", dimensions.Select(dimension => $"{dimension.Name}={dimension.Value}")));

        var chatCompletion = response?.Value;
        if (chatCompletion?.Content != null)
        {
            return chatCompletion.Content.ToString()!.Trim();
        }

        throw new InvalidOperationException("Azure OpenAI returned an empty chat response.");
    }
}
