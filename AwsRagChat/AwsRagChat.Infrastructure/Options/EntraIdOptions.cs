using System.Collections.Generic;

namespace AwsRagChat.Infrastructure.Options;

public sealed class EntraIdOptions
{
    public const string SectionName = "EntraId";

    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string Authority { get; set; } = string.Empty;

    public Dictionary<string, string> GroupMappings { get; set; } = new();
}
