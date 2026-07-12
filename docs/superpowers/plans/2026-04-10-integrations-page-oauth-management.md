# Integrations Page OAuth Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a real integrations management flow with persistent `AppInstance` creation, instance-based OAuth start/reconnect, callback token storage, and a two-section integrations page.

**Architecture:** Extend the integrations backend so OAuth is driven by persisted `AppInstance` records instead of static frontend templates. Refactor the callback path to resolve the target instance from backend-controlled state, then rebuild the integrations page around live `AppDefinition` and `AppInstance` data with a modal-based create-and-connect flow.

**Tech Stack:** ASP.NET Core 10, EF Core, xUnit v3, Next.js 16, React 19, TypeScript, openapi-typescript, Radix Dialog, sonner

---

## File Structure

### Backend

- Create: `src/server/Agw.Integrations/Contracts/Manager/AppInstanceCreateRequest.cs`
- Create: `src/server/Agw.Integrations/Contracts/Manager/AuthorizeStartResponse.cs`
- Modify: `src/server/Agw.Integrations/Controllers/IntegrationsController.cs`
- Modify: `src/server/Agw.Integrations/Controllers/OauthController.cs`
- Modify: `src/server/Agw.Integrations/Domain/Entities/AppDefinition.cs`
- Modify: `src/server/Agw.Integrations/IntegrationConstants.cs`
- Test: `tests/Agw.Tasks.Tests/Integrations/IntegrationsControllerTests.cs`
- Test: `tests/Agw.Tasks.Tests/Integrations/OauthControllerTests.cs`

### Frontend

- Create: `src/frontend/web/src/app/(app)/integrations/components/app-definition-card.tsx`
- Create: `src/frontend/web/src/app/(app)/integrations/components/app-instance-card.tsx`
- Create: `src/frontend/web/src/app/(app)/integrations/components/create-connection-dialog.tsx`
- Create: `src/frontend/web/src/app/(app)/integrations/types.ts`
- Modify: `src/frontend/web/src/app/(app)/integrations/page.tsx`
- Modify: `src/frontend/web/openapi.json`
- Modify: `src/frontend/web/src/api/openapi.d.ts`

## Task 1: Add AppInstance Create API

**Files:**
- Create: `src/server/Agw.Integrations/Contracts/Manager/AppInstanceCreateRequest.cs`
- Modify: `src/server/Agw.Integrations/Controllers/IntegrationsController.cs`
- Test: `tests/Agw.Tasks.Tests/Integrations/IntegrationsControllerTests.cs`

- [ ] **Step 1: Write the failing backend tests**

```csharp
[Fact]
public async Task CreateAppInstanceAsync_WhenRequestIsValid_ReturnsCreatedInstance()
{
    var controller = scope.CreateController();
    var request = new AppInstanceCreateRequest("github", "client-id", "client-secret", true);

    var result = await InvokeActionAsync(controller, "CreateAppInstanceAsync", request);

    var ok = Assert.IsType<OkObjectResult>(result);
    var created = ok.Value;
    Assert.Equal("github", ReadProperty<string>(created!, "AppName"));
    Assert.True(ReadProperty<bool>(created!, "HasClientSecret"));
    Assert.False(ReadProperty<bool>(created!, "IsAuthorized"));
}

[Fact]
public async Task CreateAppInstanceAsync_WhenAppDefinitionDoesNotExist_ReturnsBadRequest()
{
    var controller = scope.CreateController();
    var request = new AppInstanceCreateRequest("missing-app", "client-id", "client-secret", true);

    var result = await InvokeActionAsync(controller, "CreateAppInstanceAsync", request);

    Assert.IsType<BadRequestObjectResult>(result);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "IntegrationsControllerTests&CreateAppInstanceAsync"`

Expected: FAIL because `CreateAppInstanceAsync` and `AppInstanceCreateRequest` do not exist yet.

- [ ] **Step 3: Write the minimal implementation**

