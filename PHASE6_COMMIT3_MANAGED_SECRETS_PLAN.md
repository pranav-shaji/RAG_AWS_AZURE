# PHASE6_COMMIT3_MANAGED_SECRETS_PLAN

## Executive Summary

The platform currently loads all sensitive values — API keys, client secrets, connection strings, and IAM ARNs — from `appsettings.json`, `local.settings.json`, and environment variables. This is acceptable for local development but is a production security risk.

Commit 3 introduces cloud-native secret management for both runtime paths:

- **AWS**: AWS Secrets Manager (primary) + Systems Manager Parameter Store (non-sensitive config)
- **Azure**: Azure Key Vault + Managed Identity

The approach integrates secret stores as **additional IConfiguration sources**, injected before DI registration. This means the existing Options classes, DI registrations, and CloudProvider switching architecture are **entirely preserved**. No new abstractions are added. No provider coupling is introduced.

---

## Current Secret Inventory

All sensitive values were identified by inspecting all Options classes, `Program.cs` files, `DependencyInjection.cs`, `AzureProviderServiceCollectionExtensions.cs`, `AwsProviderServiceCollectionExtensions.cs`, and all `appsettings.json` / `local.settings.json` files.

### Classification Key

| Priority | Definition |
|---|---|
| **P0** | Credential allows full resource access or identity impersonation. Immediate risk if leaked. |
| **P1** | API key or endpoint that enables billable or sensitive operations. High risk if leaked. |
| **P2** | Non-sensitive configuration. No credential value. Safe to leave in config files. |

---

### AWS Runtime — Secret Inventory

| Config Key | Options Class | Description | Classification |
|---|---|---|---|
| *(implicit)* AWS IAM credentials | `AwsOptions` (SDK default chain) | Access key + secret for SDK clients | **P0** — managed via IAM Role, not stored in config |
| `Cognito:UserPoolId` | `CognitoOptions` | Pool identifier — public, non-secret | **P2** |
| `Cognito:AppClientId` | `CognitoOptions` | Client ID — public, not a secret | **P2** |
| `Cognito:Region` | `CognitoOptions` | AWS region string | **P2** |
| `AWS:Region` | (inline in `AwsProviderServiceCollectionExtensions`) | AWS region string | **P2** |
| `VectorStore:Endpoint` / `OpenSearch:Endpoint` | `AzureAiSearchOptions` (mapped from OpenSearch section) | OpenSearch Serverless endpoint URL | **P1** — endpoint exposes resource location |
| `TextractAsync:SnsTopicArn` | `TextractAsyncOptions` | SNS ARN — non-secret but infrastructure-sensitive | **P2** |
| `TextractAsync:TextractPublishRoleArn` | `TextractAsyncOptions` | IAM Role ARN — non-secret but infra-sensitive | **P2** |
| `S3:BucketName` / `Storage:BucketOrContainerName` | `S3Options` | S3 bucket name | **P2** |
| `DynamoDb:TableName` | `DynamoDbOptions` | DynamoDB table name | **P2** |
| `Bedrock:EmbeddingModelId` / `Chat:ModelId` | `BedrockOptions` | Model ID strings — non-secret | **P2** |
| `Redis:ConnectionString` | (inline in `Program.cs`) | Redis connection string (host + port + auth token) | **P0** — contains auth credential |

---

### Azure Runtime — Secret Inventory

