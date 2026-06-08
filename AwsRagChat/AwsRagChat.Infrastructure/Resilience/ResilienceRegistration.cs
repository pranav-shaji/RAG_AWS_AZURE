using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;
using Polly.Timeout;
using System;
using System.Net;
using System.IO;
using System.Net.Sockets;
using Amazon.Runtime;
using Azure;
using System.ClientModel;
using Microsoft.Graph.Models.ODataErrors;
using Amazon.DynamoDBv2.Model;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.S3;
using StackExchange.Redis;
using OpenSearch.Client;
using OpenSearch.Net;

namespace AwsRagChat.Infrastructure.Resilience;

public static class ResilienceRegistration
{
    public static IServiceCollection AddCustomResiliencePipelines(this IServiceCollection services)
    {
        // 1. AWS Bedrock Chat Pipeline
        services.AddResiliencePipeline("BedrockChatPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(45));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonServiceException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<AmazonClientException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonServiceException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<AmazonClientException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // 2. AWS Bedrock Embed Pipeline
        services.AddResiliencePipeline("BedrockEmbedPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(15));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonServiceException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<AmazonClientException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonServiceException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<AmazonClientException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // 3. Azure OpenAI Chat Pipeline
        services.AddResiliencePipeline("AzureOpenAiChatPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(45));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ClientResultException>(ex =>
                    ex.Status == 429 || ex.Status >= 500)
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ClientResultException>(ex =>
                    ex.Status == 429 || ex.Status >= 500),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // 4. Azure OpenAI Embed Pipeline
        services.AddResiliencePipeline("AzureOpenAiEmbedPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(15));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ClientResultException>(ex =>
                    ex.Status == 429 || ex.Status >= 500)
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ClientResultException>(ex =>
                    ex.Status == 429 || ex.Status >= 500),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // 5. AWS OpenSearch Pipeline
        services.AddResiliencePipeline("OpenSearchPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(10));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<OpenSearchClientException>()
                    .Handle<UnexpectedOpenSearchClientException>()
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<WebException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(800)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<OpenSearchClientException>()
                    .Handle<UnexpectedOpenSearchClientException>()
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<WebException>(),
                FailureRatio = 0.4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(20)
            });
        });

        // 6. Azure AI Search Pipeline
        services.AddResiliencePipeline("AzureAiSearchPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(10));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RequestFailedException>(ex =>
                    ex.Status == 429 || ex.Status >= 500)
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(800)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RequestFailedException>(ex =>
                    ex.Status == 429 || ex.Status >= 500)
                    .Handle<SocketException>()
                    .Handle<IOException>(),
                FailureRatio = 0.4,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(20)
            });
        });

        // 7. DynamoDB Pipeline (No Circuit Breakers)
        services.AddResiliencePipeline("DynamoDbPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(5));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ProvisionedThroughputExceededException>()
                    .Handle<Amazon.DynamoDBv2.Model.LimitExceededException>()
                    .Handle<AmazonServiceException>(ex =>
                        ex.StatusCode == HttpStatusCode.TooManyRequests ||
                        ex.StatusCode == HttpStatusCode.InternalServerError ||
                        ex.StatusCode == HttpStatusCode.ServiceUnavailable)
                    .Handle<AmazonClientException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 4,
                Delay = TimeSpan.FromMilliseconds(500)
            });
        });

        // 8. Cosmos DB Pipeline (No Circuit Breakers)
        services.AddResiliencePipeline("CosmosDbPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(5));
            builder.AddRetry(new RetryStrategyOptions
            {
                // Handles only failures escaping the SDK rate-limit retry layer
                ShouldHandle = new PredicateBuilder().Handle<Microsoft.Azure.Cosmos.CosmosException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.RequestTimeout ||
                    ex.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 4,
                Delay = TimeSpan.FromMilliseconds(500)
            });
        });

        // 9. AWS Cognito Pipeline
        services.AddResiliencePipeline("CognitoPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(5));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TooManyRequestsException>()
                    .Handle<AmazonServiceException>(ex =>
                        ex.StatusCode == HttpStatusCode.TooManyRequests ||
                        ex.StatusCode == HttpStatusCode.InternalServerError)
                    .Handle<AmazonClientException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TooManyRequestsException>()
                    .Handle<AmazonServiceException>(ex =>
                        ex.StatusCode == HttpStatusCode.TooManyRequests ||
                        ex.StatusCode == HttpStatusCode.InternalServerError),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // 10. Microsoft Graph Pipeline
        services.AddResiliencePipeline("GraphPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(5));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ODataError>(ex =>
                    ex.ResponseStatusCode == 429 ||
                    ex.ResponseStatusCode == 503 ||
                    ex.ResponseStatusCode == 504)
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<ODataError>(ex =>
                    ex.ResponseStatusCode == 429 ||
                    ex.ResponseStatusCode == 503 ||
                    ex.ResponseStatusCode == 504)
                    .Handle<SocketException>()
                    .Handle<IOException>(),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(15)
            });
        });

        // 11. Redis Pipeline
        services.AddResiliencePipeline("RedisPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(1));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RedisConnectionException>()
                    .Handle<RedisTimeoutException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Constant,
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(100)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RedisConnectionException>()
                    .Handle<RedisTimeoutException>(),
                FailureRatio = 0.4,
                SamplingDuration = TimeSpan.FromSeconds(15),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(30)
            });
        });

        // 12. S3 Storage Pipeline
        services.AddResiliencePipeline("S3Pipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(30));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonS3Exception>(ex =>
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    ex.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<AmazonClientException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500)
            });
        });

        // 13. Blob Storage Pipeline
        services.AddResiliencePipeline("BlobStoragePipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(30));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RequestFailedException>(ex =>
                    ex.Status == 500 || ex.Status == 503 || ex.Status == 504)
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(500)
            });
        });

        // 14. Document Processing (OCR) Pipeline
        services.AddResiliencePipeline("OcrPipeline", builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(60));
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonServiceException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable)
                    .Handle<RequestFailedException>(ex =>
                        ex.Status == 429 || ex.Status >= 500)
                    .Handle<TimeoutException>(),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2)
            });
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<AmazonServiceException>(ex =>
                    ex.StatusCode == HttpStatusCode.TooManyRequests ||
                    ex.StatusCode == HttpStatusCode.InternalServerError ||
                    ex.StatusCode == HttpStatusCode.ServiceUnavailable)
                    .Handle<RequestFailedException>(ex =>
                        ex.Status == 429 || ex.Status >= 500),
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(60),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(60)
            });
        });

        return services;
    }
}
