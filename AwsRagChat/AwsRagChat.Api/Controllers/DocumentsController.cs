using AwsRagChat.Api.Security;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace AwsRagChat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class DocumentsController : ControllerBase
{
    private const long MaxUploadBytes = 25L * 1024L * 1024L;

    private static readonly HashSet<string> SupportedExtensions =
    [
        ".txt",
        ".csv",
        ".pdf",
        ".png",
        ".jpg",
        ".jpeg",
        ".tif",
        ".tiff"
    ];

    private readonly IStorageService _storageService;
    private readonly IDocumentRepository _documentRepository;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IStorageService storageService,
        IDocumentRepository documentRepository,
        ILogger<DocumentsController> logger)
    {
        _storageService = storageService;
        _documentRepository = documentRepository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExistingDocumentRecord>>> List(
    CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var documents =
                await _documentRepository.GetAdminDocumentsAsync(
                    cancellationToken);

            return Ok(documents
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ToList());
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to list documents for user {UserId}.",
                userId);

            return Problem("Documents could not be loaded right now.");
        }
    }

    [HttpGet("{documentId}")]
    public async Task<ActionResult<ExistingDocumentRecord>> Get(
        string documentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return BadRequest("DocumentId is required.");

        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var document = await _documentRepository.GetDocumentByIdAsync(
                documentId,
                cancellationToken);

            if (document is null || !document.IsAdminDocument)
            {
                throw new ArgumentException("Document not found.");
            }

            return Ok(document);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load document {DocumentId} for user {UserId}.",
                documentId,
                userId);

            return Problem("The document could not be loaded right now.");
        }
    }

    [HttpPost("upload")]
    [Authorize(Roles = "Admin")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<UploadDocumentResponse>> Upload(
        IFormFile file,
        [FromForm] List<string> allowedRoles,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest("File is required.");

        if (file.Length > MaxUploadBytes)
            return BadRequest("File size exceeds the 25 MB upload limit.");

        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        var normalizedAllowedRoles = NormalizeAllowedRoles(allowedRoles);

        if (normalizedAllowedRoles.Count == 0)
            return BadRequest("Select at least one valid document visibility role.");

        var safeFileName = Path.GetFileName(file.FileName);

        if (string.IsNullOrWhiteSpace(safeFileName))
            return BadRequest("File name is required.");

        var extension = Path.GetExtension(safeFileName).ToLowerInvariant();

        if (!SupportedExtensions.Contains(extension))
            return BadRequest("Unsupported file type. Upload PDF, text, CSV, or image files supported by Textract.");

        try
        {
            await using var uploadStream = file.OpenReadStream();
            await using var memoryStream = new MemoryStream();

            await uploadStream.CopyToAsync(memoryStream, cancellationToken);

            var fileHash = ComputeSha256Hash(memoryStream);

            var existingDocument = await _documentRepository.FindByOwnerAndHashAsync(
                userId,
                fileHash,
                cancellationToken);

            if (existingDocument != null)
            {
                return Ok(new UploadDocumentResponse
                {
                    DocumentId = existingDocument.DocumentId,
                    ExistingDocumentId = existingDocument.DocumentId,
                    FileName = existingDocument.FileName,
                    StorageKey = existingDocument.StorageKey,
                    IsDuplicate = true,
                    Status = existingDocument.Status,
                    Message = "This file already exists. You can continue querying it."
                });
            }

            var documentId = Guid.NewGuid().ToString();
            var key = $"uploads/{userId}/{documentId}/{safeFileName}";

            memoryStream.Position = 0;

            var savedKey = await _storageService.UploadAsync(
                memoryStream,
                key,
                cancellationToken);

            await _documentRepository.CreateUploadRecordAsync(
                documentId,
                userId,
                safeFileName,
                savedKey,
                fileHash,
                file.Length,
                isAdminDocument: true,
                normalizedAllowedRoles,
                cancellationToken);

            return Ok(new UploadDocumentResponse
            {
                DocumentId = documentId,
                FileName = safeFileName,
                StorageKey = savedKey,
                IsDuplicate = false,
                Status = "UPLOADED",
                Message = $"File uploaded successfully by user {userId}. Ingestion will happen asynchronously."
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Document upload failed for user {UserId} and file {FileName}.",
                userId,
                safeFileName);

            return Problem(
                title: "Upload failed.",
                detail: "The document could not be uploaded right now.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string ComputeSha256Hash(Stream stream)
    {
        stream.Position = 0;

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);

        stream.Position = 0;

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static List<string> NormalizeAllowedRoles(IEnumerable<string>? roles)
    {
        return (roles ?? [])
            .SelectMany(role => role.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(EnterpriseRoles.IsValid)
            .Select(EnterpriseRoles.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
