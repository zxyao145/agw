# AgentRun Low-Risk Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `src/server/Agw.Agents/Application/AgentRun/` easier to read by separating execution, session-state, external-agent, definition-agent, tools, and skills responsibilities without changing `IAgentRuntimeService` or caller behavior.

**Architecture:** Keep `AgentRuntimeService` as the public orchestration service. First split the existing partial class methods into responsibility-focused files with no behavior change, then extract cache-backed agent session persistence into `AgentSessionStateStore` and inject it into `AgentRuntimeService`. Public APIs remain unchanged.

**Tech Stack:** C#/.NET 10, ASP.NET Core DI, Microsoft.Agents.AI, HybridCache, xUnit tests in `tests/Agw.Agents.Tests`.

---

## File Structure

- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`
  - Keep constructor, fields, and public class declaration only.
  - Add `AgentSessionStateStore` constructor dependency.
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Execution.cs`
  - Move `ExecuteStreamingAsync`, `ExecuteByNameAsync`, `ExecuteByIdAsync`, `ExecuteAsync`, and `CollectStreamingMessagesAsync` here.
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Session.cs`
  - Keep project-task session creation and Codex provider session binding logic.
  - Replace direct cache session logic with `AgentSessionStateStore` calls.
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.ExternalAgents.cs`
  - Move `TryCreateExternalAgent`, `CreateClaudeCodeAgent`, `CreateCodexAgent`, `IsCodexExternalAgent`, and `IsEmptyJsonObject` here.
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.DefinitionAgents.cs`
  - Move `CreateDefinitionAgentAsync`, `CreateOpenAiAgent`, `CreateAnthropicAgent`, and `ResolveApiKey` here.
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Tools.cs`
  - Move `CreateAgentTools`, `AddUniqueTools`, and `ListToolsByAgentAsync` here.
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Skills.cs`
  - Move `CreateSkillsProviderAsync`, `GetSkillAbsolutePath`, and `GetWebRootPath` here.
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentSessionStateStore.cs`
  - Own cache-backed session restore and save behavior.
- Modify: `src/server/Agw.Agents/DependencyInjection.cs`
  - Register `AgentSessionStateStore` as scoped.
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceDependencyTests.cs`
  - Update constructor-dependency expectations to include `AgentSessionStateStore`.
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceCompositionTests.cs`
  - Update `CreateRuntimeServiceForReflection()` to pass the new store dependency.
- Create or modify: `tests/Agw.Agents.Tests/AgentSessionStateStoreTests.cs`
  - Add focused tests for session-state behavior.

---

### Task 1: Add session-state store tests

**Files:**
- Create: `tests/Agw.Agents.Tests/AgentSessionStateStoreTests.cs`

- [ ] **Step 1: Create focused tests for cache-backed session behavior**

Create `tests/Agw.Agents.Tests/AgentSessionStateStoreTests.cs` with this content:

