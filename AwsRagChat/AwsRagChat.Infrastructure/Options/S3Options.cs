namespace AwsRagChat.Infrastructure.Options;

public sealed class S3Options
{
    public const string SectionName = "S3";

    public string BucketName { get; set; } = string.Empty;
}