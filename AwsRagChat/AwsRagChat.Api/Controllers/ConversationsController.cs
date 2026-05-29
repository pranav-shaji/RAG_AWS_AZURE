using AwsRagChat.Api.Security;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AwsRagChat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ConversationsController : ControllerBase
{
    private readonly ConversationService _conversationService;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        ConversationService conversationService,
        ILogger<ConversationsController> logger)
    {
        _conversationService = conversationService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<ConversationSessionDto>> Create(
        [FromBody] CreateConversationRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var result = await _conversationService.CreateSessionAsync(
                userId,
                request?.Title,
                cancellationToken);

            return Ok(result);
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
            _logger.LogError(ex, "Failed to create conversation for user {UserId}.", userId);
            return Problem("The conversation could not be created right now.");
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationSessionDto>>> List(
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var result = await _conversationService.GetSessionsAsync(userId, cancellationToken);

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list conversations for user {UserId}.", userId);
            return Problem("Conversations could not be loaded right now.");
        }
    }

    [HttpGet("{sessionId}")]
    public async Task<ActionResult<ConversationSessionDto>> Get(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest("SessionId is required.");

        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var result = await _conversationService.GetSessionAsync(userId, sessionId, cancellationToken);

            if (result is null)
                return NotFound("Conversation session was not found.");

            return Ok(result);
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
            _logger.LogError(
                ex,
                "Failed to load conversation {SessionId} for user {UserId}.",
                sessionId,
                userId);

            return Problem("The conversation could not be loaded right now.");
        }
    }

    [HttpGet("{sessionId}/messages")]
    public async Task<ActionResult<IReadOnlyList<ConversationMessageDto>>> GetMessages(
        string sessionId,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest("SessionId is required.");

        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        var normalizedTake = Math.Clamp(take, 1, 200);

        try
        {
            var result = await _conversationService.GetMessagesAsync(
                userId,
                sessionId,
                normalizedTake,
                cancellationToken);

            if (result.Count == 0)
            {
                var session = await _conversationService.GetSessionAsync(userId, sessionId, cancellationToken);

                if (session is null)
                    return NotFound("Conversation session was not found.");
            }

            return Ok(result);
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
            _logger.LogError(
                ex,
                "Failed to load messages for conversation {SessionId} and user {UserId}.",
                sessionId,
                userId);

            return Problem("Conversation messages could not be loaded right now.");
        }
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return BadRequest("SessionId is required.");

        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var deleted = await _conversationService.DeleteSessionAsync(
                userId,
                sessionId,
                cancellationToken);

            if (!deleted)
                return NotFound("Conversation session was not found.");

            return NoContent();
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
            _logger.LogError(
                ex,
                "Failed to delete conversation {SessionId} for user {UserId}.",
                sessionId,
                userId);

            return Problem("The conversation could not be deleted right now.");
        }
    }
}
