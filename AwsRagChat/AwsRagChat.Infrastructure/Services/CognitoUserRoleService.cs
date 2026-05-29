using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AwsRagChat.Infrastructure.Services;

public sealed class CognitoUserRoleService : IUserRoleService
{
    private readonly IAmazonCognitoIdentityProvider _cognito;
    private readonly CognitoOptions _options;

    public CognitoUserRoleService(
        IAmazonCognitoIdentityProvider cognito,
        IOptions<CognitoOptions> options)
    {
        _cognito = cognito;
        _options = options.Value;
    }

    public async Task<string?> GetUserRoleAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var response = await _cognito.AdminListGroupsForUserAsync(
            new AdminListGroupsForUserRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = userId
            },
            cancellationToken);

        return response.Groups
            .Select(x => x.GroupName)
            .FirstOrDefault(EnterpriseRoles.IsValid);
    }

    public async Task AssignRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (!EnterpriseRoles.IsValid(role))
            throw new InvalidOperationException($"Invalid role: {role}");

        role = EnterpriseRoles.Normalize(role);

        var existingRole = await GetUserRoleAsync(
            userId,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(existingRole))
            throw new InvalidOperationException("User role already assigned.");

        await _cognito.AdminAddUserToGroupAsync(
            new AdminAddUserToGroupRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = userId,
                GroupName = role
            },
            cancellationToken);
    }

    public async Task SetRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (!EnterpriseRoles.IsValid(role))
            throw new InvalidOperationException($"Invalid role: {role}");

        role = EnterpriseRoles.Normalize(role);

        var response = await _cognito.AdminListGroupsForUserAsync(
            new AdminListGroupsForUserRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = userId
            },
            cancellationToken);

        foreach (var group in response.Groups
                     .Select(x => x.GroupName)
                     .Where(groupName => EnterpriseRoles.IsValid(groupName) &&
                                         !string.Equals(groupName, role, StringComparison.OrdinalIgnoreCase)))
        {
            await _cognito.AdminRemoveUserFromGroupAsync(
                new AdminRemoveUserFromGroupRequest
                {
                    UserPoolId = _options.UserPoolId,
                    Username = userId,
                    GroupName = group
                },
                cancellationToken);
        }

        if (!response.Groups.Any(group =>
                string.Equals(group.GroupName, role, StringComparison.OrdinalIgnoreCase)))
        {
            await _cognito.AdminAddUserToGroupAsync(
                new AdminAddUserToGroupRequest
                {
                    UserPoolId = _options.UserPoolId,
                    Username = userId,
                    GroupName = role
                },
                cancellationToken);
        }

        await _cognito.AdminUpdateUserAttributesAsync(
            new AdminUpdateUserAttributesRequest
            {
                UserPoolId = _options.UserPoolId,
                Username = userId,
                UserAttributes =
                [
                    new AttributeType
                    {
                        Name = "custom:approvalStatus",
                        Value = ApprovalStatuses.Approved
                    },
                    new AttributeType
                    {
                        Name = "custom:approvedRole",
                        Value = role
                    }
                ]
            },
            cancellationToken);
    }

    public async Task<string?> GetUserEmailAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        try
        {
            var bySub = await _cognito.ListUsersAsync(
                new ListUsersRequest
                {
                    UserPoolId = _options.UserPoolId,
                    Filter = $"sub = \"{userId}\"",
                    Limit = 1
                },
                cancellationToken);

            var email = bySub.Users
                .SelectMany(user => user.Attributes)
                .FirstOrDefault(attribute => attribute.Name == "email")
                ?.Value;

            if (!string.IsNullOrWhiteSpace(email))
                return email;
        }
        catch (Exception)
        {
            // Fall through to AdminGetUser; some pools only accept username lookups.
        }

        try
        {
            var response = await _cognito.AdminGetUserAsync(
                new AdminGetUserRequest
                {
                    UserPoolId = _options.UserPoolId,
                    Username = userId
                },
                cancellationToken);

            return response.UserAttributes
                .FirstOrDefault(attribute => attribute.Name == "email")
                ?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
