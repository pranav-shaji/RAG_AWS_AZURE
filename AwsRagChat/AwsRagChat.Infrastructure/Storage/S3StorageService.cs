using Amazon.S3;
using Amazon.S3.Model;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AwsRagChat.Infrastructure.Storage;

public sealed class S3StorageService : IStorageProvider
{
    private readonly IAmazonS3 _amazonS3;
    private readonly S3Options _s3Options;

    public S3StorageService(
        IAmazonS3 amazonS3,
        IOptions<S3Options> s3Options)
    {
        _amazonS3 = amazonS3;
        _s3Options = s3Options.Value;
    }

    public async Task<string> UploadAsync(Stream stream, string key, CancellationToken cancellationToken = default)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("S3 object key is required.", nameof(key));

        if (string.IsNullOrWhiteSpace(_s3Options.BucketName))
            throw new InvalidOperationException("S3 bucket name is not configured.");

        var request = new PutObjectRequest
        {
            BucketName = _s3Options.BucketName,
            Key = key,
            InputStream = stream
        };

        var response = await _amazonS3.PutObjectAsync(request, cancellationToken);

        if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Failed to upload file to S3. Status code: {response.HttpStatusCode}");
        }

        return key;
    }

    public Task<string> CreateReadUrlAsync(
        string key,
        TimeSpan expiresIn,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("S3 object key is required.", nameof(key));

        if (string.IsNullOrWhiteSpace(_s3Options.BucketName))
            throw new InvalidOperationException("S3 bucket name is not configured.");

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _s3Options.BucketName,
            Key = key,
            Expires = DateTime.UtcNow.Add(expiresIn)
        };

        return Task.FromResult(_amazonS3.GetPreSignedURL(request));
    }
}