| Config Key | Options Class | Description | Classification |
|---|---|---|---|
| `EntraId:ClientSecret` | `EntraIdOptions` | App Registration client secret | **P0** — highest priority secret in Azure path |
| `EntraId:TenantId` | `EntraIdOptions` | Directory tenant ID — semi-public | **P2** |
| `EntraId:ClientId` | `EntraIdOptions` | App registration client ID — semi-public | **P2** |
| `AzureOpenAi:ApiKey` | `AzureOpenAiOptions` | Azure OpenAI service key | **P0** — direct API billing access |
| `AzureOpenAi:Endpoint` | `AzureOpenAiOptions` | Azure OpenAI resource endpoint | **P1** — exposes resource identity |
| `AzureOpenAi:EmbeddingDeploymentName` | `AzureOpenAiOptions` | Deployment name — non-secret | **P2** |
| `AzureOpenAi:ChatDeploymentName` | `AzureOpenAiOptions` | Deployment name — non-secret | **P2** |
| `VectorStore:ApiKey` | `AzureAiSearchOptions` | Azure AI Search admin/query key | **P0** — full index read/write access |
| `VectorStore:Endpoint` | `AzureAiSearchOptions` | AI Search resource endpoint | **P1** |
| `CosmosDb:AuthKey` | `CosmosDbOptions` | Cosmos DB primary key | **P0** — full database read/write access |
| `CosmosDb:Endpoint` | `CosmosDbOptions` | Cosmos DB account endpoint | **P1** |
| `Storage:ConnectionString` | `AzureStorageOptions` | Blob Storage connection string (with key) | **P0** |
| `Storage:AccountUrl` | `AzureStorageOptions` | Storage account URL | **P1** |
| `DocumentProcessing:ApiKey` | `AzureDocumentProcessingOptions` (Ingestion) | Document Intelligence key | **P0** |
| `DocumentProcessing:Endpoint` | `AzureDocumentProcessingOptions` (Ingestion) | Document Intelligence endpoint | **P1** |
| `Redis:ConnectionString` | (inline in `Program.cs`) | Redis connection string (with auth) | **P0** |
| `AzureWebJobsStorage` | `local.settings.json` | Azure Functions storage connection string | **P0** in production |

---

## AWS Secret Management Design

### Tool Selection

| Tool | Decision |
|---|---|
| **AWS Secrets Manager** | ✅ Selected for all P0 secrets |
| **AWS SSM Parameter Store** | ✅ Selected for P1 infra values (endpoints, ARNs) |
| Hardcoded environment variables | ✅ Retained for P2 values (region, model IDs, bucket names) |

AWS Secrets Manager is preferred over SSM for P0 values because it supports automatic rotation, fine-grained IAM access policies, and cross-account access. SSM Parameter Store (SecureString) is appropriate for infrastructure-sensitive values that do not require rotation.

### Recommended AWS Secrets Manager Secret Structure

All secrets follow a flat JSON structure under a single secret name per environment:

```
/rag-chat/{environment}/api-secrets
```

Example content (single secret, JSON object):

```json
{
  "Redis__ConnectionString": "rediss://:authtoken@hostname:6380"
}
```

### Recommended SSM Parameter Store Structure

```
/rag-chat/{environment}/config/OpenSearch__Endpoint
/rag-chat/{environment}/config/S3__BucketName
/rag-chat/{environment}/config/DynamoDb__TableName
/rag-chat/{environment}/config/TextractAsync__SnsTopicArn
/rag-chat/{environment}/config/TextractAsync__TextractPublishRoleArn
```

### Integration Package

```
Amazon.Extensions.Configuration.SystemsManager
```

This is the standard AWS-maintained package for integrating Secrets Manager and SSM Parameter Store as `IConfiguration` sources. It supports:

- Secret flattening with `__` → `:` key mapping
- Automatic reload on rotation
- Prefix-based filtering

### Startup Integration (API Host — AWS Mode)

Secret sources are added to `WebApplicationBuilder.Configuration` **before** `builder.Build()` is called, and therefore before `AddCloudProviderInfrastructure`. The CloudProvider value is read first from the existing environment variable or base config to determine which secret source to attach.

```
[1] Base configuration loaded (appsettings.json, env vars)
[2] CloudProvider determined
[3] If AWS: AddSystemsManager("/rag-chat/{env}/") added to IConfigurationBuilder
[4] Secrets Manager secrets merged into IConfiguration
[5] builder.Services.AddCloudProviderAuthentication(...)
[6] builder.Services.AddCloudProviderInfrastructure(...)
```

No changes to DI extension methods are required.

### IAM Role Requirements (AWS)

The EC2/ECS/App Runner task role (or Lambda execution role) must have:

```json
{
  "Effect": "Allow",
  "Action": [
    "secretsmanager:GetSecretValue",
    "ssm:GetParameter",
    "ssm:GetParametersByPath"
  ],
  "Resource": [
    "arn:aws:secretsmanager:us-east-1:ACCOUNT_ID:secret:/rag-chat/*",
    "arn:aws:ssm:us-east-1:ACCOUNT_ID:parameter/rag-chat/*"
  ]
}
```

No access keys or secret access keys are stored. IAM Role provides ambient credentials via the SDK default credential chain.

---

## Azure Secret Management Design

### Tool Selection

| Tool | Decision |
|---|---|
| **Azure Key Vault** | ✅ Selected for all P0 and P1 secrets |
| **Managed Identity** | ✅ Required for Key Vault access in production |
| `DefaultAzureCredential` | ✅ Already used in `AzureProviderServiceCollectionExtensions.cs` — extends naturally |

