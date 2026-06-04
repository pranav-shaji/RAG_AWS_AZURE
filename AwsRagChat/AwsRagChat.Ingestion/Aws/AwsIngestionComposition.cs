using Amazon.BedrockRuntime;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.Textract;
using AwsRagChat.Ingestion.Models;
using AwsRagChat.Ingestion.Options;
using AwsRagChat.Ingestion.Services;
using AwsRagChat.Infrastructure.Persistence;
using AwsRagChat.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using InfrastructureDynamoDbOptions = AwsRagChat.Infrastructure.Options.DynamoDbOptions;

namespace AwsRagChat.Ingestion.Aws;

public static class AwsIngestionComposition
{
    public static AwsIngestionServices Create(IConfiguration configuration)
    {
        var dynamoDbOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(DynamoDbOptions.SectionName).Get<DynamoDbOptions>() ?? new DynamoDbOptions());

        var infrastructureDynamoDbOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(InfrastructureDynamoDbOptions.SectionName).Get<InfrastructureDynamoDbOptions>() ?? new InfrastructureDynamoDbOptions());

        var bedrockOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(BedrockOptions.SectionName).Get<BedrockOptions>() ?? new BedrockOptions());

        var textractAsyncOptions = Microsoft.Extensions.Options.Options.Create(
            configuration.GetSection(TextractAsyncOptions.SectionName).Get<TextractAsyncOptions>() ?? new TextractAsyncOptions());

        var amazonS3 = new AmazonS3Client();
        var dynamoDb = new AmazonDynamoDBClient();
        var bedrock = new AmazonBedrockRuntimeClient();
        var textract = new AmazonTextractClient();

        var textExtractionService = new TextExtractionService();
        var textractTextExtractionService = new TextractTextExtractionService(textract);
        var textractAsyncExtractionService = new TextractAsyncExtractionService(textract, textractAsyncOptions);

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
            AmazonS3 = amazonS3,
            TextExtractionService = textExtractionService,
            TextractTextExtractionService = textractTextExtractionService,
            TextractAsyncExtractionService = textractAsyncExtractionService,
            DocumentStatusService = documentStatusService,
            DocumentIngestionPipeline = documentIngestionPipeline
        };
    }
}