```csharp
public sealed record AppInstanceCreateRequest(
    string AppName,
    string ClientId,
    string ClientSecret,
    bool UsePkce);

[HttpPost("app-instances")]
public async Task<IActionResult> CreateAppInstanceAsync([FromBody] AppInstanceCreateRequest request)
{
    var definition = await _appDefinitionRepository.GetByIdAsync(request.AppName);
    if (definition == null || string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
    {
        return BadRequest("Invalid app instance request.");
    }

    var entity = new AppInstance
    {
        Id = Guid.NewGuid(),
        AppName = definition.Name,
        ClientId = request.ClientId.Trim(),
        ClientSecret = request.ClientSecret.Trim(),
        UsePkce = request.UsePkce,
        CreateBy = User?.Identity?.Name ?? "system",
        CreateTime = DateTime.UtcNow
    };

    await _appInstanceRepository.AddAsync(entity);
    await _unitOfWork.SaveChangesAsync();

    return Ok(Map(entity, definition, now: DateTimeOffset.UtcNow));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "IntegrationsControllerTests&CreateAppInstanceAsync"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add tests/Agw.Tasks.Tests/Integrations/IntegrationsControllerTests.cs src/server/Agw.Integrations/Contracts/Manager/AppInstanceCreateRequest.cs src/server/Agw.Integrations/Controllers/IntegrationsController.cs
git commit -m "feat: add integration app instance create api"
```

## Task 2: Add Instance-Based OAuth Start And Callback Resolution

**Files:**
- Create: `src/server/Agw.Integrations/Contracts/Manager/AuthorizeStartResponse.cs`
- Modify: `src/server/Agw.Integrations/Domain/Entities/AppDefinition.cs`
- Modify: `src/server/Agw.Integrations/IntegrationConstants.cs`
- Modify: `src/server/Agw.Integrations/Controllers/IntegrationsController.cs`
- Modify: `src/server/Agw.Integrations/Controllers/OauthController.cs`
- Test: `tests/Agw.Tasks.Tests/Integrations/OauthControllerTests.cs`

- [ ] **Step 1: Write the failing OAuth tests**

```csharp
[Fact]
public async Task AuthorizeStartAsync_WhenInstanceExists_ReturnsAuthorizeUrl()
{
    var result = await controller.AuthorizeStartAsync(appInstanceId, cancellationToken);

    var ok = Assert.IsType<OkObjectResult>(result);
    var payload = Assert.IsType<AuthorizeStartResponse>(ok.Value);
    Assert.Contains("client_id=client-id", payload.AuthorizeUrl);
    Assert.Contains("state=", payload.AuthorizeUrl);
}

[Fact]
public async Task OAuthCallback_WhenStateMapsToAppInstance_StoresTokenForThatInstance()
{
    var result = await controller.OAuthCallback(cancellationToken);

    Assert.IsType<RedirectResult>(result);
    Assert.True(await dbContext.OAuthAuthorizationTokens.AnyAsync(x => x.AppInstanceId == appInstanceId, cancellationToken));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "OauthControllerTests"`

Expected: FAIL because `AuthorizeStartAsync` and instance-based callback resolution do not exist yet.

- [ ] **Step 3: Extend app definition metadata and add authorize-start**

```csharp
public class AppDefinition
{
    public required string TokenEndpoint { get; init; }
    public string? SubjectField { get; init; }
}

public sealed record AuthorizeStartResponse(string AuthorizeUrl);

[HttpPost("app-instances/{id:guid}/authorize-start")]
public async Task<IActionResult> AuthorizeStartAsync(Guid id, CancellationToken cancellationToken)
{
    var appInstance = await _appInstanceRepository.GetByIdAsync(id);
    if (appInstance == null)
    {
        return NotFound();
    }

    var appDefinition = await _appDefinitionRepository.GetByIdAsync(appInstance.AppName);
    if (appDefinition == null)
    {
        return BadRequest("App definition not found.");
    }

    var authorizeUrl = await BuildAuthorizeUrlAsync(appInstance, appDefinition, cancellationToken);
    return Ok(new AuthorizeStartResponse(authorizeUrl));
}
```

