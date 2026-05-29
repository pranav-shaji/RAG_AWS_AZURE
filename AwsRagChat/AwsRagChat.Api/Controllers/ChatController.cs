using AwsRagChat.Api.Security;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AwsRagChat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ChatController : ControllerBase
{
    private readonly ChatService _chatService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        ChatService chatService,
        ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    [HttpPost("ask")]
    public async Task<ActionResult<ChatAskResponse>> Ask(
        [FromBody] ChatAskRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest("Request body is required.");

        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest("SessionId is required.");

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest("Question is required.");

        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("User identity could not be resolved from token.");

        try
        {
            var result = await _chatService.AskAsync(
                userId,
                User.GetEmail(),
                User.GetFirstRole(),
                request,
                cancellationToken);

            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound("Conversation session was not found.");
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
                "Failed to answer chat question for user {UserId} and session {SessionId}.",
                userId,
                request.SessionId);

            return Problem(
                title: "Chat request failed.",
                detail: "The question could not be processed right now.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

}
