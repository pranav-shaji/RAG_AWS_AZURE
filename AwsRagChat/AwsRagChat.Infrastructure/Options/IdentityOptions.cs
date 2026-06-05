namespace AwsRagChat.Infrastructure.Options;

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    public static string GroupsClaimType { get; set; } = "cognito:groups";

    public string Provider { get; set; } = "AWS";

    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string GroupsClaim
    {
        get => GroupsClaimType;
        set => GroupsClaimType = value;
    }
}
