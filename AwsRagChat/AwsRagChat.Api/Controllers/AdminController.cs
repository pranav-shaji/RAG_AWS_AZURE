using AwsRagChat.Api.Security;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AwsRagChat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public sealed class AdminController : ControllerBase
{
    private readonly IAdminAnalyticsService _adminAnalyticsService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IStorageService _storageService;
    private readonly IUserApprovalService _userApprovalService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IAdminAnalyticsService adminAnalyticsService,
        IDocumentRepository documentRepository,
        IStorageService storageService,
        IUserApprovalService userApprovalService,
        ILogger<AdminController> logger)
    {
        _adminAnalyticsService = adminAnalyticsService;
        _documentRepository = documentRepository;
        _storageService = storageService;
        _userApprovalService = userApprovalService;
        _logger = logger;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _adminAnalyticsService.GetDashboardStatsAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load admin dashboard analytics.");
            return Problem(detail: ex.ToString(), title: "Admin dashboard analytics could not be loaded.");
        }
    }

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _userApprovalService.GetUsersAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load enterprise users.");
            return Problem("Enterprise users could not be loaded.");
        }
    }

    [HttpPost("users/{userId}/approve")]
    public async Task<IActionResult> ApproveUser(
        string userId,
        [FromBody] ApproveUserRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("UserId is required.");

        if (request is null || string.IsNullOrWhiteSpace(request.Role))
            return BadRequest("Role is required.");

        try
        {
            var approvedUser = await _userApprovalService.ApproveUserAsync(
                userId,
                request.Role,
                User.GetRequiredUserId(),
                cancellationToken);

            return Ok(approvedUser);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("User was not found.");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve enterprise user {UserId}.", userId);
            return Problem("User approval could not be saved.");
        }
    }

    [HttpGet("documents")]
    public async Task<IActionResult> Documents(
        [FromQuery] int pageSize = 20,
        [FromQuery] string? nextToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _adminAnalyticsService.GetDocumentsPageAsync(pageSize, nextToken, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load admin document monitoring.");
            return Problem("Admin document monitoring could not be loaded.");
        }
    }

    [HttpGet("documents/{documentId}/preview-url")]
    public async Task<IActionResult> DocumentPreviewUrl(
        string documentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return BadRequest("DocumentId is required.");

        try
        {
            var document = await _documentRepository.GetDocumentByIdAsync(documentId, cancellationToken);

            if (document is null || !document.IsAdminDocument)
                return NotFound("Document not found.");

            var url = await _storageService.CreateReadUrlAsync(
                document.S3Key,
                TimeSpan.FromMinutes(10),
                cancellationToken);

            return Ok(new { url });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create preview URL for document {DocumentId}.", documentId);
            return Problem("Document preview could not be opened.");
        }
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _adminAnalyticsService.GetRecentConversationsAsync(take, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load admin conversation analytics.");
            return Problem(detail: ex.ToString(), title: "Admin conversation analytics could not be loaded.");
        }
    }

    [HttpGet("ingestion-status")]
    public async Task<IActionResult> IngestionStatus(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _adminAnalyticsService.GetIngestionStatusAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ingestion analytics.");
            return Problem("Ingestion analytics could not be loaded.");
        }
    }
}
