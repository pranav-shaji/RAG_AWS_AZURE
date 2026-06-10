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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AwsRagChat.Infrastructure.Resilience;
using InfrastructureDynamoDbOptions = AwsRagChat.Infrastructure.Options.DynamoDbOptions;
using InfrastructureS3Options = AwsRagChat.Infrastructure.Options.S3Options;

namespace AwsRagChat.Ingestion.Aws;

public static class AwsIngestionComposition
{
    public static AwsIngestionServices Create(IConfiguration configuration, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

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

        // 2. Setup Resilience Pipeline Provider
        var resilienceServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        resilienceServices.AddCustomResiliencePipelines();
        var resilienceProvider = resilienceServices.BuildServiceProvider();
        var pipelineProvider = resilienceProvider.GetRequiredService<Polly.Registry.ResiliencePipelineProvider<string>>();

        // 3. Instantiate AWS Clients with MaxErrorRetry = 1 to prevent retry amplification
        var s3Config = new AmazonS3Config { MaxErrorRetry = 1 };
        var dynamoDbConfig = new AmazonDynamoDBConfig { MaxErrorRetry = 1 };
        var bedrockConfig = new AmazonBedrockRuntimeConfig { MaxErrorRetry = 1 };
        var textractConfig = new AmazonTextractConfig { MaxErrorRetry = 1 };

        var amazonS3 = new AmazonS3Client(s3Config);
        var dynamoDb = new AmazonDynamoDBClient(dynamoDbConfig);
        var bedrock = new AmazonBedrockRuntimeClient(bedrockConfig);
        var textract = new AmazonTextractClient(textractConfig);

        var textExtractionService = new TextExtractionService();
        var textractTextExtractionService = new TextractTextExtractionService(textract);
        var textractAsyncExtractionService = new TextractAsyncExtractionService(textract, textractAsyncOptions);
        var storageProvider = new S3StorageService(amazonS3, s3Options, pipelineProvider);
        var documentProcessor = new AwsDocumentProcessor(
            textExtractionService,
            textractTextExtractionService,
            textractAsyncExtractionService,
            storageProvider,
            pipelineProvider);

        var chunkingService = new ChunkingService();
        var embeddingBatchService = new EmbeddingBatchService(bedrock, bedrockOptions, pipelineProvider);
        var chunkRepository = new DynamoDbChunkRepository(
            dynamoDb, 
            infrastructureDynamoDbOptions, 
            pipelineProvider, 
            loggerFactory.CreateLogger<DynamoDbChunkRepository>());

        var documentStatusService = new DocumentStatusService(
            dynamoDb,
            configuration["DynamoDb:DocumentsTableName"] ?? "rag-documents");

        var openSearchService = new OpenSearchService(
            configuration, 
            pipelineProvider, 
            loggerFactory.CreateLogger<OpenSearchService>());

        var documentIngestionPipeline = new DocumentIngestionPipeline(
            chunkingService,
            embeddingBatchService,
            chunkRepository,
            documentStatusService,
            openSearchService,
            loggerFactory.CreateLogger<DocumentIngestionPipeline>());

        _ = dynamoDbOptions;

        return new AwsIngestionServices
        {
            DocumentProcessor = documentProcessor,
            DocumentStatusService = documentStatusService,
            DocumentIngestionPipeline = documentIngestionPipeline
        };
    }
}
