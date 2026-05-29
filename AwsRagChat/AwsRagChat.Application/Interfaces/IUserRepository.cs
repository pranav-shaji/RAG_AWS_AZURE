using AwsRagChat.Application.DTOs;

namespace AwsRagChat.Application.Interfaces;

public interface IUserRepository
{
    Task<EnterpriseUserDto?> GetByUserIdAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnterpriseUserDto>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        EnterpriseUserDto user,
        CancellationToken cancellationToken = default);
}

public interface IUserApprovalService
{
    Task RegisterPendingUserAsync(
        string userId,
        string email,
        CancellationToken cancellationToken = default);

    Task<UserAccessResult> ResolveAccessAsync(
        string userId,
        string email,
        string? claimedRole,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnterpriseUserDto>> GetUsersAsync(
        CancellationToken cancellationToken = default);

    Task<EnterpriseUserDto> ApproveUserAsync(
        string userId,
        string role,
        string approvedBy,
        CancellationToken cancellationToken = default);
}
