using System.Security.Claims;

namespace AwsRagChat.Api.Security;

public static class ClaimsPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? principal.FindFirstValue("sub");
    }

    public static string GetEmail(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Email)
               ?? principal.FindFirstValue("email")
               ?? string.Empty;
    }

    public static string? GetFirstRole(this ClaimsPrincipal principal)
    {
        return principal.FindAll("cognito:groups")
                   .Select(claim => claim.Value)
                   .FirstOrDefault()
               ?? principal.FindAll(ClaimTypes.Role)
                   .Select(claim => claim.Value)
                   .FirstOrDefault();
    }

    public static string GetRequiredUserId(this ClaimsPrincipal principal)
    {
        var userId = principal.GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("User identity could not be resolved from token.");

        return userId;
    }
}