### Recommended Key Vault Secret Name Structure

Azure Key Vault secret names use hyphens (colons and underscores are not permitted). The integration package maps `--` to `:` for IConfiguration compatibility.

```
AzureOpenAi--ApiKey
AzureOpenAi--Endpoint
VectorStore--ApiKey
VectorStore--Endpoint
CosmosDb--AuthKey
CosmosDb--Endpoint
Storage--ConnectionString
Storage--AccountUrl
DocumentProcessing--ApiKey
DocumentProcessing--Endpoint
EntraId--ClientSecret
Redis--ConnectionString
AzureWebJobsStorage        (Azure Functions host only)
```

### Integration Package

```
Azure.Extensions.AspNetCore.Configuration.Secrets
Azure.Security.KeyVault.Secrets
Azure.Identity
```

`Azure.Identity` is already a dependency of the project (`DefaultAzureCredential` is used in `AzureProviderServiceCollectionExtensions.cs`). No new identity packages are required.

### Startup Integration (API Host — Azure Mode)

```
[1] Base configuration loaded (appsettings.json, env vars)
[2] CloudProvider determined ("Azure")
[3] KeyVaultUri read from environment variable: AZURE_KEY_VAULT_URI
[4] builder.Configuration.AddAzureKeyVault(vaultUri, new DefaultAzureCredential()) added
[5] Key Vault secrets merged into IConfiguration
[6] builder.Services.AddCloudProviderAuthentication(...)
[7] builder.Services.AddCloudProviderInfrastructure(...)
```

### Startup Integration (Azure Functions Host)

The Azure Functions Isolated Worker host uses `HostBuilder`. Key Vault is added during `ConfigureAppConfiguration`:

```
[1] ConfigureFunctionsWorkerDefaults()
[2] ConfigureAppConfiguration: AddAzureKeyVault(...)
[3] ConfigureServices: AzureIngestionComposition.Create(configuration)
```

The `AzureIngestionComposition.Create(IConfiguration)` method already accepts `IConfiguration`, so Key Vault secrets are automatically available without any changes to composition logic.

### Managed Identity Requirements (Azure)

The App Service / Container App / Azure Functions identity must be assigned the built-in RBAC role:

```
Key Vault Secrets User
```

on the Key Vault resource. No client secrets are stored for the managed identity. `DefaultAzureCredential` resolves identity automatically in App Service / Azure Functions environments.

### EntraId ClientSecret Special Case

`EntraId:ClientSecret` is the single most sensitive value in the Azure path. In production, the recommended approach is to replace client secret authentication with **Managed Identity** (federated identity or system-assigned identity), eliminating the need for `ClientSecret` entirely. However, if a client secret is required (e.g., multi-tenant scenarios), it must be stored in Key Vault and never present in any configuration file.

---

## Configuration Loading Flow

### AWS Runtime

```
Priority (high → low):
1. AWS Secrets Manager     (/rag-chat/{env}/api-secrets)
2. SSM Parameter Store     (/rag-chat/{env}/config/*)
3. Environment Variables
4. appsettings.{Environment}.json
5. appsettings.json
```

### Azure Runtime (API)

```
Priority (high → low):
1. Azure Key Vault          (AZURE_KEY_VAULT_URI env var)
2. Environment Variables
3. appsettings.{Environment}.json
4. appsettings.json
```

### Azure Functions Runtime

```
Priority (high → low):
1. Azure Key Vault          (AZURE_KEY_VAULT_URI env var)
2. Azure App Settings       (FUNCTIONS_WORKER_RUNTIME, etc.)
3. local.settings.json      (local dev only)
```

### CloudProvider Gating

Secret source attachment is conditional on `CloudProvider`:

```csharp
var cloudProvider = builder.Configuration["CloudProvider"];

if (cloudProvider == "AWS")
{
    // Attach Secrets Manager + SSM
}
else if (cloudProvider == "Azure")
{
    // Attach Key Vault
}
```

This preserves the existing provider-isolation architecture established in Phase 5 and maintained through Phase 6 Commits 1 and 2.

---

## Local Development Strategy

No cloud secret stores are required for local development. The precedence order for local development is:

```
Priority (high → low):
1. .NET User Secrets        (dotnet user-secrets set ...)
2. Environment Variables
3. appsettings.Development.json
4. appsettings.json
5. local.settings.json      (Azure Functions only)
```

