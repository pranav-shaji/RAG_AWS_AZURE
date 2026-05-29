# AWS RAG Chat

Production-oriented enterprise RAG chat application with an Angular standalone frontend, ASP.NET Core Web API backend, Cognito authentication, Bedrock generation/embeddings, OpenSearch vector retrieval, DynamoDB persistence, S3 document storage, and Lambda-based ingestion.

## Architecture Overview

The system has three runtime surfaces:

- `aws-rag-chat-ui`: Angular standalone SPA for Cognito login, upload, document selection, conversation management, and chat.
- `AwsRagChat/AwsRagChat.Api`: ASP.NET Core Web API secured by Cognito access tokens.
- `AwsRagChat/AwsRagChat.Ingestion`: Lambda handlers for S3 ingestion, text extraction, chunking, embeddings, DynamoDB persistence, and OpenSearch indexing.

AWS services:

- Cognito validates users and issues access tokens.
- S3 stores uploaded source documents.
- DynamoDB stores document metadata, chunks, conversation sessions, and messages.
- Textract extracts text from scanned PDFs/images.
- Bedrock generates embeddings and grounded assistant answers.
- OpenSearch Serverless performs primary vector retrieval.

## Folder Structure

```text
.
|-- aws-rag-chat-ui/
|   |-- src/app/
|   |   |-- components/          # Sidebar, upload, chat input, messages, charts
|   |   |-- api.ts               # Typed HTTP client
|   |   |-- auth.ts              # Cognito PKCE auth service
|   |   `-- auth-interceptor.ts  # Bearer token attachment
|   `-- src/environments/        # API and Cognito frontend config
`-- AwsRagChat/
    |-- AwsRagChat.Api/          # Controllers, auth, CORS, Swagger, health
    |-- AwsRagChat.Application/  # DTOs, interfaces, chat/retrieval/conversation services
    |-- AwsRagChat.Domain/       # Core entities
    |-- AwsRagChat.Infrastructure/ # AWS, OpenSearch, DynamoDB, S3, Bedrock integrations
    `-- AwsRagChat.Ingestion/    # Lambda handlers and ingestion services
```

## Frontend Flow

1. `Auth` restores a valid Cognito session or processes the OAuth callback.
2. `App` loads conversations and documents only after authentication is ready.
3. Upload success stores the returned document id, refreshes the document list, and polls document status until `INDEXED` or `FAILED`.
4. Users can ask against the selected document or enable global search.
5. If no conversation is active, the frontend creates one before sending the question.
6. User messages render optimistically; after the answer returns, canonical server messages are reloaded to prevent duplicates or stale ids.
7. Conversation switching uses request ids so late message loads cannot replace the active chat.
8. Delete uses a confirmation modal, removes the chat immediately, then reconciles with the API.

## Backend Flow

1. ASP.NET Core validates Cognito access tokens and requires `token_use=access`.
2. Uploads are size/type checked, hashed for duplicate detection, written to S3, and recorded in DynamoDB.
3. `GET /api/Documents` and `GET /api/Documents/{documentId}` expose authenticated document metadata for UI status and selection.
4. Chat requests validate session ownership, document ownership, document status, and retrieval mode before calling RAG.
5. Conversation reads use strongly consistent DynamoDB reads to reduce blank or stale chats after create/delete/switch.
6. Chat persists both user and assistant messages, updates the session title/count/timestamps, and returns grounded citations.

## Upload And Indexing Flow

1. API uploads to `uploads/{ownerUserId}/{documentId}/{fileName}`.
2. S3 invokes `S3DocumentIngestionFunction`.
3. The function extracts owner/document ids from the key and marks document status through the pipeline.
4. Direct extraction handles `.txt`, `.csv`, and text PDFs; scanned PDFs/images use Textract.
5. Text is chunked, embedded with Bedrock, saved to DynamoDB, and indexed into OpenSearch.
6. OpenSearch indexing uses deterministic chunk ids and waits for refresh so newly indexed chunks become searchable more predictably.
7. Final status becomes `INDEXED` with `ChunkCount` and `PageCount`, or `FAILED` with an error message.

## Page Count Metadata Flow

