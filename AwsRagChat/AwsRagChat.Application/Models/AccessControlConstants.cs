namespace AwsRagChat.Application.Models;

public static class EnterpriseRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string Manager = "Manager";
    public const string Hr = "HR";

    public static readonly IReadOnlyList<string> All =
    [
        Admin,
        User,
        Manager,
        Hr
    ];

    public static bool IsValid(string? role) =>
        All.Contains(role ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string role)
    {
        var match = All.FirstOrDefault(x =>
            string.Equals(x, role, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(match))
            throw new ArgumentException("Invalid role.", nameof(role));

        return match;
    }
}

public static class ApprovalStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";

    public static bool IsApproved(string? status) =>
        string.Equals(status, Approved, StringComparison.OrdinalIgnoreCase);
}