**Recommended approach for each developer:**

- Azure secrets: `dotnet user-secrets set "AzureOpenAi:ApiKey" "..."` in `AwsRagChat.Api`
- AWS secrets: Environment variables or `~/.aws/credentials` with IAM user/role
- Azure Functions: Values in `local.settings.json` (already gitignored)

**Important**: `appsettings.json` and `local.settings.json` must **never** contain real credentials. They should contain empty strings or placeholder comments for all P0/P1 values.

### .gitignore Validation

Confirm the following are excluded from source control:

```
appsettings.Development.json
appsettings.Production.json
local.settings.json
```

`local.settings.json` is already present in the repo — this must be audited and removed or gitignored before production if it contains real values.

---

## CI/CD Strategy

### AWS (GitHub Actions / CodePipeline)

- The CI/CD pipeline IAM role is granted `secretsmanager:GetSecretValue` only for the relevant environment prefix.
- Secrets are **not** passed as GitHub Actions secrets into the build — they are resolved at runtime by the application from Secrets Manager via IAM Role.
- The only CI/CD-level secret required is the AWS credentials for deployment (e.g., `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` or OIDC federation).
- `AWS_REGION` is passed as a non-secret environment variable.

### Azure (GitHub Actions / Azure DevOps)

- The deployment service principal is granted `Key Vault Secrets Officer` during infrastructure setup, and `Key Vault Secrets User` for the runtime Managed Identity.
- `AZURE_KEY_VAULT_URI` is passed as a GitHub Actions variable (non-secret — it is the vault URI, not a credential).
- The actual secrets (OpenAI key, Cosmos key, etc.) are stored in Key Vault and never touch CI/CD pipelines.
- Deployment is performed via Azure CLI or Bicep with OIDC federation to the deployment service principal.

---

## Risks

| Risk | Severity | Mitigation |
|---|---|---|
| `local.settings.json` currently committed with real values | **P0** | Audit immediately. Remove real values. Add to `.gitignore` if not already excluded. |
| `appsettings.json` in `AwsRagChat.Ingestion.Aws` contains real ARNs and endpoint URLs | **P1** | ARNs are not credentials but expose infrastructure topology. Move endpoints to SSM. |
| `AzureWebJobsStorage` in `local.settings.json` uses `UseDevelopmentStorage=true` — safe locally | Low | Confirm production Azure Functions app uses a real storage connection string via Key Vault or App Settings. |
| `EntraId:ClientSecret` — if present in any config file | **P0** | Confirm no secrets are committed. Replace with Managed Identity where possible. |
| Startup failure if Key Vault / Secrets Manager is unreachable | Medium | Use `optional: true` flag on secret provider registration in non-production environments. Guard with environment check. |
| `DefaultAzureCredential` cold-start latency | Low | Already in use for Cosmos DB. Key Vault adds one additional token acquisition — negligible. |
| Secret reload during rolling deploy | Low | Use `reloadInterval` on SSM provider to avoid stale secrets after rotation. Key Vault integration is read-once at startup by default. |

---

## Commit Scope

This commit is **infrastructure-only**. It does not modify:

- Domain layer
- Application layer
- Repository contracts
- CloudProvider switching logic
- DI extension methods for provider registration
- Options classes (no new properties needed)

### What changes:

1. `AwsRagChat.Api/Program.cs` — Add conditional secret source attachment before `builder.Build()`
2. `AwsRagChat.Ingestion.Azure/Program.cs` — Add `ConfigureAppConfiguration` with Key Vault source
3. `AwsRagChat.Ingestion.Aws/Program.cs` (if exists) or Lambda bootstrap — Add SSM/Secrets Manager source
4. `AwsRagChat.Api/AwsRagChat.Api.csproj` — Add `Amazon.Extensions.Configuration.SystemsManager` and `Azure.Extensions.AspNetCore.Configuration.Secrets` package references
5. `AwsRagChat.Ingestion.Azure/AwsRagChat.Ingestion.Azure.csproj` — Add `Azure.Extensions.AspNetCore.Configuration.Secrets`
6. `.gitignore` — Confirm `local.settings.json` and `appsettings.*.json` (except base) are excluded
7. `appsettings.json` files — Remove any real credential values, replace with empty strings

---

## Files Expected To Change