- [ ] **Step 4: Refactor callback to resolve the persisted instance**

```csharp
private sealed record OAuthCallbackState
{
    public Guid AppInstanceId { get; init; }
    public string? IntegrationId { get; init; }
    public string? Verifier { get; init; }
    public string? State { get; init; }
    public string? CreatedAt { get; init; }
}

var appInstance = await _appInstanceRepository.Queryable
    .FirstOrDefaultAsync(instance => instance.Id == callbackState.AppInstanceId, cancellationToken);
var appDefinition = await _appDefinitionRepository.GetByIdAsync(appInstance.AppName);

var providerConfiguration = new OAuthProviderConfiguration
{
    ClientId = appInstance.ClientId,
    ClientSecret = appInstance.ClientSecret,
    TokenEndpoint = appDefinition.TokenEndpoint,
    SubjectField = appDefinition.SubjectField,
    AppInstanceId = appInstance.Id
};
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "OauthControllerTests|IntegrationsControllerTests"`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add tests/Agw.Tasks.Tests/Integrations/OauthControllerTests.cs src/server/Agw.Integrations/Contracts/Manager/AuthorizeStartResponse.cs src/server/Agw.Integrations/Controllers/IntegrationsController.cs src/server/Agw.Integrations/Controllers/OauthController.cs src/server/Agw.Integrations/Domain/Entities/AppDefinition.cs src/server/Agw.Integrations/IntegrationConstants.cs
git commit -m "feat: add instance based integration oauth flow"
```

## Task 3: Rebuild The Integrations Page Around Live APIs

**Files:**
- Create: `src/frontend/web/src/app/(app)/integrations/types.ts`
- Create: `src/frontend/web/src/app/(app)/integrations/components/app-definition-card.tsx`
- Create: `src/frontend/web/src/app/(app)/integrations/components/app-instance-card.tsx`
- Create: `src/frontend/web/src/app/(app)/integrations/components/create-connection-dialog.tsx`
- Modify: `src/frontend/web/src/app/(app)/integrations/page.tsx`

- [ ] **Step 1: Write the failing UI scaffolding by replacing static template usage**

```tsx
const [definitions, setDefinitions] = React.useState<AppDefinitionItem[]>([]);
const [instances, setInstances] = React.useState<AppInstanceItem[]>([]);

React.useEffect(() => {
  void Promise.all([
    apiGet("/api/integrations/app-definitions"),
    apiGet("/api/integrations/app-instances"),
  ]).then(([definitionResponse, instanceResponse]) => {
    setDefinitions(definitionResponse ?? []);
    setInstances(instanceResponse ?? []);
  });
}, []);
```

- [ ] **Step 2: Run frontend type generation and typecheck to verify the current page breaks before the refactor is complete**

Run: `pnpm exec tsc --noEmit`

Expected: The integrations page compile path will fail until the new types and components replace the old hard-coded prototype. Ignore unrelated repo-wide pre-existing type errors outside the integrations files and focus on eliminating any new integrations-specific errors.

- [ ] **Step 3: Add page-local types and focused components**

```tsx
export type AppDefinitionItem = ApiResponse<"/api/integrations/app-definitions", "get">[number];
export type AppInstanceItem = ApiResponse<"/api/integrations/app-instances", "get">[number];

export type CreateConnectionFormState = {
  clientId: string;
  clientSecret: string;
  usePkce: boolean;
};
```

```tsx
<section className="space-y-4">
  <header className="flex items-end justify-between gap-4">
    <div>
      <h2 className="text-xl font-semibold">Connected apps</h2>
      <p className="text-sm text-muted-foreground">Reconnect or remove persisted OAuth app instances.</p>
    </div>
  </header>
  <div className="grid gap-4 md:grid-cols-2">
    {instances.map((instance) => (
      <AppInstanceCard key={instance.id} instance={instance} onReconnect={handleReconnect} onDelete={handleDelete} />
    ))}
  </div>
