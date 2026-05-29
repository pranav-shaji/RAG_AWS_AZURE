namespace AwsRagChat.Infrastructure.Options;

public sealed class CognitoOptions
{
    public const string SectionName = "Cognito";

    public string UserPoolId { get; set; } = string.Empty;

    public string AppClientId { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;
}