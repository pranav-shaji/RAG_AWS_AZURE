using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using AwsRagChat.Application.Interfaces;
using AwsRagChat.Application.Models;
using AwsRagChat.Infrastructure.Options;
using Microsoft.Extensions.Options;

using Polly;
using Polly.Registry;

namespace AwsRagChat.Infrastructure.Storage;

public sealed class AzureBlobStorageService : IStorageProvider
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly AzureStorageOptions _options;
    private readonly ResiliencePipeline _resiliencePipeline;

    public AzureBlobStorageService(
        IOptions<AzureStorageOptions> options,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _options = options.Value;
        _resiliencePipeline = pipelineProvider.GetPipeline("BlobStoragePipeline");

        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.MaxRetries = 0; // Disable SDK native retry so Polly handles it

        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _blobServiceClient = new BlobServiceClient(_options.ConnectionString, clientOptions);
        }
        else if (!string.IsNullOrWhiteSpace(_options.AccountUrl))
        {
            _blobServiceClient = new BlobServiceClient(new Uri(_options.AccountUrl), new DefaultAzureCredential(), clientOptions);
        }
        else
        {
            throw new InvalidOperationException("Azure Blob Storage requires either ConnectionString or AccountUrl.");
        }
    }

    public async Task<string> UploadAsync(Stream stream, string key, CancellationToken cancellationToken = default)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Storage object key is required.", nameof(key));

        var containerName = _options.BucketOrContainerName;
        if (string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException("Azure Storage container name is not configured.");

        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await _resiliencePipeline.ExecuteAsync(
            async token => await containerClient.CreateIfNotExistsAsync(cancellationToken: token),
            cancellationToken);

        var blobClient = containerClient.GetBlobClient(key);
        await _resiliencePipeline.ExecuteAsync(
            async token => await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: token),
            cancellationToken);

        return key;
    }

    public async Task<string> CreateReadUrlAsync(
        string key,
        TimeSpan expiresIn,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Storage object key is required.", nameof(key));

        var containerName = _options.BucketOrContainerName;
        if (string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException("Azure Storage container name is not configured.");

        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(key);

        if (blobClient.CanGenerateSasUri)
        {
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = key,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn)
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sasUri = blobClient.GenerateSasUri(sasBuilder);
            return sasUri.ToString();
        }

        try
        {
            var userDelegationKey = await _resiliencePipeline.ExecuteAsync(
                async token => await _blobServiceClient.GetUserDelegationKeyAsync(
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.Add(expiresIn),
                    cancellationToken: token),
                cancellationToken);

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = key,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(expiresIn)
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sasValues = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, _blobServiceClient.AccountName);
            var uriBuilder = new BlobUriBuilder(blobClient.Uri)
            {
                Sas = sasValues
            };
            return uriBuilder.ToUri().ToString();
        }
        catch
        {
            return blobClient.Uri.ToString();
        }
    }

    public async Task<StorageObjectReadResult> OpenReadAsync(
        StorageObjectReadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.ObjectKey))
            throw new ArgumentException("Storage object key is required.", nameof(request.ObjectKey));

        var containerName = string.IsNullOrWhiteSpace(request.BucketOrContainerName)
            ? _options.BucketOrContainerName
            : request.BucketOrContainerName;

        if (string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException("Azure Storage container name is not configured.");

        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(request.ObjectKey);

        var response = await _resiliencePipeline.ExecuteAsync(
            async token => await blobClient.DownloadStreamingAsync(cancellationToken: token),
            cancellationToken);

        return new StorageObjectReadResult
        {
            Content = response.Value.Content,
            ContentType = response.Value.Details.ContentType,
            ContentLength = response.Value.Details.ContentLength
        };
    }
}
