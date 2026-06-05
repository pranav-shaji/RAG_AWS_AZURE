using AwsRagChat.Application.DTOs;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using Microsoft.Extensions.Logging;

namespace AwsRagChat.Application.Services;

public sealed class UserApprovalService : IUserApprovalService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserRoleService _userRoleService;
    private readonly ILogger<UserApprovalService> _logger;

    public UserApprovalService(
        IUserRepository userRepository,
        IUserRoleService userRoleService,
        ILogger<UserApprovalService> logger)
    {
        _userRepository = userRepository;
        _userRoleService = userRoleService;
        _logger = logger;
    }

    public async Task RegisterPendingUserAsync(
        string userId,
        string email,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await _userRepository.UpsertAsync(
            new EnterpriseUserDto
            {
                UserId = userId,
                Email = email,
                ApprovalStatus = ApprovalStatuses.Pending,
                ApprovedRole = string.Empty,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            cancellationToken);
    }

    public async Task<UserAccessResult> ResolveAccessAsync(
        string userId,
        string email,
        string? claimedRole,
        CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByUserIdAsync(
            userId,
            cancellationToken);

        if (user is null)
        {
            user = BuildInitialRecord(userId, email, claimedRole);

            await _userRepository.UpsertAsync(
                user,
                cancellationToken);
        }

        var approvedRole = EnterpriseRoles.IsValid(user.ApprovedRole)
            ? EnterpriseRoles.Normalize(user.ApprovedRole)
            : string.Empty;

        return new UserAccessResult
        {
            UserId = user.UserId,
            Email = user.Email,
            ApprovalStatus = user.ApprovalStatus,
            ApprovedRole = approvedRole,
            IsApproved = ApprovalStatuses.IsApproved(user.ApprovalStatus) &&
                         EnterpriseRoles.IsValid(approvedRole)
        };
    }

    public async Task<IReadOnlyList<EnterpriseUserDto>> GetUsersAsync(
        CancellationToken cancellationToken = default)
    {
        var users = (await _userRepository.GetAllAsync(cancellationToken))
            .ToList();

        foreach (var user in users.Where(user => string.IsNullOrWhiteSpace(user.Email)))
        {
            var email = await _userRoleService.GetUserEmailAsync(
                user.UserId,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(email))
                continue;

            user.Email = email;
            user.UpdatedAtUtc = DateTime.UtcNow;

            await _userRepository.UpsertAsync(
                user,
                cancellationToken);
        }

        return users;
    }

    public async Task<EnterpriseUserDto> ApproveUserAsync(
        string userId,
        string role,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedRole = EnterpriseRoles.Normalize(role);
        var user = await _userRepository.GetByUserIdAsync(
            userId,
            cancellationToken);

        if (user is null)
            throw new KeyNotFoundException("User was not found.");

        var identityUsername = string.IsNullOrWhiteSpace(user.Email)
            ? user.UserId
            : user.Email;

        await _userRoleService.SetRoleAsync(
            identityUsername,
            normalizedRole,
            cancellationToken);

        var now = DateTime.UtcNow;

        user.ApprovalStatus = ApprovalStatuses.Approved;
        user.ApprovedRole = normalizedRole;
        user.ApprovedBy = approvedBy;
        user.ApprovedAtUtc = now;
        user.UpdatedAtUtc = now;

        await _userRepository.UpsertAsync(
            user,
            cancellationToken);

        _logger.LogInformation(
            "Enterprise user approved. UserId={UserId}, Role={Role}, ApprovedBy={ApprovedBy}",
            userId,
            normalizedRole,
            approvedBy);

        return user;
    }

    private static EnterpriseUserDto BuildInitialRecord(
        string userId,
        string email,
        string? claimedRole)
    {
        var now = DateTime.UtcNow;
        var hasValidClaimedRole = EnterpriseRoles.IsValid(claimedRole);
        var role = hasValidClaimedRole
            ? EnterpriseRoles.Normalize(claimedRole!)
            : string.Empty;

        return new EnterpriseUserDto
        {
            UserId = userId,
            Email = email,
            ApprovalStatus = hasValidClaimedRole
                ? ApprovalStatuses.Approved
                : ApprovalStatuses.Pending,
            ApprovedRole = role,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }
}
