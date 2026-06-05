using AwsRagChat.Api.Security;
using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AwsRagChat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class UserRoleController : ControllerBase
{
    private readonly IUserRoleService _userRoleService;

    public UserRoleController(
        IUserRoleService userRoleService)
    {
        _userRoleService = userRoleService;
    }

    [HttpGet]
    public ActionResult<UserRoleResponse> GetCurrentUserRole()
    {
        var userId = User.GetRequiredUserId();

        // Access token usually does not contain email.
        var email =
            User.FindFirst("email")?.Value
            ?? string.Empty;

        // Read role directly from JWT claims.
        var role =
            User.Claims
                .Where(x => x.Type == IdentityOptions.GroupsClaimType)
                .Select(x => x.Value)
                .FirstOrDefault()
            ?? User.Claims
                .Where(x => x.Type == "cognito:groups")
                .Select(x => x.Value)
                .FirstOrDefault()
            ?? string.Empty;

        return Ok(new UserRoleResponse
        {
            UserId = userId,
            Email = email,
            Role = role,
            RoleAssigned = !string.IsNullOrWhiteSpace(role)
        });
    }

    [HttpPost("assign")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserRoleResponse>> AssignRole(
        [FromBody] AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest("Request is required.");

        if (string.IsNullOrWhiteSpace(request.Role))
            return BadRequest("Role is required.");

        if (!EnterpriseRoles.IsValid(request.Role))
        {
            return BadRequest(
                "Invalid role.");
        }

        var normalizedRole = EnterpriseRoles.Normalize(request.Role);

        var userId =
            User.GetRequiredUserId();

        var email =
            User.FindFirst("email")?.Value
            ?? string.Empty;

        var existingRole =
            User.Claims
                .Where(x => x.Type == IdentityOptions.GroupsClaimType)
                .Select(x => x.Value)
                .FirstOrDefault()
            ?? User.Claims
                .Where(x => x.Type == "cognito:groups")
                .Select(x => x.Value)
                .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(existingRole))
        {
            return Conflict(
                "Role already assigned.");
        }

        await _userRoleService.AssignRoleAsync(
             userId,
             normalizedRole,
             cancellationToken);

        return Ok(new UserRoleResponse
        {
            UserId = userId,
            Email = email,
            Role = normalizedRole,
            RoleAssigned = true
        });
    }
}
