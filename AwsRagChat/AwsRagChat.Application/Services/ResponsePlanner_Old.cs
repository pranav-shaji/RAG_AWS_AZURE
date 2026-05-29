using AwsRagChat.Application.Models;

namespace AwsRagChat.Application.Services;

public static class ResponsePlanner
{
    public static ResponsePlan Plan(
    string question,
    bool hasDocumentScope)
    {
        var responseType =
            DetermineResponseType(question);

        var route =
            DetermineRoute(responseType);

        return new ResponsePlan(
            route,
            responseType);
    }

    private static string DetermineResponseType(
        string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return AiResponseType.Text;

        var normalized = question.Trim().ToLowerInvariant();

        if (normalized.Contains("pie chart") || normalized.Contains("pie-chart"))
            return AiResponseType.PieChart;

        if (normalized.Contains("line chart") || normalized.Contains("line-chart"))
            return AiResponseType.LineChart;

        if (normalized.Contains("bar chart") || normalized.Contains("bar-chart") || normalized.Contains("chart") || normalized.Contains("graph"))
            return AiResponseType.BarChart;

        if (normalized.Contains("table") || normalized.Contains("tabular"))
            return AiResponseType.Table;

        if (normalized.Contains("image") || normalized.Contains("figure") || normalized.Contains("diagram"))
            return AiResponseType.Image;

        return question switch
        {
            AiIntent.Chart
                => AiResponseType.BarChart,

            AiIntent.Table
                => AiResponseType.Table,

            AiIntent.Image
                => AiResponseType.Image,

            _ => AiResponseType.Text
        };
    }

    private static AiRoute DetermineRoute(
        string responseType)
    {
        return responseType switch
        {
            AiResponseType.Image
                => AiRoute.ImageExtraction,

            _ => AiRoute.Rag
        };
    }
}

public enum AiRoute
{
    Rag,
    ImageExtraction,
    Conversation
}

public sealed record ResponsePlan(
    AiRoute Route,
    string ResponseType);