Page count is treated as document metadata, not inferred from retrieved text. Direct PDF extraction records the actual PDF page count from PdfPig, plain text/CSV records one page, and Textract-based image/PDF extraction records the page count from Textract `PAGE` blocks. Ingestion writes `PageCount` to the document metadata record when OCR completes and again when indexing completes.

Questions such as `How many pages does this document contain?`, `total pages`, and `page count` route to document metadata. In single-document mode the assistant answers directly, for example `This document contains 12 pages.` In global search mode it returns a table of page counts for uploaded documents. Older records created before this metadata existed may show page count as unavailable until re-indexed.

## Retrieval Modes

Global Search OFF:

- `documentId` is required.
- The backend verifies the document belongs to the authenticated user.
- The document must be `INDEXED` and have at least one chunk.
- Retrieval is scoped to `OwnerUserId + DocumentId`.

Global Search ON:

- `documentId` is ignored.
- Retrieval is scoped to `OwnerUserId` only.
- The assistant may answer from any indexed document owned by the user.

The system never intentionally mixes documents in single-document mode.

## RAG Retrieval Flow

1. Recent conversation turns are used only to clarify follow-up wording for embedding.
2. Bedrock generates the query embedding.
3. OpenSearch vector search runs with strict owner and optional document filters.
4. Filters tolerate keyword/text mapping drift for `ownerUserId` and `documentId`.
5. The retriever loads persisted chunks from DynamoDB and hybrid-ranks them with vector similarity, keyword phrase matches, section-heading matches, and an OpenSearch-hit boost.
6. Heading matches are intentionally strong so queries such as `RESULTS AND DISCUSSION`, `Explain results`, and `Tell about results section` surface chunks headed `V. RESULTS AND DISCUSSION`.
7. Bedrock receives only the final ranked chunks as factual context.
8. Citations are built from the top distinct retrieved chunks.

## Chunking Strategy

The ingestion pipeline uses heading-aware semantic chunking:

- Extracted text is normalized to remove duplicate whitespace, repair hyphenated line breaks, and preserve line/paragraph boundaries.
- PDF text is reconstructed from positioned words where possible, which preserves spaces and reading order better than raw PDF text.
- Textract line output is normalized before chunking.
- Section headings such as `V. RESULTS AND DISCUSSION` are detected and stored as `Heading` and `Section`.
- Chunks target roughly 500-1200 characters with about 180 characters of overlap.
- A heading is kept with its following paragraph so section-title questions retrieve the actual section content, not a detached title.
- Each chunk stores `PageNumber`, `Heading`, `Section`, `ChunkOrder`, file name, source key, and embedding.

## Embedding And Indexing Quality

- Bedrock Titan embeddings are validated as non-empty.
- Document and query embeddings are L2-normalized before storage/search to keep cosine-style ranking consistent.
- Chunks with corrupted or mismatched embedding dimensions do not receive vector score during hybrid reranking.
- OpenSearch indexing uses deterministic ids shaped from user, document, and chunk ids.
- Index writes wait for refresh, reducing the gap where a just-indexed document is marked ready but not searchable yet.

## Bedrock Prompting Flow

The prompt instructs the model to:

- Use only retrieved context as factual evidence.
- Use conversation history only for follow-up references.
- Say it does not know when context is insufficient.
- Avoid invented names, dates, numbers, policies, totals, and citations.
- Use markdown tables only when requested.

No fake AI responses are generated by the frontend.

## Response Type Handling

Chat answers use `responseType` to keep rendering predictable without changing the upload, retrieval, citation, or session flows:

- `text`: normal markdown/text answer.
- `table`: structured table payload in `data.columns` and `data.rows`.
- `pie-chart` / `bar-chart`: chart payload in `chartData.labels` and `chartData.values`.
- `interactive-options`: clickable option cards for content discovery prompts.
- `document-selector`: uploaded document cards for selecting a single retrieval scope.
- `image`, `json`, and `code`: existing specialized responses remain supported.

Assistant messages persist `responseType`, optional `data`, and optional `chartData` with the conversation record so refreshed chats can continue rendering typed responses.

## Chart Rendering Flow

