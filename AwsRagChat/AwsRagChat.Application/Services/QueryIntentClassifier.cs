namespace AwsRagChat.Application.Services;

public enum QueryIntent
{
    Greeting,
    KnowledgeOverview,
    Metadata,
    DocumentQuestion,
    General
}

public static class QueryIntentClassifier
{
    public static QueryIntent Classify(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return QueryIntent.General;

        var q = question.Trim().ToLowerInvariant();

        // =========================================
        // GREETING
        // =========================================
        if (
            q is "hi" or
            "hii" or
            "hello" or
            "hey" or
            "hai" or
            "good morning" or
            "good evening")
        {
            return QueryIntent.Greeting;
        }

        if (IsKnowledgeOverviewQuestion(q))
            return QueryIntent.KnowledgeOverview;

        if (IsGeneralTechnicalGuidanceQuestion(q))
            return QueryIntent.General;

        // =========================================
        // METADATA / ANALYTICS / DASHBOARD
        // =========================================
        if (IsMetadataQuestion(q))
            return QueryIntent.Metadata;

        // =========================================
        // DOCUMENT RAG QUESTIONS
        // =========================================
        if (IsDocumentQuestion(q))
            return QueryIntent.DocumentQuestion;

        return QueryIntent.General;
    }

    public static bool IsGeneralTechnicalGuidanceQuestion(string q)
    {
        var asksForHowToGuidance =
            q.StartsWith("how to ") ||
            q.StartsWith("how do i ") ||
            q.StartsWith("how can i ") ||
            q.StartsWith("where can i ") ||
            q.StartsWith("where do i ") ||
            q.StartsWith("where to ") ||
            q.StartsWith("steps to ") ||
            q.Contains(" how to ") ||
            q.Contains(" how do i ") ||
            q.Contains(" where can i ") ||
            q.Contains(" steps to ");

        if (!asksForHowToGuidance)
            return false;

        var technicalSignals = new[]
        {
            "access token",
            "id token",
            "refresh token",
            "jwt",
            "bearer token",
            "oauth",
            "openid",
            "oidc",
            "cognito",
            "swagger",
            "postman",
            "browser",
            "developer tools",
            "devtools",
            "local storage",
            "session storage",
            "cookie",
            "api",
            "endpoint",
            "authorization header",
            "authentication header",
            "http",
            "https",
            "login flow",
            "client id",
            "callback url",
            "redirect url",
            "aws",
            "console",
            "sdk",
            "cli"
        };

        return technicalSignals.Any(signal =>
            q.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsKnowledgeOverviewQuestion(string q)
    {
        var normalized = q.Trim().TrimEnd('?', '.', '!');

        return normalized is
                   "what content do you have for me" or
                   "what is there for me" or
                   "what can i ask" or
                   "what knowledge is available" or
                   "what can i ask about" or
                   "what topics are available" or
                   "what information is available" ||
               normalized.Contains("what content do you have for me") ||
               normalized.Contains("what knowledge is available") ||
               normalized.Contains("what can i ask") ||
               normalized.Contains("what topics are available");
    }

    public static bool IsMetadataQuestion(string q)
    {
        var asksForMetadataView =
            q.Contains("document") ||
            q.Contains("documents") ||
            q.Contains("doc") ||
            q.Contains("docs") ||
            q.Contains("file") ||
            q.Contains("files") ||
            q.Contains("upload") ||
            q.Contains("uploads") ||
            q.Contains("status") ||
            q.Contains("dashboard") ||
            q.Contains("analytics") ||
            q.Contains("statistics") ||
            q.Contains("page count") ||
            q.Contains("total pages") ||
            q.Contains("how many pages");

        return

            // =========================
            // DOCUMENT COUNTS
            // =========================
            q.Contains("how many doc") ||
            q.Contains("how many docs") ||
            q.Contains("how many document") ||
            q.Contains("how many documents") ||
            q.Contains("how many file") ||
            q.Contains("how many files") ||

            q.Contains("count doc") ||
            q.Contains("count docs") ||
            q.Contains("count document") ||
            q.Contains("count documents") ||
            q.Contains("count file") ||
            q.Contains("count files") ||

            q.Contains("number of doc") ||
            q.Contains("number of docs") ||
            q.Contains("number of document") ||
            q.Contains("number of documents") ||

            q.Contains("total doc") ||
            q.Contains("total docs") ||
            q.Contains("total document") ||
            q.Contains("total documents") ||

            // =========================
            // LIST FILES
            // =========================
            q.Contains("list doc") ||
            q.Contains("list docs") ||
            q.Contains("list document") ||
            q.Contains("list documents") ||

            q.Contains("list file") ||
            q.Contains("list files") ||

            q.Contains("show file") ||
            q.Contains("show files") ||

            q.Contains("show document") ||
            q.Contains("show documents") ||

            q.Contains("uploaded files") ||
            q.Contains("uploaded documents") ||

            q.Contains("what files do i have") ||
            q.Contains("what documents do i have") ||

            q.Contains("my uploads") ||

            // =========================
            // STATUS / ANALYTICS
            // =========================
            q.Contains("status") ||
            q.Contains("statuses") ||
            q.Contains("upload status") ||
            q.Contains("upload statuses") ||
            q.Contains("document status") ||
            q.Contains("document statuses") ||

            // =========================
            // PAGE METADATA
            // =========================
            q.Contains("how many pages") ||
            q.Contains("total pages") ||
            q.Contains("number of pages") ||

            // =========================
            // METADATA VISUALIZATIONS
            // =========================
            ((q.Contains("table") ||
              q.Contains("chart") ||
              q.Contains("graph") ||
              q.Contains("pie chart") ||
              q.Contains("bar chart") ||
              q.Contains("line chart") ||
            q.Contains("dashboard") ||
            q.Contains("analytics") ||
            q.Contains("statistics")) &&
             asksForMetadataView);
    }

    public static bool IsDocumentQuestion(string q)
    {
        var enterpriseKnowledgeSignals = new[]
        {
            "policy",
            "leave",
            "attendance",
            "holiday",
            "benefit",
            "benefits",
            "payroll",
            "salary",
            "expense",
            "expenses",
            "reimbursement",
            "compliance",
            "subscription",
            "tier",
            "sales",
            "revenue",
            "operating expense",
            "opex",
            "target audience",
            "employee",
            "employees",
            "hr"
        };

        var asksAboutEnterpriseKnowledge =
            enterpriseKnowledgeSignals.Any(signal =>
                q.Contains(signal, StringComparison.OrdinalIgnoreCase));

        return

            q.Contains("summarize") ||
            q.Contains("summary") ||
            q.Contains("overview") ||
            (q.Contains("explain") && asksAboutEnterpriseKnowledge) ||
            (q.StartsWith("what is ") && asksAboutEnterpriseKnowledge) ||
            (q.StartsWith("what are ") && asksAboutEnterpriseKnowledge) ||
            (q.StartsWith("how to ") && asksAboutEnterpriseKnowledge) ||
            (q.StartsWith("how do i ") && asksAboutEnterpriseKnowledge) ||
            q.Contains("explain this document") ||
            q.Contains("explain the document") ||
            q.Contains("explain this file") ||
            q.Contains("explain the file") ||
            q.Contains("explain this pdf") ||
            q.Contains("extract") ||
            q.Contains("what does") ||
            q.Contains("tell me about") ||
            q.Contains("in the document") ||
            q.Contains("in this document") ||
            q.Contains("in the file") ||
            q.Contains("in this file") ||
            q.Contains("in the pdf") ||
            q.Contains("from the document") ||
            q.Contains("from this document") ||
            q.Contains("from the file") ||
            q.Contains("according to the document") ||
            q.Contains("mentioned in the document") ||
            q.Contains("mentioned in this document") ||
            q.Contains("uploaded document") ||
            q.Contains("document says") ||
            q.Contains("file says");
    }
}