</section>

<section className="space-y-4">
  <div>
    <h2 className="text-xl font-semibold">App catalog</h2>
    <p className="text-sm text-muted-foreground">Create a new app connection from the available definitions.</p>
  </div>
  <div className="grid gap-4 [grid-template-columns:repeat(auto-fit,minmax(280px,1fr))]">
    {definitions.map((definition) => (
      <AppDefinitionCard key={definition.name} definition={definition} onSelect={setSelectedDefinition} />
    ))}
  </div>
</section>
```

- [ ] **Step 4: Implement modal create-and-connect, reconnect, and delete flows**

```tsx
const created = await apiPost("/api/integrations/app-instances", {
  body: {
    appName: selectedDefinition.name,
    clientId: form.clientId,
    clientSecret: form.clientSecret,
    usePkce: form.usePkce,
  },
});

const authorizeStart = await apiPost("/api/integrations/app-instances/{id}/authorize-start", {
  params: { path: { id: created.id } },
});

const authorizeUrl = authorizeStart?.authorizeUrl ?? "";
const state = new URL(authorizeUrl).searchParams.get("state");
if (state) {
  sessionStorage.setItem(`agw.oauth2.${created.id}`, JSON.stringify({ state, integrationId: created.appName, createdAt: new Date().toISOString() }));
}
window.open(authorizeUrl, "_self");
```

```tsx
const handleReconnect = async (instance: AppInstanceItem) => {
  const payload = await apiPost("/api/integrations/app-instances/{id}/authorize-start", {
    params: { path: { id: instance.id } },
  });
  window.open(payload?.authorizeUrl ?? "", "_self");
};

const handleDelete = async (instance: AppInstanceItem) => {
  if (!window.confirm(`Delete ${instance.displayName}?`)) return;
  await apiDelete("/api/integrations/app-instances/{id}", {
    params: { path: { id: instance.id } },
  });
  await reloadData();
};
```

- [ ] **Step 5: Run targeted frontend checks**

Run: `pnpm exec oxlint src/app/(app)/integrations/page.tsx src/app/(app)/integrations/components`

Expected: PASS for the integrations page files

- [ ] **Step 6: Commit**

```bash
git add src/frontend/web/src/app/\(app\)/integrations/page.tsx src/frontend/web/src/app/\(app\)/integrations/types.ts src/frontend/web/src/app/\(app\)/integrations/components
git commit -m "feat: rebuild integrations page with live oauth management"
```

## Task 4: Refresh OpenAPI And Run Final Verification

**Files:**
- Modify: `src/frontend/web/openapi.json`
- Modify: `src/frontend/web/src/api/openapi.d.ts`

- [ ] **Step 1: Regenerate frontend contract files**

```bash
dotnet run --project src/server/Agw.Host --no-build
```

```bash
cd src/frontend/web
pnpm gen:openapi
```

- [ ] **Step 2: Verify backend test coverage**

Run: `dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj`

Expected: PASS

- [ ] **Step 3: Verify the host still builds**

Run: `dotnet build src/server/Agw.Host/Agw.Host.csproj`

Expected: PASS

- [ ] **Step 4: Verify generated paths exist**

Run: `rg -n 'api/integrations/app-definitions|api/integrations/app-instances|api/integrations/app-instances/{id}|authorize-start' src/frontend/web/openapi.json src/frontend/web/src/api/openapi.d.ts`

Expected: matching entries in both files

- [ ] **Step 5: Commit**

```bash
git add src/frontend/web/openapi.json src/frontend/web/src/api/openapi.d.ts
git commit -m "chore: refresh integrations openapi schema"
```