```csharp
using System.Text.Json;

using Agw.Agents.Application.AgentRun;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentSessionStateStoreTests
{
    [Fact]
    public async Task GetOrCreateAsync_WhenAgentIsExternal_DoesNotReadCache()
    {
        var cache = new ThrowingHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.External };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheIsEmpty_CreatesNewSession()
    {
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.Custom };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheContainsSerializedSession_DeserializesSession()
    {
        var cache = new InMemoryHybridCache();
        await cache.SetAsync("task-1", "{\"id\":\"cached\"}", cancellationToken: TestContext.Current.CancellationToken);
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.Custom };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.DeserializedSession, session);
        Assert.Equal(0, aiAgent.CreateSessionCallCount);
        Assert.Equal(1, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenCacheContainsInvalidJson_CreatesNewSession()
    {
        var cache = new InMemoryHybridCache();
        await cache.SetAsync("task-1", "not-json", cancellationToken: TestContext.Current.CancellationToken);
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var agent = new Agent { Type = AgentType.Custom };
        var aiAgent = new TestAIAgent();

        var session = await store.GetOrCreateAsync(
            agent,
            aiAgent,
            "task-1",
            TestContext.Current.CancellationToken);

        Assert.Same(aiAgent.CreatedSession, session);
        Assert.Equal(1, aiAgent.CreateSessionCallCount);
        Assert.Equal(0, aiAgent.DeserializeSessionCallCount);
    }

    [Fact]
    public async Task SaveAsync_SerializesSessionIntoCache()
    {
        var cache = new InMemoryHybridCache();
        var store = new AgentSessionStateStore(cache, NullLogger<AgentSessionStateStore>.Instance);
        var aiAgent = new TestAIAgent();
        var session = await aiAgent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await store.SaveAsync("task-1", aiAgent, session, TestContext.Current.CancellationToken);

        var serialized = await cache.GetOrCreateAsync<string>(
            "task-1",
            _ => ValueTask.FromResult(string.Empty),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("{\"id\":\"created\"}", serialized);
    }

    private sealed class TestAIAgent : AIAgent
    {
        public AgentSession CreatedSession { get; } = new TestAgentSession("created");
        public AgentSession DeserializedSession { get; } = new TestAgentSession("cached");
        public int CreateSessionCallCount { get; private set; }
        public int DeserializeSessionCallCount { get; private set; }

        public override string? Name => "test-agent";
        public override string? Description => null;

        public override Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        {
            CreateSessionCallCount++;
            return Task.FromResult(CreatedSession);
        }

        public override Task<AgentSession> DeserializeSessionAsync(JsonElement serializedSession, CancellationToken cancellationToken = default)
        {
            DeserializeSessionCallCount++;
            return Task.FromResult(DeserializedSession);
        }

        public override Task<JsonElement> SerializeSessionAsync(AgentSession session, CancellationToken cancellationToken = default)
        {
            var id = Assert.IsType<TestAgentSession>(session).Id;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { id }));
        }

        public override IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<AgentResponseUpdate>();
    }

    private sealed class TestAgentSession(string id) : AgentSession
    {
        public string Id { get; } = id;
    }

    private sealed class ThrowingHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cache should not be read for external agents.");

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cache should not be written in this test.");

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class InMemoryHybridCache : HybridCache
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public override ValueTask<T> GetOrCreateAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            if (_values.TryGetValue(key, out var value))
            {
                return ValueTask.FromResult((T)value!);
            }

            return CreateAsync();

            async ValueTask<T> CreateAsync()
            {
                var created = await factory(cancellationToken);
                _values[key] = created;
                return created;
            }
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Run the new test file and verify it fails because the store does not exist**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~AgentSessionStateStoreTests"
```

Expected: FAIL with a compile error similar to `The type or namespace name 'AgentSessionStateStore' could not be found`.

---

### Task 2: Implement `AgentSessionStateStore`

**Files:**
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentSessionStateStore.cs`
- Modify: `src/server/Agw.Agents/DependencyInjection.cs`

- [ ] **Step 1: Add the session-state store**

Create `src/server/Agw.Agents/Application/AgentRun/AgentSessionStateStore.cs`:

```csharp
using System.Text.Json;

using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.AgentRun;

public sealed class AgentSessionStateStore
{
    private readonly HybridCache _cache;
    private readonly ILogger<AgentSessionStateStore> _logger;

