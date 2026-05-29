namespace AwsRagChat.Application.Interfaces;

public interface IUserRoleService
{
    Task<string?> GetUserRoleAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task AssignRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default);

    Task SetRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default);

    Task<string?> GetUserEmailAsync(
        string userId,
        CancellationToken cancellationToken = default);
}