| File | Change |
|---|---|
| [`AwsRagChat.Api/Program.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/Program.cs) | Add CloudProvider-conditional secret source registration |
| [`AwsRagChat.Api/AwsRagChat.Api.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Api/AwsRagChat.Api.csproj) | Add `Amazon.Extensions.Configuration.SystemsManager`, `Azure.Extensions.AspNetCore.Configuration.Secrets` |
| [`AwsRagChat.Ingestion.Azure/Program.cs`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Azure/Program.cs) | Add `ConfigureAppConfiguration` block with Key Vault source |
| [`AwsRagChat.Ingestion.Azure/AwsRagChat.Ingestion.Azure.csproj`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Azure/AwsRagChat.Ingestion.Azure.csproj) | Add `Azure.Extensions.AspNetCore.Configuration.Secrets` |
| [`AwsRagChat.Ingestion.Aws/appsettings.json`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Aws/appsettings.json) | Remove real endpoint URLs and ARNs. Replace with empty strings or SSM placeholders |
| [`AwsRagChat.Ingestion.Azure/local.settings.json`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/AwsRagChat/AwsRagChat.Ingestion.Azure/local.settings.json) | Confirm only dev-safe values. Add `AZURE_KEY_VAULT_URI` placeholder |
| [`.gitignore`](file:///c:/Users/Admin/source/repos/RAGAZUREAWS/.gitignore) | Verify exclusions for `local.settings.json`, `appsettings.Development.json`, `appsettings.Production.json` |

**Files NOT changing:**

- All `Options/*.cs` files — no structural changes
- `DependencyInjection.cs` — no changes
- `AzureProviderServiceCollectionExtensions.cs` — no changes
- `AwsProviderServiceCollectionExtensions.cs` — no changes
- `AzureIngestionComposition.cs` — no changes
- All Application / Domain layer files

---

## Validation Strategy

### Build Validation

```powershell
dotnet build AwsRagChat\AwsRagChat.slnx --no-incremental -v q
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

### AWS Runtime Validation

1. Deploy to an environment with the IAM Role attached.
2. Confirm the application starts without `InvalidOperationException` on options validation.
3. Confirm `Redis:ConnectionString` resolves from Secrets Manager (not from `appsettings.json`).
4. Confirm OpenSearch endpoint resolves from SSM.
5. Confirm AWS SDK clients initialize correctly using ambient IAM role credentials.

### Azure Runtime Validation

1. Deploy to App Service or Container App with Managed Identity assigned.
2. Set `AZURE_KEY_VAULT_URI` in App Service configuration.
3. Confirm Key Vault secrets are loaded at startup (log output will show successful configuration provider addition).
4. Confirm `AzureOpenAi:ApiKey`, `CosmosDb:AuthKey`, `VectorStore:ApiKey` resolve from Key Vault.
5. Confirm `EntraId:ClientSecret` resolves from Key Vault (or is absent if Managed Identity is used).

### Azure Functions Validation

1. Deploy `AwsRagChat.Ingestion.Azure` to Azure Functions.
2. Set `AZURE_KEY_VAULT_URI` in Function App settings.
3. Upload a test document to the blob trigger container.
4. Confirm `BlobStorageIngestionTrigger` fires and completes without credential-related exceptions.

### Local Development Validation

1. Set local secrets via `dotnet user-secrets`.
2. Remove all values from `appsettings.json` P0/P1 fields.
3. Confirm application starts locally using User Secrets as source.

### Git Hygiene Validation

```powershell
git log --all --full-history -- "**/local.settings.json"
git log --all --full-history -- "**/appsettings.Production.json"
```

Confirm no committed files contain real credentials.

---

## Definition Of Done

- [ ] `Amazon.Extensions.Configuration.SystemsManager` integrated into `AwsRagChat.Api` and `AwsRagChat.Ingestion.Aws` startup
- [ ] `Azure.Extensions.AspNetCore.Configuration.Secrets` integrated into `AwsRagChat.Api` and `AwsRagChat.Ingestion.Azure` startup
- [ ] Secret attachment is gated on `CloudProvider` value — no cross-provider leakage
- [ ] All P0 secrets removed from `appsettings.json` and `local.settings.json`
- [ ] All P1 endpoint values removed from committed config files
- [ ] `.gitignore` confirmed to exclude sensitive config files
- [ ] Build passes with 0 errors, 0 warnings
- [ ] AWS IAM policy documented and applied to runtime role
- [ ] Azure Key Vault RBAC assignment documented and applied to Managed Identity
- [ ] Local development workflow documented and validated
- [ ] No changes to Application layer, Domain layer, Options classes, or DI extension methods
