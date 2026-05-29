namespace AwsRagChat.Application.DTOs;

public sealed class AssignRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

public sealed class UserRoleResponse
{
    public string UserId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public bool RoleAssigned { get; set; }
}