    public AgentSessionStateStore(HybridCache cache, ILogger<AgentSessionStateStore> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<AgentSession> GetOrCreateAsync(
        Agent agent,
        AIAgent aiAgent,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (agent.Type == AgentType.External)
        {
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var serialized = await _cache.GetOrCreateAsync<string>(
            taskId,
            _ => ValueTask.FromResult(string.Empty),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(serialized))
        {
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var serializedSession = JsonSerializer.Deserialize<JsonElement>(serialized);
            return await aiAgent.DeserializeSessionAsync(serializedSession, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Agent session cache deserialization failed for task {TaskId}. A new session will be created.",
                taskId);
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(
        string taskId,
        AIAgent aiAgent,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var serializedSession = await aiAgent.SerializeSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var serialized = JsonSerializer.Serialize(serializedSession);
        await _cache.SetAsync(taskId, serialized, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 2: Register the store in DI**

Open `src/server/Agw.Agents/DependencyInjection.cs` and add this registration next to the existing runtime service registrations:

```csharp
services.AddScoped<AgentSessionStateStore>();
```

The nearby block should include:

```csharp
services.AddScoped<LoggingMiddleware>();
services.AddScoped<AgentSessionStateStore>();
services.AddScoped<IAgentRuntimeService, AgentRuntimeService>();
```

If `LoggingMiddleware` is not directly adjacent, add `AgentSessionStateStore` immediately before `IAgentRuntimeService`.

- [ ] **Step 3: Run the focused tests and verify they pass**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~AgentSessionStateStoreTests"
```

Expected: PASS for all `AgentSessionStateStoreTests` tests.

---

### Task 3: Wire `AgentRuntimeService` to the session-state store

**Files:**
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Session.cs`
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceDependencyTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceCompositionTests.cs`

- [ ] **Step 1: Add the store dependency to `AgentRuntimeService`**

In `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`, add a field:

```csharp
private readonly AgentSessionStateStore _sessionStateStore;
```

Update the constructor signature so the final parameters are:

```csharp
IAgwFileSystemResolver fileSystemResolver,
AgentSessionStateStore sessionStateStore,
ILogger<AgentRuntimeService> logger,
LoggingMiddleware loggingMiddleware)
```

Assign the field in the constructor:

```csharp
_sessionStateStore = sessionStateStore;
```

- [ ] **Step 2: Replace session cache calls in project-task session creation**

In `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Session.cs`, replace:

```csharp
var agentSession = await GetOrCreateThreadAsync(agent, aiAgent, taskIdString, cancellationToken);
```

with:

```csharp
var agentSession = await _sessionStateStore.GetOrCreateAsync(agent, aiAgent, taskIdString, cancellationToken);
```

- [ ] **Step 3: Replace direct execution session restore and save calls**

In `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`, replace:

```csharp
var session = await CreateOrRestoreSessionAsync(aiAgent, taskId).ConfigureAwait(false);
taskId ??= Guid.NewGuid();
string taskIdValue = taskId.Value.Normalize();
```

with:

```csharp
taskId ??= Guid.NewGuid();
string taskIdValue = taskId.Value.Normalize();
var session = await _sessionStateStore.GetOrCreateAsync(agent, aiAgent, taskIdValue, cancellationToken).ConfigureAwait(false);
```

Replace:

```csharp
await SaveSessionThreadStateAsync(session._taskId, session.Agent, session.Session, cancellationToken);
```

with:

```csharp
await _sessionStateStore.SaveAsync(session._taskId, session.Agent, session.Session, cancellationToken);
```

- [ ] **Step 4: Remove now-unused private session cache methods**

Delete these methods from `AgentRuntimeService.cs`:

```csharp
private async Task<AgentSession> GetOrCreateThreadAsync(
    Agent agent,
    AIAgent aiAgent,
    string taskId,
    CancellationToken cancellationToken)
{
    ...
}

private async Task SaveSessionThreadStateAsync(string taskId, AIAgent aiAgent, AgentSession session, CancellationToken cancellationToken)
{
    ...
}

private async Task<AgentSession> CreateOrRestoreSessionAsync(AIAgent aiAgent, Guid? taskId)
{
    ...
}
```

Then remove `using System.Text.Json;` from `AgentRuntimeService.cs` if it is no longer needed.

- [ ] **Step 5: Update dependency test expectations**

In `tests/Agw.Agents.Tests/AgentRuntimeServiceDependencyTests.cs`, add an assertion that the constructor contains the new dependency:

```csharp
[Fact]
public void Constructor_UsesAgentSessionStateStoreForSessionPersistence()
{
    var constructor = Assert.Single(typeof(AgentRuntimeService).GetConstructors());

    Assert.Contains(
        constructor.GetParameters(),
        parameter => parameter.ParameterType == typeof(AgentSessionStateStore));
}
```

- [ ] **Step 6: Update reflection test helper constructor call**

In `tests/Agw.Agents.Tests/AgentRuntimeServiceCompositionTests.cs`, update `CreateRuntimeServiceForReflection()` to pass the new dependency:

```csharp
return new AgentRuntimeService(
    agentAppService: null!,
    projectAppService: null!,
    toolRegistry: null!,
    cache: null!,
    chatHistoryProvider: null!,
    providerSessionState: null!,
    projectTaskSessionBindingService: null!,
    webHostEnvironment: null!,
    fileSystemResolver: null!,
    sessionStateStore: null!,
    logger: NullLogger<AgentRuntimeService>.Instance,
    loggingMiddleware: NullLogger<LoggingMiddleware>.Instance == null ? null! : new LoggingMiddleware(NullLogger<LoggingMiddleware>.Instance));
```

If the existing constructor helper already passes `loggingMiddleware`, preserve that existing argument and insert only `sessionStateStore: null!` before `logger`.

- [ ] **Step 7: Run Agw.Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: PASS.

---

### Task 4: Split execution methods into `AgentRuntimeService.Execution.cs`

**Files:**
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Execution.cs`

- [ ] **Step 1: Create the execution partial file**

Create `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Execution.cs` and move these methods from `AgentRuntimeService.cs` into it without changing method bodies except for already-applied `AgentSessionStateStore` calls:

```csharp
public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
    AgentExecSession session,
    AgwUserInput input,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)

public async Task<AgentExecutionResult?> ExecuteByNameAsync(
    AgentExecuteByNameRequest request,
    CancellationToken cancellationToken = default)

public async Task<AgentExecutionResult?> ExecuteByIdAsync(
    AgentExecuteByIdRequest request,
    CancellationToken cancellationToken = default)

private async Task<AgentExecutionResult?> ExecuteAsync(
    AgentExecuteRequest request,
    CancellationToken cancellationToken = default)

private static async Task<List<AgwMessage>> CollectStreamingMessagesAsync(
    AIAgent aiAgent,
    IReadOnlyList<ChatMessage> chatMessages,
    AgentSession session)
```

Use this header for the new file:

```csharp
using System.Runtime.CompilerServices;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Application.Agents;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Shared.Models;

using Microsoft.Agents.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    // moved methods
}
```

- [ ] **Step 2: Trim `AgentRuntimeService.cs` usings**

After moving methods, `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs` should only need usings for constructor field types. Remove usings that no longer compile as used.

- [ ] **Step 3: Run Agw.Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: PASS.

---

### Task 5: Split external-agent construction methods

**Files:**
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.CreateAiAgent.cs`
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.ExternalAgents.cs`

- [ ] **Step 1: Create the external-agent partial file**

Create `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.ExternalAgents.cs` and move these methods from `AgentRuntimeService.CreateAiAgent.cs` into it unchanged:

```csharp
private bool TryCreateExternalAgent(CreateAiAgentRequest request, Project project, out AIAgent? aiAgent)
private AIAgent? CreateClaudeCodeAgent(Project project, Guid? taskId, bool resume, IReadOnlyDictionary<string, string>? environmentVariables)
private AIAgent? CreateCodexAgent(Project project, Guid? threadId, bool resume, IReadOnlyDictionary<string, string>? environmentVariables, Func<string, CancellationToken, ValueTask>? onThreadStartedAsync)
private static bool IsEmptyJsonObject(string value)
private static bool IsCodexExternalAgent(Agent agent)
```

Use this header:

```csharp
using System.Diagnostics.CodeAnalysis;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    // moved methods
}
```

- [ ] **Step 2: Remove the moved external-agent methods from `AgentRuntimeService.CreateAiAgent.cs`**

Delete the moved methods and the `#region CreateExternalAgent` wrapper from `AgentRuntimeService.CreateAiAgent.cs`.

- [ ] **Step 3: Trim usings in both files**

Remove any usings that are no longer needed from `AgentRuntimeService.CreateAiAgent.cs` and `AgentRuntimeService.ExternalAgents.cs`.

- [ ] **Step 4: Run Agw.Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: PASS.

---

### Task 6: Split definition-agent construction methods

**Files:**
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.CreateAiAgent.cs`
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.DefinitionAgents.cs`

- [ ] **Step 1: Create the definition-agent partial file**

Create `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.DefinitionAgents.cs` and move these methods from `AgentRuntimeService.CreateAiAgent.cs` into it unchanged:

```csharp
private async Task<AIAgent?> CreateDefinitionAgentAsync(Agent agentDefinition, Project project, CancellationToken cancellationToken)
private AIAgent CreateOpenAiAgent(Agent agentDefinition, LlmModel model, Provider provider, ProviderAuthConfig authConfig, IList<AITool>? tools, AIContextProvider? skillsProvider, string? workspace)
private AIAgent CreateAnthropicAgent(Agent agentDefinition, LlmModel model, Provider provider, ProviderAuthConfig authConfig, IList<AITool>? tools, AIContextProvider? skillsProvider, string? workspace)
private string ResolveApiKey(ProviderAuthConfig authConfig)
```

Use this header:

```csharp
using System.ClientModel;

using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Exceptions;

using Anthropic;
using Anthropic.Core;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OpenAI;
using OpenAI.Chat;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    // moved methods
}
```

- [ ] **Step 2: Remove the moved definition-agent methods from `AgentRuntimeService.CreateAiAgent.cs`**

Delete the moved methods and the `#region DefinitionAgent` wrapper from `AgentRuntimeService.CreateAiAgent.cs`.

- [ ] **Step 3: Trim usings in both files**

Remove any usings that are no longer needed from `AgentRuntimeService.CreateAiAgent.cs` and `AgentRuntimeService.DefinitionAgents.cs`.

- [ ] **Step 4: Run Agw.Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: PASS.

---

### Task 7: Split tools and skills methods

**Files:**
- Modify: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.CreateAiAgent.cs`
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Tools.cs`
- Create: `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Skills.cs`

- [ ] **Step 1: Create the tools partial file**

Create `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Tools.cs` and move these methods from `AgentRuntimeService.CreateAiAgent.cs` into it unchanged:

```csharp
private async Task<IList<AITool>?> CreateAgentTools(Agent agent, Guid projectId, CancellationToken cancellationToken)
private static void AddUniqueTools(ICollection<AITool> destination, ISet<string> registeredToolNames, IEnumerable<AITool> tools)
private async Task<IReadOnlyList<McpClientTool>> ListToolsByAgentAsync(Guid agentId, CancellationToken cancellationToken)
```

Use this header:

```csharp
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    // moved methods
}
```

- [ ] **Step 2: Create the skills partial file**

Create `src/server/Agw.Agents/Application/AgentRun/AgentRuntimeService.Skills.cs` and move these methods from `AgentRuntimeService.CreateAiAgent.cs` into it unchanged:

```csharp
private async Task<AIContextProvider?> CreateSkillsProviderAsync(Guid agentId)
private string GetSkillAbsolutePath(Skill skill)
private string GetWebRootPath()
```

Use this header:

```csharp
using Agw.Shared.Data.Entities.Skills;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    // moved methods
}
```

- [ ] **Step 3: Remove the moved tools and skills methods from `AgentRuntimeService.CreateAiAgent.cs`**

Delete the moved methods from `AgentRuntimeService.CreateAiAgent.cs`.

- [ ] **Step 4: Trim usings in all touched files**

After this task, `AgentRuntimeService.CreateAiAgent.cs` should mainly contain:

```csharp
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.Agents.AI;
```

Keep additional usings only if the compiler requires them.

- [ ] **Step 5: Run Agw.Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: PASS.

---

### Task 8: Final verification and cleanup

**Files:**
- Modify as needed only in files touched by previous tasks.

- [ ] **Step 1: Run formatting check/build-relevant tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: PASS.

- [ ] **Step 2: Run broader related tests**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests --filter "FullyQualifiedName~AgentRuntimeService"
dotnet test tests/Agw.A2A.Tests --filter "FullyQualifiedName~DependencyInjection"
```

Expected: PASS for both commands.

- [ ] **Step 3: Review diff for behavior-only risks**

Run:

```bash
git diff -- src/server/Agw.Agents/Application/AgentRun src/server/Agw.Agents/DependencyInjection.cs tests/Agw.Agents.Tests
```

Expected: Diff shows method moves, `AgentSessionStateStore` extraction, DI registration, and tests. No public `IAgentRuntimeService` signature changes.

- [ ] **Step 4: Confirm public interface stayed unchanged**

Run:

```bash
git diff -- src/server/Agw.Agents/Application/AgentRun/IAgentRuntimeService.cs
```

Expected: No diff.

---

## Self-Review

- Spec coverage: The plan covers low-risk partial-file reorganization, session cache extraction, no public interface change, test updates, and focused verification.
- Placeholder scan: No placeholder steps are intentionally left; every code-changing task identifies exact files and code/methods to move or add.
- Type consistency: The plan consistently uses `AgentSessionStateStore`, `GetOrCreateAsync`, and `SaveAsync`; existing public `IAgentRuntimeService` method names remain unchanged.
