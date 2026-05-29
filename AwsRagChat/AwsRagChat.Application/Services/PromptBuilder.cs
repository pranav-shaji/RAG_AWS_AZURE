using AwsRagChat.Domain.Entities;
using System.Text;

namespace AwsRagChat.Application.Services;

public static class PromptBuilder
{
    public static string BuildKnowledgeOverviewPrompt(
        IReadOnlyList<string> knowledgeSignals)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You summarize the available enterprise knowledge areas using ONLY the headings, sections, and snippets below.");
        sb.AppendLine("Do not use outside knowledge. Do not invent topics. Do not mention document names, file names, document IDs, S3 keys, source labels, or citations.");
        sb.AppendLine("Return a short answer that starts exactly with:");
        sb.AppendLine("I can help with these knowledge areas available in the enterprise knowledge base:");
        sb.AppendLine("Then provide 3 to 7 concise bullet points. Use broad topic areas, not file names.");
        sb.AppendLine();
        sb.AppendLine("Available indexed content signals:");

        foreach (var signal in knowledgeSignals.Take(24))
        {
            if (string.IsNullOrWhiteSpace(signal))
                continue;

            sb.AppendLine("- " + TrimToMaxLength(signal, 420));
        }

        return sb.ToString();
    }

    public static string BuildGeneralAssistantPrompt(
        string question,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat = "text")
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a helpful enterprise AI assistant.");
        sb.AppendLine("Answer naturally and professionally using general knowledge and reasoning.");
        sb.AppendLine("Do not claim to use uploaded documents unless document context is provided.");
        sb.AppendLine("Do not fabricate citations.");
        sb.AppendLine();

        AppendOutputFormatRules(sb, outputFormat);
        AppendConversationContext(sb, conversationHistory, conversationSummary);

        sb.AppendLine("Current User Question:");
        sb.AppendLine(question);

        return sb.ToString();
    }

    public static string BuildGroundedPrompt(
        string question,
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary,
        string outputFormat = "text")
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are an extractive enterprise RAG assistant. You must answer ONLY from the retrieved document context below.");
        sb.AppendLine("The retrieved context is the only source of truth. Do not use outside knowledge, prior knowledge, assumptions, or examples.");
        sb.AppendLine("If the context does not directly support the answer, respond exactly: I could not find that information in the available enterprise documents.");
        sb.AppendLine("Do not invent information, numbers, policies, names, dates, or facts.");
        sb.AppendLine("Never provide generic policy lists, examples, summaries, names, rules, numbers, dates, or sections unless those exact details are present in the retrieved context.");
        sb.AppendLine("For list questions, include only items explicitly stated in the retrieved context.");
        sb.AppendLine("Every factual statement must be supported by the retrieved context.");
        sb.AppendLine("Keep the answer close to the wording of the retrieved chunks.");
        sb.AppendLine("Do not infer, expand, explain, or add implications beyond what the retrieved chunks say.");
        sb.AppendLine("Use source markers like [doc1] only as internal attribution signals for supported claims; they will be removed before display.");
        sb.AppendLine("Do not mention source tags in prose or explain the source tag system.");
        sb.AppendLine("When the retrieved context contains structured business data such as tables, rows, columns, tiers, categories, percentages, metrics, prices, counts, target audiences, comparisons, or distributions, preserve that structure instead of flattening it into a paragraph.");
        sb.AppendLine("For structured or table-like context, answer with: a short overview, then a valid Markdown table using column names that match the context, then 1 to 3 short key insights only when directly supported.");
        sb.AppendLine("In Markdown tables, preserve all numeric values, percentages, labels, category names, tier names, and audience descriptions exactly as stated in the retrieved context.");
        sb.AppendLine("Do not invent missing rows, columns, percentages, categories, metrics, explanations, or trend interpretations.");
        sb.AppendLine("If the retrieved context has only a simple prose fact and no structured data, answer normally in concise prose.");
        sb.AppendLine();

        AppendOutputFormatRules(sb, outputFormat);
        AppendConversationContext(sb, conversationHistory, conversationSummary);

        sb.AppendLine("Context:");
        sb.AppendLine();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            sb.AppendLine($"[doc{i + 1}] Source");
            sb.AppendLine($"File: {TrimToMaxLength(chunk.FileName, 180)}");
            sb.AppendLine($"Page: {chunk.PageNumber}");
            sb.AppendLine($"Section: {TrimToMaxLength(chunk.Section, 180)}");
            sb.AppendLine("Text:");
            sb.AppendLine(TrimToMaxLength(chunk.Text, 2200));
            sb.AppendLine();
        }

        sb.AppendLine("Current User Question:");
        sb.AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("Answer using only the retrieved context above.");
        sb.AppendLine("Add internal source markers like [doc1] only next to factual claims supported by that chunk; do not otherwise expose or explain those tags.");
        sb.AppendLine("If the answer is incomplete or unavailable in the retrieved context, respond exactly: I could not find that information in the available enterprise documents.");

        return sb.ToString();
    }

    private static void AppendConversationContext(
        StringBuilder sb,
        IReadOnlyList<ConversationMessage> conversationHistory,
        string? conversationSummary)
    {
        if (!string.IsNullOrWhiteSpace(conversationSummary))
        {
            sb.AppendLine("Conversation Summary:");
            sb.AppendLine(TrimToMaxLength(conversationSummary, 2000));
            sb.AppendLine();
        }

        if (conversationHistory.Count == 0)
            return;

        sb.AppendLine("Recent Conversation History:");
        sb.AppendLine();

        foreach (var message in conversationHistory
                     .OrderBy(x => x.CreatedAtUtc)
                     .TakeLast(10))
        {
            var roleLabel = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "Assistant"
                : "User";

            sb.AppendLine($"{roleLabel}: {TrimToMaxLength(message.Content, 1200)}");
        }

        sb.AppendLine();
    }

    private static void AppendOutputFormatRules(StringBuilder sb, string outputFormat)
    {
        var normalizedFormat = outputFormat.ToLowerInvariant();

        sb.AppendLine("Output Format Rules:");

        if (normalizedFormat == "table")
        {
            sb.AppendLine("- Return the main answer as a valid markdown table.");
            sb.AppendLine("- The table must include a header row and separator row.");
            sb.AppendLine("- Do not use plain aligned text as a table.");
            sb.AppendLine("- Add a short note after the table when useful.");
        }
        else if (normalizedFormat == "pie-chart")
        {
            sb.AppendLine("- Return data suitable for a PIE CHART.");
            sb.AppendLine("- Focus on proportional distribution.");
            sb.AppendLine("- Include category labels and numeric values from the retrieved context.");
            sb.AppendLine("- Return the chartable data as a valid markdown table with exactly two columns: Category and Value.");
            sb.AppendLine("- If the retrieved context does not contain enough numeric/category data, say that clearly instead of inventing data.");
            sb.AppendLine("- Do not generate bar or line chart descriptions.");
        }
        else if (normalizedFormat == "bar-chart")
        {
            sb.AppendLine("- Return data suitable for a BAR CHART.");
            sb.AppendLine("- Compare values across categories.");
            sb.AppendLine("- Include category labels and numeric values from the retrieved context.");
            sb.AppendLine("- Return the chartable data as a valid markdown table with exactly two columns: Category and Value.");
            sb.AppendLine("- If the retrieved context does not contain enough numeric/category data, say that clearly instead of inventing data.");
            sb.AppendLine("- Do not generate pie or line chart descriptions.");
        }
        else if (normalizedFormat == "line-chart")
        {
            sb.AppendLine("- Return data suitable for a LINE CHART.");
            sb.AppendLine("- Focus on trends or sequential progression.");
            sb.AppendLine("- Include ordered labels and numeric values from the retrieved context.");
            sb.AppendLine("- Return the chartable data as a valid markdown table with exactly two columns: Category and Value.");
            sb.AppendLine("- If the retrieved context does not contain enough numeric/category data, say that clearly instead of inventing data.");
            sb.AppendLine("- Do not generate pie or bar chart descriptions.");
        }
        else if (normalizedFormat == "image")
        {
            sb.AppendLine("- If the user asks to extract images, describe available image assets and their source pages.");
            sb.AppendLine("- Do not invent diagrams or figures. If images are unavailable, explain that clearly.");
        }
        else if (normalizedFormat == "json")
        {
            sb.AppendLine("- Return compact valid JSON only when the user explicitly asked for JSON.");
        }
        else if (normalizedFormat == "code")
        {
            sb.AppendLine("- Use fenced code blocks for code.");
            sb.AppendLine("- Add concise explanation before or after the code when helpful.");
        }
        else
        {
            sb.AppendLine("- Return normal readable text with short paragraphs or bullets when helpful.");
            sb.AppendLine("- Use a valid Markdown table when the retrieved context itself is table-like or contains categories, tiers, percentages, metrics, or comparison data.");
            sb.AppendLine("- Do not use a table for simple prose facts, policy definitions, or one-value answers.");
        }

        sb.AppendLine();
    }

    private static string TrimToMaxLength(string input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        if (input.Length <= maxLength)
            return input;

        return input[..maxLength] + "...";
    }
}