1. `ResponsePlanner` detects explicit chart/statistics requests such as `show in pie chart`, `display as chart`, `statistics format`, `show analytics`, and `bar chart`.
2. Metadata chart requests, including upload status/statistics, are answered from document metadata and return `pie-chart` or `bar-chart`.
3. RAG chart requests still retrieve from the selected document scope; numeric markdown table output is converted into chart data when available, with citation keyword data as a fallback.
4. Angular renders the chart through the chart component based on `responseType` and `chartData`.

## Table Rendering Flow

1. Table requests such as `table format`, `show as table`, and `tabular format` return `responseType = table`.
2. Metadata table requests return structured `columns` and `rows` for uploaded documents.
3. RAG table answers use markdown table prompting and the backend extracts a structured table payload when possible.
4. Angular renders dynamic columns and rows in a responsive enterprise table.

## Interactive Options Flow

Content discovery prompts such as `What content do you have for me?`, `What can I ask?`, and `Show available content` return `interactive-options`.

Semantic trigger handling normalizes punctuation/case and matches grouped intent phrases, so variants such as `What do you have for me?`, `What exactly can you do?`, `What can you help me with?`, `What domains of information do you cover?`, `Show available options`, and `What features do you support?` return the same options UI without enabling general-knowledge routing.

The UI renders clickable cards for:

- Summarize uploaded documents
- Show uploaded documents
- Extract charts/statistics
- Extract images/figures
- Ask questions from documents
- Choose document to fetch required information

Prompt-style options send the matching chat request through the existing ask flow.

## Dynamic Helper Options

The helper chips below the chat input depend on retrieval mode:

- Single-document mode with a selected document shows document-focused prompts for summarization, citations, tables, charts, figure extraction, and selected-document analysis.
- Global search mode shows all-documents prompts for searching across indexed files, comparing documents, finding related content, searching the knowledge base, and retrieving from all indexed files.
- When no document is selected and global search is off, helper prompts are hidden until the user chooses a valid retrieval scope.

## Global Search UI Behavior

When global search is enabled, the uploaded document selector is disabled and visually faded, and the settings panel shows a note that global search is active. When global search is disabled, the selector becomes active again and the default choice remains `Select a document`. Retrieval payloads are unchanged: global search sends `searchAcrossAllDocuments = true`, while single-document mode sends the selected `documentId`.

## Response-Length Handling

Bedrock answer generation now allows longer grounded responses with `max_new_tokens = 4096`, and the prompt asks the model to match the requested level of detail rather than always being concise. Frontend rendering does not clip markdown answers. Chunking, embeddings, retrieval top-k, and indexing architecture are unchanged.

## Document Selection Interaction Flow

The `Choose document to fetch required information` option opens a `document-selector` response in chat. Uploaded documents are shown as clickable cards with status and chunk count. Selecting a document updates the existing selected document state, turns off global search, highlights the chosen document, and future questions use that document id so retrieval is scoped to that document only.

## Figure Extraction Flow

Figure/image requests such as `Extract figures from this document`, `Show figures`, `Extract images`, and `Get diagrams from this file` route to the image response flow instead of normal RAG retrieval. This prevents image-heavy, scanned, failed, or zero-chunk PDFs from failing document-search validation.

Supported behavior:

- Uploaded image files (`png`, `jpg`, `jpeg`, `tif`, `tiff`) are returned as image assets with temporary S3 read URLs and rendered in the chat.
- Image-heavy or non-searchable PDFs return a safe PDF preview card so users can open the source document and inspect figures without breaking the chat.
- Text PDFs or documents without available image assets return `No extractable figures or images were found in this document.`

Fallback handling is defensive: document-scope lookup and preview URL creation are caught inside the image route, logged as warnings, and converted into a meaningful assistant response instead of an internal server error or frontend `Question failed` state.

## Conversation Flow

1. Conversations are created with `POST /api/Conversations`.
2. Sessions are listed with `GET /api/Conversations`.
3. Messages are loaded with `GET /api/Conversations/{sessionId}/messages`.
4. The frontend caches messages per session for fast switching, then refreshes from the server.
5. Asking reloads canonical server messages after the assistant response so old chats stay consistent after refresh.
6. Deleting a session removes the session and all message items from DynamoDB.

## Stabilization Fixes Made

