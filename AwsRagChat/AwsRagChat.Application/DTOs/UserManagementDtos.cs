namespace AwsRagChat.Application.DTOs;

public sealed class EnterpriseUserDto
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public string ApprovedRole { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
}

public sealed class ApproveUserRequest
{
    public string Role { get; set; } = string.Empty;
}

public sealed class UserAccessResult
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public string ApprovedRole { get; set; } = string.Empty;
    public bool IsApproved { get; set; }
}
