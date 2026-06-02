    using AwsRagChat.Application.DTOs;
    using AwsRagChat.Application.Interfaces;
    using AwsRagChat.Application.Models;
    using AwsRagChat.Domain.Entities;
    using Microsoft.Extensions.Logging;
    using System.Diagnostics;
    using System.Globalization;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    namespace AwsRagChat.Application.Services;

    public sealed class ChatService
    {
        private const int HistoryWindowSize = 10;
        private const string GroundedNoResultMessage = "I could not find this information in the uploaded documents.";
        private const string NoChartableDataMessage = "I found relevant document content, but it does not contain enough numeric/category data to render a chart.";
        private const string NoEnterpriseKnowledgeMessage = "No enterprise documents are available for your role yet.";
        private const string PendingApprovalMessage = "Your account is pending admin approval.";

        private readonly IConversationRepository _conversationRepository;
        private readonly RetrievalService _retrievalService;
        private readonly IDocumentRepository _documentRepository;
        private readonly IChunkRepository _chunkRepository;
        private readonly IChatCompletionService _chatCompletionService;
        private readonly IStorageService _storageService;
        private readonly IUserApprovalService _userApprovalService;
        private readonly ILogger<ChatService> _logger;

        public ChatService(
            IConversationRepository conversationRepository,
            RetrievalService retrievalService,
            IDocumentRepository documentRepository,
            IChunkRepository chunkRepository,
            IChatCompletionService chatCompletionService,
            IStorageService storageService,
            IUserApprovalService userApprovalService,
            ILogger<ChatService> logger)
        {
            _conversationRepository = conversationRepository;
            _retrievalService = retrievalService;
            _documentRepository = documentRepository;
            _chunkRepository = chunkRepository;
            _chatCompletionService = chatCompletionService;
            _storageService = storageService;
            _userApprovalService = userApprovalService;
            _logger = logger;
        }

        public async Task<ChatAskResponse> AskAsync(
            string ownerUserId,
            string userEmail,
            string? claimedRole,
            ChatAskRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ownerUserId))
                throw new ArgumentException("OwnerUserId is required.", nameof(ownerUserId));

            if (request is null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.SessionId))
                throw new ArgumentException("SessionId is required.", nameof(request.SessionId));

            if (string.IsNullOrWhiteSpace(request.Question))
                throw new ArgumentException("Question is required.", nameof(request.Question));

        var totalStopwatch = Stopwatch.StartNew();

        var trimmedQuestion = request.Question.Trim();

        var intent =
            QueryIntentClassifier.Classify(trimmedQuestion);

        var hasExplicitDocumentScope =
            !string.IsNullOrWhiteSpace(request.DocumentId);

        var shouldUseRetrieval =
            intent is not QueryIntent.Greeting and
                not QueryIntent.KnowledgeOverview and
                not QueryIntent.Metadata;

        var searchSharedEnterpriseDocuments =
            request.SearchAcrossAllDocuments ||
            !hasExplicitDocumentScope;

        _logger.LogInformation(
            "Incoming chat ask payload. UserId={OwnerUserId}, SessionId={SessionId}, DocumentId={DocumentId}, SearchAcrossAllDocuments={SearchAcrossAllDocuments}",
            ownerUserId,
            request.SessionId,
            request.DocumentId ?? "(none)",
            request.SearchAcrossAllDocuments);

        _logger.LogInformation(
            "Chat request scope resolved. UserId={OwnerUserId}, SessionId={SessionId}, SearchAcrossAllDocuments={SearchAcrossAllDocuments}, DocumentId={DocumentId}, SearchSharedEnterpriseDocuments={SearchSharedEnterpriseDocuments}, Intent={Intent}, ShouldUseRetrieval={ShouldUseRetrieval}",
            ownerUserId,
            request.SessionId,
            request.SearchAcrossAllDocuments,
            request.DocumentId ?? "(none)",
            searchSharedEnterpriseDocuments,
            intent,
            shouldUseRetrieval);

        string responseType = AiResponseType.Text;

        string finalAnswer = string.Empty;

        var citations = new List<Citation>();

        object? data = null;

        ChartData? chartData = null;

        var historyTask =
            _conversationRepository.GetMessagesAsync(
                ownerUserId,
                request.SessionId,
                HistoryWindowSize,
                cancellationToken);

        var sessionTask =
            _conversationRepository.GetSessionAsync(
                ownerUserId,
                request.SessionId,
                cancellationToken);

        var conversationLoadStopwatch = Stopwatch.StartNew();

        await Task.WhenAll(historyTask, sessionTask);

        conversationLoadStopwatch.Stop();

        var recentHistory = await historyTask;

        var orderedHistory =
            recentHistory
                .OrderBy(x => x.CreatedAtUtc)
                .ToList();

        var outputFormat = "text";

        var session = await sessionTask;

        if (session is null || session.IsArchived)
            throw new KeyNotFoundException(
                "Conversation session was not found.");

        _logger.LogInformation(
            "Chat timing. Stage=ConversationLoad, DurationMs={DurationMs}, SessionId={SessionId}, HistoryCount={HistoryCount}",
            conversationLoadStopwatch.ElapsedMilliseconds,
            request.SessionId,
            orderedHistory.Count);

        var access = await _userApprovalService.ResolveAccessAsync(
            ownerUserId,
            userEmail,
            claimedRole,
            cancellationToken);

        _logger.LogInformation(
            "Chat access scope resolved. UserId={OwnerUserId}, ApprovalStatus={ApprovalStatus}, ApprovedRole={ApprovedRole}, IsApproved={IsApproved}",
            ownerUserId,
            access.ApprovalStatus,
            access.ApprovedRole,
            access.IsApproved);

        if (!access.IsApproved)
        {
            finalAnswer = PendingApprovalMessage;
            responseType = AiResponseType.Text;

            await SaveConversationTurnAsync(
                ownerUserId,
                request,
                session,
                trimmedQuestion,
                finalAnswer,
                citations,
                responseType,
                data,
                chartData,
                cancellationToken);

            return new ChatAskResponse
            {
                ResponseType = responseType,
                Answer = finalAnswer,
                Citations = citations,
                Data = data,
                ChartData = chartData
            };
        }

        if (intent == QueryIntent.Greeting)
        {
            finalAnswer =
                "Hello! How can I assist you today?";
        }

        else if (intent == QueryIntent.KnowledgeOverview)
        {
            finalAnswer =
                await BuildKnowledgeOverviewAnswerAsync(
                    access.ApprovedRole,
                    cancellationToken);

            responseType = AiResponseType.Text;
        }

        else if (intent == QueryIntent.Metadata)
        {

            var metadataResponseType =
                  ResponsePlanner.Plan(
                    trimmedQuestion,
                  hasExplicitDocumentScope)
                  .ResponseType;

            var metadataResult =
                await BuildMetadataAnswerAsync(
                    ownerUserId,
                    request,
                    access.ApprovedRole,
                    metadataResponseType,
                    trimmedQuestion,
                    cancellationToken);

            finalAnswer = metadataResult.Answer;

            responseType = metadataResult.ResponseType;

            data = metadataResult.Data;

            chartData = metadataResult.ChartData;
        }

        else if (intent == QueryIntent.General && !shouldUseRetrieval)
        {
            var plan =
                ResponsePlanner.Plan(
                    trimmedQuestion,
                    false);

            responseType = plan.ResponseType == AiResponseType.Image
                ? AiResponseType.Text
                : plan.ResponseType;

            outputFormat =
                GetOutputFormat(responseType);

            _logger.LogInformation(
                "Chat ask routed to general assistant. UserId={OwnerUserId}, SessionId={SessionId}, Intent={Intent}, ResponseType={ResponseType}, QuestionLength={QuestionLength}",
                ownerUserId,
                request.SessionId,
                intent,
                responseType,
                trimmedQuestion.Length);

            finalAnswer =
                await _chatCompletionService.GenerateGeneralAnswerAsync(
                    trimmedQuestion,
                    orderedHistory,
                    session.Summary,
                    outputFormat,
                    cancellationToken);

            if (responseType == AiResponseType.Table)
            {
                data =
                    BuildTableDataFromMarkdown(finalAnswer);
            }
        }

        else
        {
            var plan =
                ResponsePlanner.Plan(
                    trimmedQuestion,
                    true);

            responseType = plan.ResponseType;

            _logger.LogInformation(
                "Chat retrieval path selected. UserId={OwnerUserId}, UserRole={UserRole}, SessionId={SessionId}, DocumentId={DocumentId}, SearchAcrossAllDocuments={SearchAcrossAllDocuments}, GlobalMode={GlobalMode}, Route={Route}, ResponseType={ResponseType}, QuestionLength={QuestionLength}",
                ownerUserId,
                access.ApprovedRole,
                request.SessionId,
                request.DocumentId ?? "(none)",
                request.SearchAcrossAllDocuments,
                searchSharedEnterpriseDocuments,
                plan.Route,
                plan.ResponseType,
                trimmedQuestion.Length);

            switch (plan.Route)
            {
                case AiRoute.ImageExtraction:
                    {
                        var imageResult =
                            await BuildImageExtractionAnswerAsync(
                                ownerUserId,
                                request,
                                access.ApprovedRole,
                                cancellationToken);

                        finalAnswer = imageResult.Answer;

                        responseType = imageResult.ResponseType;

                        data = imageResult.Data;

                        citations = imageResult.Citations;

                        break;
                    }

                case AiRoute.Rag:
                default:
                    {
                        outputFormat =
                             GetOutputFormat(responseType);

                        await ValidateDocumentScopeAsync(
                            ownerUserId,
                            request,
                            searchSharedEnterpriseDocuments,
                            access.ApprovedRole,
                            cancellationToken);

                        var ragAnswer =
                            await _retrievalService.AskAsync(
                                ownerUserId,
                                access.ApprovedRole,
                                searchSharedEnterpriseDocuments
                                    ? null
                                    : request.DocumentId,
                                trimmedQuestion,
                                orderedHistory,
                                session.Summary,
                                outputFormat,
                                cancellationToken);

                        finalAnswer = ragAnswer.Answer;

                        citations = ragAnswer.Citations;

                        if (IsGroundedNoResult(finalAnswer))
                        {
                            finalAnswer = GroundedNoResultMessage;
                            citations = [];
                        }

                        _logger.LogInformation(
                            "Chat retrieval completed. UserId={OwnerUserId}, UserRole={UserRole}, SessionId={SessionId}, DocumentId={DocumentId}, SearchAcrossAllDocuments={SearchAcrossAllDocuments}, CitationCount={CitationCount}",
                            ownerUserId,
                            access.ApprovedRole,
                            request.SessionId,
                            request.DocumentId ?? "(none)",
                            request.SearchAcrossAllDocuments,
                            citations.Count);

                        if (responseType is
                            AiResponseType.PieChart or
                            AiResponseType.BarChart or
                            AiResponseType.LineChart)
                        {
                            chartData =
                                BuildChartDataFromAnswer(finalAnswer);

                            if (chartData is null &&
                                !IsGroundedNoResult(finalAnswer))
                            {
                                finalAnswer = NoChartableDataMessage;
                            }
                        }

                        if (responseType == AiResponseType.Table)
                        {
                            data =
                                BuildTableDataFromMarkdown(finalAnswer);
                        }

                        break;
                    }
            }
        }

        var saveStopwatch = Stopwatch.StartNew();

        await SaveConversationTurnAsync(
            ownerUserId,
            request,
            session,
            trimmedQuestion,
            finalAnswer,
            citations,
            responseType,
            data,
            chartData,
            cancellationToken);

        saveStopwatch.Stop();
        totalStopwatch.Stop();

        _logger.LogInformation(
            "Chat timing. Stage=SaveConversationTurn, DurationMs={DurationMs}, SessionId={SessionId}",
            saveStopwatch.ElapsedMilliseconds,
            request.SessionId);

        _logger.LogInformation(
            "Chat timing. Stage=TotalChatRequest, DurationMs={DurationMs}, SessionId={SessionId}, ResponseType={ResponseType}, CitationCount={CitationCount}",
            totalStopwatch.ElapsedMilliseconds,
            request.SessionId,
            responseType,
            citations.Count);

        return new ChatAskResponse
        {
            ResponseType = responseType,
            Answer = finalAnswer,
            Citations = citations,
            Data = data,
            ChartData = chartData
        };
    }
   

        private async Task SaveConversationTurnAsync(
            string ownerUserId,
            ChatAskRequest request,
            ConversationSession session,
            string trimmedQuestion,
            string finalAnswer,
            List<Citation> citations,
            string responseType,
            object? data,
            ChartData? chartData,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;

            var userMessageTask = _conversationRepository.AddMessageAsync(
                    new ConversationMessage
                    {
                        OwnerUserId = ownerUserId,
                        SessionId = request.SessionId,
                        MessageId = Guid.NewGuid().ToString(),
                        Role = "user",
                        Content = request.Question,
                        CreatedAtUtc = now
                    },
                    cancellationToken);

            var assistantMessageTask = _conversationRepository.AddMessageAsync(
                    new ConversationMessage
                    {
                        OwnerUserId = ownerUserId,
                        SessionId = request.SessionId,
                        MessageId = Guid.NewGuid().ToString(),
                        Role = "assistant",
                        Content = finalAnswer,
                        CreatedAtUtc = now.AddTicks(1),
                        Citations = citations,
                        ResponseType = responseType,
                        DataJson = SerializePayload(data),
                        ChartDataJson = SerializePayload(chartData)
                    },
                    cancellationToken);

            await Task.WhenAll(userMessageTask, assistantMessageTask);

            session.MessageCount += 2;
            session.LastMessageAtUtc = DateTime.UtcNow;
            session.UpdatedAtUtc = DateTime.UtcNow;

            if (session.Title == "New chat")
                session.Title = GenerateConversationTitle(trimmedQuestion);

            await _conversationRepository.UpsertSessionAsync(
                session,
                cancellationToken);
        }

        private static string GetOutputFormat(string responseType)
        {
            return responseType switch
            {
                AiResponseType.Table => "table",

                AiResponseType.PieChart => "pie-chart",

                AiResponseType.BarChart => "bar-chart",

                AiResponseType.LineChart => "line-chart",

                AiResponseType.Image => "image",

                AiResponseType.Json => "json",

                AiResponseType.Code => "code",

                _ => "text"
            };
        }

        private async Task ValidateDocumentScopeAsync(
            string ownerUserId,
            ChatAskRequest request,
            bool searchSharedEnterpriseDocuments,
            string currentUserRole,
            CancellationToken cancellationToken)
        {
            if (searchSharedEnterpriseDocuments)
            {
                var adminDocuments =
                    (await _documentRepository.GetAdminDocumentsAsync(cancellationToken))
                    .Where(document => DocumentAllowsRole(document, currentUserRole))
                    .ToList();

                if (adminDocuments.Count == 0)
                {
                    throw new ArgumentException(
                        "No admin documents are available yet.");
                }

                if (!adminDocuments.Any(x => x.IsSearchable))
                {
                    throw new ArgumentException(
                        "Documents exist but indexing is not completed yet.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(request.DocumentId))
                throw new ArgumentException("Please upload or select a document first.");

            var document = await _documentRepository.GetDocumentByIdAsync(request.DocumentId, cancellationToken);

            if (document is null || !document.IsAdminDocument)
            {
                throw new ArgumentException("Document not found.");
            }

            if (!DocumentAllowsRole(document, currentUserRole))
                throw new ArgumentException("Document not found.");

            if (!document.IsSearchable)
                throw new ArgumentException($"Document is not yet searchable. Current status: {document.Status}. Please wait for processing to complete.");

            if (document.ChunkCount <= 0)
                throw new ArgumentException("Document indexing completed without searchable chunks. Upload a document with extractable text or try another file.");
        }

        private async Task<string> BuildKnowledgeOverviewAnswerAsync(
            string currentUserRole,
            CancellationToken cancellationToken)
        {
            var documents =
                await _documentRepository.GetAdminDocumentsAsync(
                    cancellationToken);

            var searchableDocuments = documents
                .Where(document => document.IsAdminDocument && document.IsSearchable)
                .Where(document => DocumentAllowsRole(document, currentUserRole))
                .DistinctBy(document => document.DocumentId)
                .ToList();

            if (searchableDocuments.Count == 0)
                return NoEnterpriseKnowledgeMessage;

            var documentIds = searchableDocuments
                .Select(document => document.DocumentId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            var chunks =
                await _chunkRepository.GetChunksByDocumentsAsync(
                    documentIds,
                    cancellationToken);

            var sharedChunks = chunks
                .Where(chunk => ChunkAllowsRole(chunk, currentUserRole))
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text) ||
                                !string.IsNullOrWhiteSpace(chunk.Heading) ||
                                !string.IsNullOrWhiteSpace(chunk.Section))
                .OrderBy(chunk => chunk.DocumentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(chunk => chunk.ChunkOrder)
                .ToList();

            if (sharedChunks.Count == 0)
                return NoEnterpriseKnowledgeMessage;

            var knowledgeSignals = BuildKnowledgeOverviewSignals(sharedChunks);

            if (knowledgeSignals.Count == 0)
                return NoEnterpriseKnowledgeMessage;

            return await _chatCompletionService.GenerateKnowledgeOverviewAsync(
                knowledgeSignals,
                cancellationToken);
        }

        private static List<string> BuildKnowledgeOverviewSignals(
            IReadOnlyList<DocumentChunk> chunks)
        {
            var headingSignals = chunks
                .Select(chunk => string.IsNullOrWhiteSpace(chunk.Heading)
                    ? chunk.Section
                    : chunk.Heading)
                .Where(signal => !string.IsNullOrWhiteSpace(signal))
                .Select(CleanKnowledgeSignal)
                .Where(signal => !string.IsNullOrWhiteSpace(signal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(18)
                .ToList();

            if (headingSignals.Count >= 4)
                return headingSignals;

            var snippetSignals = chunks
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
                .GroupBy(chunk => chunk.DocumentId)
                .SelectMany(group => group.Take(4))
                .Select(chunk => CleanKnowledgeSignal(chunk.Text))
                .Where(signal => !string.IsNullOrWhiteSpace(signal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(18)
                .ToList();

            return headingSignals
                .Concat(snippetSignals)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        private static string CleanKnowledgeSignal(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var cleaned = Regex.Replace(
                value,
                @"\s+",
                " ",
                RegexOptions.CultureInvariant).Trim();

            return cleaned.Length <= 420
                ? cleaned
                : cleaned[..420];
        }

        private async Task<MetadataAnswerResult> BuildMetadataAnswerAsync(
            string ownerUserId,
            ChatAskRequest request,
            string currentUserRole,
            string requestedResponseType,
            string question,
            CancellationToken cancellationToken)
        {
            var q = question.ToLowerInvariant();

            var documents = (await _documentRepository.GetAdminDocumentsAsync(
        cancellationToken))
                .Where(document => DocumentAllowsRole(document, currentUserRole))
                .ToList();

            if (documents.Count == 0)
            {
                return new MetadataAnswerResult
                {
                    Answer = "You have not uploaded any documents yet.",
                    ResponseType = requestedResponseType is AiResponseType.PieChart or AiResponseType.BarChart or AiResponseType.LineChart
                        ? requestedResponseType
                        : AiResponseType.Text,
                    Data = null
                };
            }

            if (q.Contains("pie chart") || q.Contains("chart") || q.Contains("graph"))
            {
                var grouped = documents
                    .GroupBy(x => x.Status)
                    .ToList();

                return new MetadataAnswerResult
                {
                    ResponseType = requestedResponseType == AiResponseType.BarChart
                        ? AiResponseType.BarChart
                        : AiResponseType.PieChart,
                    Answer = "Showing upload status distribution.",
                    ChartData = new ChartData
                    {
                        Labels = grouped.Select(x => x.Key).ToList(),
                        Values = grouped.Select(x => x.Count()).ToList()
                    }
                };
            }

            if (q.Contains("table") || q.Contains("tabular"))
            {
                return new MetadataAnswerResult
                {
                    Answer = $"Found {documents.Count} uploaded documents.",
                    ResponseType = AiResponseType.Table,
                    Data = BuildDocumentTableData(documents)
                };
            }

            var lines = documents.Select((doc, index) =>
                $"{index + 1}. {doc.FileName} - {doc.Status}");

            return new MetadataAnswerResult
            {
                Answer = "Here are your uploaded documents:\n\n" + string.Join("\n", lines),
                ResponseType = AiResponseType.Text
            };
        }

        private static MetadataAnswerResult BuildPageCountAnswer(
            IReadOnlyList<ExistingDocumentRecord> documents,
            ChatAskRequest request)
        {
            if (!request.SearchAcrossAllDocuments &&
                !string.IsNullOrWhiteSpace(request.DocumentId))
            {
                var selectedDocument = documents.FirstOrDefault(document =>
                    string.Equals(document.DocumentId, request.DocumentId, StringComparison.Ordinal));

                if (selectedDocument is null)
                {
                    return new MetadataAnswerResult
                    {
                        ResponseType = AiResponseType.Text,
                        Answer = "Document not found."
                    };
                }

                return new MetadataAnswerResult
                {
                    ResponseType = AiResponseType.Text,
                    Answer = selectedDocument.PageCount > 0
                        ? $"This document contains {selectedDocument.PageCount} page{(selectedDocument.PageCount == 1 ? string.Empty : "s")}."
                        : "The page count is not available for this document yet. Re-index the document to populate page-count metadata."
                };
            }

            var rows = documents
                .Select(document => new List<string>
                {
                    document.FileName,
                    document.PageCount > 0 ? document.PageCount.ToString() : "Not available",
                    document.Status
                })
                .ToList();

            return new MetadataAnswerResult
            {
                ResponseType = AiResponseType.Table,
                Answer = $"Page counts for {documents.Count} uploaded document{(documents.Count == 1 ? string.Empty : "s")}.",
                Data = new TableData
                {
                    Columns = ["File Name", "Pages", "Status"],
                    Rows = rows
                }
            };
        }

        private static MetadataAnswerResult BuildInteractiveOptionsAnswer()
        {
            return new MetadataAnswerResult
            {
                ResponseType = AiResponseType.InteractiveOptions,
                Answer = "Choose how you want to explore your uploaded content.",
                Data = new InteractiveOptionsData
                {
                    Options =
                    [
                        new() { Label = "Summarize uploaded documents", Description = "Create a concise summary from the selected document scope.", Action = "prompt", Prompt = "Summarize uploaded documents" },
                        new() { Label = "Show uploaded documents", Description = "List the available files with indexing status.", Action = "prompt", Prompt = "list uploaded files in table format" },
                        new() { Label = "Extract charts/statistics", Description = "Visualize available document or upload statistics.", Action = "prompt", Prompt = "show upload statuses as pie chart" },
                        new() { Label = "Extract images/figures", Description = "Show available uploaded image assets.", Action = "prompt", Prompt = "extract images/figures" },
                        new() { Label = "Ask questions from documents", Description = "Continue with a grounded document question.", Action = "focus-chat", Prompt = string.Empty },
                        new() { Label = "Ask enterprise knowledge base", Description = "Search all shared indexed documents.", Action = "focus-chat", Prompt = string.Empty }
                    ]
                }
            };
        }

        private async Task<MetadataAnswerResult> BuildDocumentSelectorAnswerAsync(
            string ownerUserId,
            string? selectedDocumentId,
            CancellationToken cancellationToken)
        {
            var documents = await _documentRepository.GetAdminDocumentsAsync(
        cancellationToken);

            return new MetadataAnswerResult
            {
                ResponseType = AiResponseType.DocumentSelector,
                Answer = documents.Count == 0
                    ? "No uploaded documents are available yet."
                    : "Select a document to scope retrieval to that file.",
                Data = new DocumentSelectorData
                {
                    Documents = documents.ToList(),
                    SelectedDocumentId = selectedDocumentId
                }
            };
        }

        private async Task<ImageAnswerResult> BuildImageExtractionAnswerAsync(
        string ownerUserId,
        ChatAskRequest request,
        string currentUserRole,
        CancellationToken cancellationToken)
        {
            IReadOnlyList<ExistingDocumentRecord> documents;

            try
            {
                documents = request.SearchAcrossAllDocuments
                    ? (await _documentRepository.GetAdminDocumentsAsync(cancellationToken))
                        .Where(document => DocumentAllowsRole(document, currentUserRole))
                        .ToList()
                    : await GetSingleDocumentAsListAsync(
                        ownerUserId,
                        request.DocumentId,
                        currentUserRole,
                        cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Image extraction request could not load document scope. UserId={OwnerUserId}, DocumentId={DocumentId}",
                    ownerUserId,
                    request.DocumentId ?? "(none)");

                return new ImageAnswerResult
                {
                    Answer = "Please select an uploaded document before extracting figures or images.",
                    ResponseType = AiResponseType.Text
                };
            }

            var imageDocuments = documents
                .Where(IsImageDocument)
                .Take(12)
                .ToList();

            var pdfPreviewDocuments = documents
                .Where(IsPdfDocument)
                .Where(document => !document.IsSearchable || document.ChunkCount <= 0)
                .Take(6)
                .ToList();

            if (imageDocuments.Count == 0 && pdfPreviewDocuments.Count == 0)
            {
                return new ImageAnswerResult
                {
                    Answer = "No extractable figures or images were found in this document.",
                    ResponseType = AiResponseType.Text
                };
            }

            var images = new List<DocumentImageDto>();

            foreach (var document in imageDocuments)
            {
                var url = await CreateSafeReadUrlAsync(document, cancellationToken);

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                images.Add(new DocumentImageDto
                {
                    DocumentId = document.DocumentId,
                    FileName = document.FileName,
                    Url = url,
                    PageNumber = 1,
                    SourceType = "uploaded-image"
                });
            }

            foreach (var document in pdfPreviewDocuments)
            {
                var url = await CreateSafeReadUrlAsync(document, cancellationToken);

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                images.Add(new DocumentImageDto
                {
                    DocumentId = document.DocumentId,
                    FileName = document.FileName,
                    Url = url,
                    PageNumber = 1,
                    SourceType = "pdf-preview"
                });
            }

            if (images.Count == 0)
            {
                return new ImageAnswerResult
                {
                    Answer = "No extractable figures or images were found in this document.",
                    ResponseType = AiResponseType.Text
                };
            }

            return new ImageAnswerResult
            {
                Answer = pdfPreviewDocuments.Count > 0 && imageDocuments.Count == 0
                    ? "This PDF appears to be image-heavy or not text-searchable. I could not extract standalone embedded figures, but you can preview the source PDF below without interrupting the chat."
                    : $"Found {images.Count} image asset{(images.Count == 1 ? string.Empty : "s")} in the selected document scope.",
                ResponseType = AiResponseType.Image,
                Data = images,
                Citations = images.Select(image => new Citation
                {
                    DocumentId = image.DocumentId,
                    FileName = image.FileName,
                    PageNumber = image.PageNumber,
                    Snippet = image.SourceType
                }).ToList()
            };
        }

        private async Task<string> CreateSafeReadUrlAsync(
            ExistingDocumentRecord document,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _storageService.CreateReadUrlAsync(
                    document.S3Key,
                    TimeSpan.FromMinutes(15),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Figure extraction must never fail the chat turn because a preview URL cannot be created.
                _logger.LogWarning(ex, "Could not create image/figure preview URL. DocumentId={DocumentId}, S3Key={S3Key}", document.DocumentId, document.S3Key);
                return string.Empty;
            }
        }

        private async Task<IReadOnlyList<ExistingDocumentRecord>>
        GetSingleDocumentAsListAsync(
        string ownerUserId,
        string? documentId,
        string currentUserRole,
        CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(documentId))
                throw new ArgumentException(
                    "Please upload or select a document first.");

            var document =
                await _documentRepository.GetDocumentByIdAsync(
                    documentId,
                    cancellationToken);

            if (document is null || !document.IsAdminDocument)
            {
                throw new ArgumentException(
                    "Document not found.");
            }

            if (!DocumentAllowsRole(document, currentUserRole))
            {
                throw new ArgumentException(
                    "Document not found.");
            }

            return new List<ExistingDocumentRecord>
        {
            document
        };
        }

        private static bool IsImageDocument(ExistingDocumentRecord document)
        {
            var extension = Path.GetExtension(document.FileName).ToLowerInvariant();
            return extension is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff";
        }

        private static bool IsPdfDocument(ExistingDocumentRecord document)
        {
            return Path.GetExtension(document.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static ChartData? BuildKeywordChartData(IEnumerable<Citation> citations)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "and", "for", "with", "that", "this", "from", "have", "are", "was", "were", "into", "your", "document", "page", "section"
            };

            var grouped = citations
                .SelectMany(citation => citation.Snippet.Split([' ', ',', '.', ';', ':', '(', ')', '[', ']', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(token => token.Trim().ToLowerInvariant())
                .Where(token => token.Length > 3 && !stopWords.Contains(token))
                .GroupBy(token => token)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Take(8)
                .ToList();

            if (grouped.Count == 0)
                return null;

            return new ChartData
            {
                Labels = grouped.Select(group => group.Key).ToList(),
                Values = grouped.Select(group => group.Count()).ToList()
            };
        }

        private static TableData BuildDocumentTableData(IReadOnlyList<ExistingDocumentRecord> documents)
        {
            return new TableData
            {
                Columns = ["File Name", "Status", "Pages", "Chunks", "Document ID"],
                Rows = documents
                    .Select(document => new List<string>
                    {
                        document.FileName,
                        document.Status,
                        document.PageCount > 0 ? document.PageCount.ToString() : "Not available",
                        document.ChunkCount.ToString(),
                        document.DocumentId
                    })
                    .ToList()
            };
        }

        private static TableData? BuildTableDataFromMarkdown(string answer)
        {
            var rows = answer
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith('|') && line.EndsWith('|'))
                .Select(line => line.Trim('|').Split('|').Select(cell => cell.Trim()).ToList())
                .ToList();

            if (rows.Count < 2)
                return null;

            var separator = rows[1];
            var isSeparator = separator.All(cell => Regex.IsMatch(cell, "^:?-{3,}:?$"));

            if (!isSeparator)
                return null;

            var columnCount = rows[0].Count;

            return new TableData
            {
                Columns = rows[0],
                Rows = rows
                    .Skip(2)
                    .Where(row => row.Count == columnCount)
                    .ToList()
            };
        }

        private static ChartData? BuildChartDataFromAnswer(string answer)
        {
            var table = BuildTableDataFromMarkdown(answer);

            if (table?.Rows.Count > 0 && table.Columns.Count >= 2)
            {
                var labels = new List<string>();
                var values = new List<int>();

                foreach (var row in table.Rows)
                {
                    if (row.Count < 2)
                        continue;

                    var numericCell = row.Skip(1).FirstOrDefault(cell => TryParseChartNumber(cell, out _));

                    if (numericCell is null || !TryParseChartNumber(numericCell, out var value))
                        continue;

                    var label = CleanChartLabel(row[0]);

                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    labels.Add(label);
                    values.Add(value);
                }

                if (labels.Count > 0)
                    return new ChartData { Labels = labels, Values = values };
            }

            var linePairs = Regex.Matches(
                answer,
                @"^\s*(?:[-*]|\d+[.)])?\s*(?<label>[A-Za-z][^:\n|]{1,80})\s*[:\-]\s*(?<value>-?\d+(?:,\d{3})*(?:\.\d+)?)\s*%?\s*$",
                RegexOptions.Multiline);

            if (linePairs.Count == 0)
                return null;

            var lineLabels = new List<string>();
            var lineValues = new List<int>();

            foreach (Match match in linePairs.Take(12))
            {
                var label = CleanChartLabel(match.Groups["label"].Value);

                if (string.IsNullOrWhiteSpace(label) ||
                    !TryParseChartNumber(match.Groups["value"].Value, out var value))
                {
                    continue;
                }

                lineLabels.Add(label);
                lineValues.Add(value);
            }

            if (lineLabels.Count == 0)
                return null;

            return new ChartData
            {
                Labels = lineLabels,
                Values = lineValues
            };
        }

        private static bool TryParseChartNumber(string value, out int number)
        {
            var match = Regex.Match(value, @"-?\d+(?:,\d{3})*(?:\.\d+)?");

            if (!match.Success ||
                !double.TryParse(match.Value.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                number = 0;
                return false;
            }

            number = (int)Math.Round(parsed);
            return true;
        }

        private static string CleanChartLabel(string value)
        {
            return Regex.Replace(
                    value.Trim(),
                    @"[*_`]+",
                    string.Empty,
                    RegexOptions.CultureInvariant)
                .Trim();
        }

        private static bool IsGroundedNoResult(string answer)
        {
            return answer.Contains(
                GroundedNoResultMessage,
                StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "I could not find that information in the available enterprise documents.",
                StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "could not find relevant information",
                StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "not found in the uploaded document",
                StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(
                "not found in the retrieved context",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool DocumentAllowsRole(
            ExistingDocumentRecord document,
            string currentUserRole)
        {
            return document.AllowedRoles.Any(role =>
                string.Equals(role, currentUserRole, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ChunkAllowsRole(
            DocumentChunk chunk,
            string currentUserRole)
        {
            return chunk.AllowedRoles.Any(role =>
                string.Equals(role, currentUserRole, StringComparison.OrdinalIgnoreCase));
        }

        private static string SerializePayload(object? payload)
        {
            return payload is null
                ? string.Empty
                : JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
        }

        private static string GenerateConversationTitle(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
                return "New chat";

            var cleaned = question.Trim();

            if (cleaned.Length <= 40)
                return cleaned;

            return cleaned[..40] + "...";
        }

        private sealed class MetadataAnswerResult
        {
            public string ResponseType { get; set; } = AiResponseType.Text;
            public string Answer { get; set; } = string.Empty;
            public object? Data { get; set; }
            public ChartData? ChartData { get; set; }
        }

        private sealed class ImageAnswerResult
        {
            public string ResponseType { get; set; } = AiResponseType.Image;
            public string Answer { get; set; } = string.Empty;
            public object? Data { get; set; }
            public List<Citation> Citations { get; set; } = [];
        }
    }
