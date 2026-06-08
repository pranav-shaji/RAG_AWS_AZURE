using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Extensions.Options;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Infrastructure.Options;

namespace AwsRagChat.Infrastructure.Services;

public sealed class EntraUserRoleService : IUserRoleService
{
    private readonly GraphServiceClient _graphClient;
    private readonly EntraIdOptions _options;

    public EntraUserRoleService(IOptions<EntraIdOptions> options)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.TenantId) || string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new InvalidOperationException("Microsoft Entra ID TenantId and ClientId must be configured.");
        }

        TokenCredential credential;
        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            credential = new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
        }
        else
        {
            credential = new DefaultAzureCredential();
        }

        _graphClient = new GraphServiceClient(credential);
    }

    private async Task<string> ResolveObjectIdAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User identifier is required.", nameof(userId));

        if (!userId.Contains('@'))
        {
            return userId; // Already a GUID/Object ID
        }

        // It is a UPN/email, look it up to resolve the unique GUID Object ID
        var user = await _graphClient.Users[userId].GetAsync(
            requestConfig => requestConfig.QueryParameters.Select = new[] { "id" },
            cancellationToken);

        if (user == null || string.IsNullOrWhiteSpace(user.Id))
        {
            throw new InvalidOperationException($"Could not resolve Microsoft Entra ID Object ID for UPN: '{userId}'");
        }

        return user.Id;
    }

    public async Task<string?> GetUserRoleAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        try
        {
            var resolvedId = await ResolveObjectIdAsync(userId, cancellationToken);
            var memberOf = await _graphClient.Users[resolvedId].MemberOf.GetAsync(cancellationToken: cancellationToken);

            if (memberOf?.Value == null)
                return null;

            foreach (var directoryObject in memberOf.Value)
            {
                var groupId = directoryObject.Id;
                if (string.IsNullOrEmpty(groupId))
                    continue;

                var matchedRole = _options.GroupMappings
                    .FirstOrDefault(x => string.Equals(x.Value, groupId, StringComparison.OrdinalIgnoreCase))
                    .Key;

                if (!string.IsNullOrEmpty(matchedRole))
                {
                    return matchedRole;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task AssignRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (!EnterpriseRoles.IsValid(role))
            throw new InvalidOperationException($"Invalid role: {role}");

        var normalizedRole = EnterpriseRoles.Normalize(role);

        if (!_options.GroupMappings.TryGetValue(normalizedRole, out var groupId) || string.IsNullOrWhiteSpace(groupId))
        {
            throw new InvalidOperationException($"No Entra ID Group ID mapping configured for role: {normalizedRole}");
        }

        var resolvedId = await ResolveObjectIdAsync(userId, cancellationToken);
        var existingRole = await GetUserRoleAsync(resolvedId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingRole))
        {
            throw new InvalidOperationException("User role already assigned.");
        }

        var requestBody = new ReferenceCreate
        {
            OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{resolvedId}"
        };

        await _graphClient.Groups[groupId].Members.Ref.PostAsync(requestBody, cancellationToken: cancellationToken);
    }

    public async Task SetRoleAsync(
        string userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (!EnterpriseRoles.IsValid(role))
            throw new InvalidOperationException($"Invalid role: {role}");

        var normalizedRole = EnterpriseRoles.Normalize(role);

        if (!_options.GroupMappings.TryGetValue(normalizedRole, out var targetGroupId) || string.IsNullOrWhiteSpace(targetGroupId))
        {
            throw new InvalidOperationException($"No Entra ID Group ID mapping configured for role: {normalizedRole}");
        }

        var resolvedId = await ResolveObjectIdAsync(userId, cancellationToken);

        // Fetch current group memberships
        var memberOf = await _graphClient.Users[resolvedId].MemberOf.GetAsync(cancellationToken: cancellationToken);
        var currentGroupIds = memberOf?.Value?.Select(x => x.Id).Where(id => id != null).Select(id => id!).ToList() ?? new List<string>();

        // Remove user from other mapped groups
        foreach (var mapping in _options.GroupMappings)
        {
            var configuredGroupId = mapping.Value;
            if (string.Equals(configuredGroupId, targetGroupId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (currentGroupIds.Any(id => string.Equals(id, configuredGroupId, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await _graphClient.Groups[configuredGroupId].Members[resolvedId].Ref.DeleteAsync(cancellationToken: cancellationToken);
                }
                catch (Exception)
                {
                    // Ignore removal failures to ensure robust operation flow
                }
            }
        }

        // Add user to the target group if not already a member
        if (!currentGroupIds.Any(id => string.Equals(id, targetGroupId, StringComparison.OrdinalIgnoreCase)))
        {
            var requestBody = new ReferenceCreate
            {
                OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{resolvedId}"
            };

            await _graphClient.Groups[targetGroupId].Members.Ref.PostAsync(requestBody, cancellationToken: cancellationToken);
        }
    }

    public async Task<string?> GetUserEmailAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        if (userId.Contains('@'))
        {
            return userId; // Already UPN / email format
        }

        try
        {
            var user = await _graphClient.Users[userId].GetAsync(
                requestConfig => requestConfig.QueryParameters.Select = new[] { "mail", "userPrincipalName" },
                cancellationToken);

            return user?.Mail ?? user?.UserPrincipalName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
