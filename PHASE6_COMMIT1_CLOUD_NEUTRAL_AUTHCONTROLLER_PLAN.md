# Phase 6 Commit 1 Plan: Cloud-Neutral AuthController & Claim Resolution

This plan details the refactoring of `AuthController` to resolve the Amazon Cognito DI dependency crash in Azure mode, and the standardization of role claim lookup inside `UserRoleController`.

---

## Current AuthController Analysis

### Dependency Graph
At startup, the ASP.NET Core MVC framework scans and registers all controllers. When a request is routed, the controller activator resolves the controller constructor parameters:
```
[HTTP Request] -> ControllerActivator 
                      -> AuthController(IAmazonCognitoIdentityProvider, IUserApprovalService, IConfiguration)
```

### Cognito Dependency Path
The constructor of `AuthController` requests `IAmazonCognitoIdentityProvider` directly:
```csharp
public AuthController(
    IAmazonCognitoIdentityProvider cognito,
    IUserApprovalService userApprovalService,
    IConfiguration configuration)
{
    _cognito = cognito;
    ...
}
```

### Azure Failure Mechanism
In Azure mode (`CloudProvider = "Azure"`), the application calls `AddAzureProviderInfrastructure` which registers Cosmos DB, Blob Storage, Azure OpenAI, Azure AI Search, and Entra ID (Microsoft Graph). It **does not** register `IAmazonCognitoIdentityProvider`.
Because `AuthController` is scanned and resolved unconditionally by ASP.NET Core, trying to activate `AuthController` or evaluate routes throws a runtime `DependencyResolutionException` because `IAmazonCognitoIdentityProvider` is missing from the DI container, crashing the API.

---

## Design Options

### Option A: Dynamic Dependency Resolution via `[FromServices]` (Recommended)
Refactor `AuthController` to remove `IAmazonCognitoIdentityProvider` from the constructor, resolving it directly in the `Register` endpoint using the `[FromServices]` parameter binding.

*   **Pros:**
    *   **Simple & Localized:** Resolves the issue entirely in the API layer without introducing new interfaces or altering Application/Domain contracts.
    *   **Zero Startup DI Blocker:** Since `IAmazonCognitoIdentityProvider` is not in the constructor, ASP.NET Core activates the controller in Azure mode without error.
    *   **Clear Runtime Behavior:** If `CloudProvider == "Azure"`, the endpoint short-circuits and returns `501 Not Implemented` immediately without evaluating Cognito.
*   **Cons:**
    *   Injects a service directly into an action method (method injection), which differs from standard constructor injection.
*   **Risks:**
    *   Extremely low risk; uses standard ASP.NET Core parameter binding.

### Option B: Introduce an `IUserRegistrationService` Abstraction
Define a provider-neutral interface `IUserRegistrationService` in the Application layer, with `CognitoUserRegistrationService` (AWS) and `EntraUserRegistrationService` (Azure) implementations in the Infrastructure layer.

*   **Pros:**
    *   Standardizes user registration behind a common interface, keeping clean constructor injection in `AuthController`.
*   **Cons:**
    *   Requires modifying the Application layer (violating the rule to avoid Application-layer modifications).
    *   Adds unnecessary architectural boilerplate for a single endpoint that is functionally unsupported in Azure.
*   **Risks:**
    *   Increases complexity and introduces new interface definitions into the shared codebase.

### Option C: Register Mock Cognito Client in Azure Mode
Register a dummy/mock version of `IAmazonCognitoIdentityProvider` in the DI container during Azure mode startup.

*   **Pros:**
    *   Requires no changes to the `AuthController` signature.
*   **Cons:**
    *   Requires either creating a mock wrapper or adding a dependency on a mock library (like Moq/NSubstitute) in production code.
    *   Registers a massive client interface purely to satisfy a constructor that won't use it.
*   **Risks:**
    *   High complexity and potential namespace confusion in production builds.

---

## Recommended Design

**Option A** is the preferred design. It is localized, self-contained within the API layer, and does not require modifying Application layer contracts or Domain model structures.

### Authentication Controller Strategy
1.  **Remove Cognito Constructor Parameter:** Modify the `AuthController` constructor to inject only `IUserApprovalService` and `IConfiguration`.
2.  **Short-Circuit Azure Mode:** At the start of the `Register` method, check configuration:
    ```csharp
    if (string.Equals(_configuration["CloudProvider"], "Azure", StringComparison.OrdinalIgnoreCase))
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "User registration is managed externally via Microsoft Entra ID portal or IT provisioning. API signup endpoint is not supported in Azure mode."
        });
    }
    ```
3.  **Bind via `[FromServices]`:** Bind the Cognito dependency using parameter injection for the register action:
    ```csharp
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterRequest request,
        [FromServices] IAmazonCognitoIdentityProvider cognito)
    ```

### UserRoleController Claim Mapping Alignment
1.  **Lookup Fallback Path:** In `UserRoleController.cs`, update role parsing in both `GetCurrentUserRole` and `AssignRole` to prioritize standard role claims (`ClaimTypes.Role`) before checking the static configuration claim or the legacy `"cognito:groups"` claim:
    ```csharp
    var role = User.Claims
        .Where(x => x.Type == ClaimTypes.Role)
        .Select(x => x.Value)
        .FirstOrDefault()
        ?? User.Claims
        .Where(x => x.Type == IdentityOptions.GroupsClaimType)
        .Select(x => x.Value)
        .FirstOrDefault()
        ?? User.Claims
        .Where(x => x.Type == "cognito:groups")
        .Select(x => x.Value)
        .FirstOrDefault()
        ?? string.Empty;
    ```

---

## Files Expected To Change

*   `AwsRagChat.Api/Controllers/AuthController.cs`
*   `AwsRagChat.Api/Controllers/UserRoleController.cs`

---

## Validation Plan

### AWS Validation
1.  **Startup check:** Configure `"CloudProvider": "AWS"`. Verify API boots.
2.  **Registration check:** POST to `/api/auth/register` and verify that a Cognito user account is successfully created, confirmed, and registered as a pending user in DynamoDB.
3.  **Role check:** Generate a Cognito JWT, call `/api/userrole`, and verify it successfully extracts roles via the `"cognito:groups"` claim type.

### Azure Validation
1.  **Startup check:** Configure `"CloudProvider": "Azure"`. Verify API boots cleanly with no dependency errors.
2.  **Registration check:** POST to `/api/auth/register` and verify it returns `501 Not Implemented` with the warning message.
3.  **Role check:** Generate an Azure Entra ID token containing group mappings. Set up user group mappings in `appsettings.json`. Call `/api/userrole` and verify it successfully parses roles mapped to `ClaimTypes.Role` (e.g. "Admin").

---

## Definition Of Done

*   API project builds successfully without warnings.
*   `AuthController` starts in Azure mode with no `IAmazonCognitoIdentityProvider` dependency error.
*   `/api/auth/register` returns `501 Not Implemented` in Azure mode and works normally in AWS mode.
*   `UserRoleController` successfully extracts mapped group claims into readable roles in both AWS and Azure.