- Added document list/status APIs and frontend document status polling.
- Fixed missing `IDocumentRepository.GetDocumentsByUserAsync` contract.
- Enforced document ownership, `INDEXED` status, and positive chunk count before single-document RAG.
- Removed unsafe debug vector endpoint and console debug tracing.
- Added structured retrieval/chat/OpenSearch logging.
- Made OpenSearch chunk indexing deterministic and refresh-aware.
- Added heading-aware semantic chunking, extraction cleanup, embedding normalization, and hybrid retrieval scoring.
- Added DynamoDB cosine-similarity fallback and hybrid reranking when OpenSearch is empty or temporarily unavailable.
- Reworked ask flow to reconcile optimistic UI with canonical server messages.
- Removed stale optimistic user messages on failed asks.
- Hardened conversation deletion and sidebar refresh behavior.
- Fixed the main viewport height from `150dvh` to a stable `100dvh` desktop layout with mobile scrolling.
- Added document picker/status display in chat settings.

## Environment Setup

Required tools:

- Node.js and npm
- Angular CLI compatible with Angular 21
- .NET SDK 8 or newer
- AWS credentials available through the default AWS credential chain
- Access to configured Cognito, S3, DynamoDB, Textract, Bedrock, and OpenSearch resources

Important backend config:

- `AWS:Region`
- `S3:BucketName`
- `DynamoDb:TableName`
- `DynamoDb:DocumentsTableName`
- `Conversations:TableName`
- `Bedrock:EmbeddingModelId`
- `Bedrock:ChatModelId`
- `Cognito:Authority`
- `Cognito:AppClientId`
- `Cors:AllowedOrigins`
- `OpenSearch:Endpoint`
- `OpenSearch:IndexName`

## Run Instructions

Backend:

```powershell
cd AwsRagChat
dotnet restore AwsRagChat.slnx
dotnet run --project AwsRagChat.Api
```

Frontend:

```powershell
cd aws-rag-chat-ui
npm install
npm run start
```

Default local URLs:

- Frontend: `http://localhost:4200`
- API: `http://localhost:5070`
- Health check: `http://localhost:5070/health`

Build verification:

```powershell
dotnet build AwsRagChat\AwsRagChat.Api\AwsRagChat.Api.csproj
cd aws-rag-chat-ui
npm run build
```

## Known Limitations

- Access tokens are not silently refreshed; users must log in again after token expiry.
- OpenSearch index creation/mapping is expected to exist before ingestion.
- DynamoDB fallback retrieval is a reliability fallback, not the primary path; very large corpora should rely on healthy OpenSearch.
- New typed assistant messages persist table/chart/interactive payloads; older historical messages created before this change may still render as text-only.
- Very large documents may require longer Lambda timeouts and Bedrock throttling controls.

## Troubleshooting

- `401 Unauthorized`: confirm the frontend sends a Cognito access token and `Cognito:AppClientId` matches token `client_id`.
- CORS errors: add the exact frontend origin to `Cors:AllowedOrigins`.
- Upload succeeds but asking says the document is not searchable: wait until document status is `INDEXED`; check Lambda, Textract, Bedrock, DynamoDB, and OpenSearch logs if it remains stuck.
- `INDEXED` with zero chunks: the file likely had no extractable text or extraction failed before chunk creation.
- Single-document answers cite other files: confirm Global Search is off and the selected document id is correct.
- Global search misses content: check OpenSearch health, vector dimension, data access policy, and DynamoDB chunk fallback logs.
- Exact headings are missed: check ingestion logs for chunk previews and confirm the heading appears in `Heading`, `Section`, and chunk `Text`.
- The model says it does not know while citations look relevant: inspect `Hybrid retrieval hit` logs for the top ranked chunks and verify the prompt context contains the expected section.
- Poor vector quality: verify document/query embedding dimensions match and that normalized embeddings are stored in DynamoDB/OpenSearch for newly ingested documents.
- Scanned PDFs stuck at `OCR_STARTED`: verify Textract SNS/SQS wiring and `TextractCompletionFunction`.
- Local API build cannot copy DLLs: stop the running `AwsRagChat.Api` process or build to a separate output folder with `dotnet build -o .\artifacts\api-build-check`.
