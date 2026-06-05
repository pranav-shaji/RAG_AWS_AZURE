using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.Textract;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Options;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Infrastructure.Storage;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using InfrastructureDynamoDbOptions = AwsRagChat.Infrastructure.Options.DynamoDbOptions;
using InfrastructureS3Options = AwsRagChat.Infrastructure.Options.S3Options;

namespace AwsRagChat.Ingestion.Aws;

public static class AwsIngestionComposition
{
    public static AwsIngestionServices Create(IConfiguration configuration)
    {
        var dynamoDbOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(DynamoDbOptions.SectionName).Get<DynamoDbOptions>() ?? new DynamoDbOptions());

        var infrastructureDynamoDbOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(InfrastructureDynamoDbOptions.SectionName).Get<InfrastructureDynamoDbOptions>() ?? new InfrastructureDynamoDbOptions());

        var bedrockOptionsObj = configuration.GetSection(BedrockOptions.SectionName).Get<BedrockOptions>() ?? new BedrockOptions();
        var embeddingModelId = configuration["Embedding:ModelId"];
        if (!string.IsNullOrEmpty(embeddingModelId))
        {
            bedrockOptionsObj.EmbeddingModelId = embeddingModelId;
        }
        var bedrockOptions = Microsoft.Extensions.Options.Options.Create(bedrockOptionsObj);

        var textractAsyncOptionsObj = configuration.GetSection(TextractAsyncOptions.SectionName).Get<TextractAsyncOptions>() ?? new TextractAsyncOptions();
        var docProcessingSection = configuration.GetSection("DocumentProcessing");
        if (docProcessingSection.Exists())
        {
            var snsTopicArn = docProcessingSection["SnsTopicArn"];
            if (!string.IsNullOrEmpty(snsTopicArn))
            {
                textractAsyncOptionsObj.SnsTopicArn = snsTopicArn;
            }
            var roleArn = docProcessingSection["TextractPublishRoleArn"];
            if (!string.IsNullOrEmpty(roleArn))
            {
                textractAsyncOptionsObj.TextractPublishRoleArn = roleArn;
            }
            var jobTag = docProcessingSection["JobTag"];
            if (!string.IsNullOrEmpty(jobTag))
            {
                textractAsyncOptionsObj.JobTag = jobTag;
            }
        }
        var textractAsyncOptions = Microsoft.Extensions.Options.Options.Create(textractAsyncOptionsObj);

        var s3OptionsObj = configuration.GetSection(InfrastructureS3Options.SectionName).Get<InfrastructureS3Options>() ?? new InfrastructureS3Options();
        var storageSection = configuration.GetSection("Storage");
        if (storageSection.Exists())
        {
            var bucket = storageSection["BucketOrContainerName"];
            if (!string.IsNullOrEmpty(bucket))
            {
                s3OptionsObj.BucketName = bucket;
            }
        }
        var s3Options = Microsoft.Extensions.Options.Options.Create(s3OptionsObj);

        var amazonS3 = new AmazonS3Client();
        var dynamoDb = new AmazonDynamoDBClient();
        var bedrock = new AmazonBedrockRuntimeClient();
        var textract = new AmazonTextractClient();

        var textExtractionService = new TextExtractionService();
        var textractTextExtractionService = new TextractTextExtractionService(textract);
        var textractAsyncExtractionService = new TextractAsyncExtractionService(textract, textractAsyncOptions);
        var storageProvider = new S3StorageService(amazonS3, s3Options);
        var documentProcessor = new AwsDocumentProcessor(
            textExtractionService,
            textractTextExtractionService,
            textractAsyncExtractionService,
            storageProvider);

        var chunkingService = new ChunkingService();
        var embeddingBatchService = new EmbeddingBatchService(bedrock, bedrockOptions);
        var chunkRepository = new DynamoDbChunkRepository(dynamoDb, infrastructureDynamoDbOptions);

        var documentStatusService = new DocumentStatusService(
            dynamoDb,
            configuration["DynamoDb:DocumentsTableName"] ?? "rag-documents");

        var openSearchService = new OpenSearchService(configuration);

        var documentIngestionPipeline = new DocumentIngestionPipeline(
            chunkingService,
            embeddingBatchService,
            chunkRepository,
            documentStatusService,
            openSearchService);

        _ = dynamoDbOptions;

        return new AwsIngestionServices
        {
            DocumentProcessor = documentProcessor,
            DocumentStatusService = documentStatusService,
            DocumentIngestionPipeline = documentIngestionPipeline
        };
    }
}
