# ExecuteWsAsync Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor `ExecuteWsAsync` so command dispatch, setting/session lifecycle, and active execution coordination are easier to read and maintain.

**Architecture:** Keep the WebSocket entrypoint thin and move connection-level state transitions into a focused coordinator that tracks current settings, session-affinity settings, and the currently running execution. Reintroduce a small `ActiveExecution` handle so interrupt, cleanup, and “busy” checks are consistent for both agent and agentflow execution paths.

**Tech Stack:** ASP.NET Core, xUnit v3, WebSocket streaming, existing `AgentExecSession` runtime abstractions

---

### Task 1: Add regression tests for connection state rules

**Files:**
- Create: `tests/Agw.Agents.Tests/ExecutionConnectionStateTests.cs`
- Modify: `src/backend/Agw.Agents/Properties/AssemblyInfo.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task ApplySettings_WhenExecutionIsRunning_DefersImmediateSessionRefresh()

[Fact]
public async Task ApplySettings_WhenSettingsChangedWhileIdle_RequiresImmediateSessionRefresh()

[Fact]
public async Task MarkExecutionStarted_WhenAnotherExecutionIsRunning_ReturnsFalse()

[Fact]
public async Task ReleaseCompletedExecutionAsync_WhenExecutionCompleted_ClearsActiveExecution()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --filter ExecutionConnectionStateTests`
Expected: FAIL because `ExecutionConnectionState` and/or `ActiveExecution` members do not exist yet.

- [ ] **Step 3: Write minimal implementation**

```csharp
internal sealed class ExecutionConnectionState
{
    // Track requested settings, session-bound settings, and active execution.
}

public sealed class ActiveExecution : IAsyncDisposable
{
    // Wrap execution task and cancellation source.
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --filter ExecutionConnectionStateTests`
Expected: PASS

### Task 2: Refactor controller command loop to use the connection state

**Files:**
- Modify: `src/backend/Agw.Agents/Controllers/AgentExecutionsController.cs`

- [ ] **Step 1: Write the failing test**

Use the regression tests from Task 1 as the safety net for the state rules before touching the controller flow.

- [ ] **Step 2: Run test to verify it still fails or is the active guard**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --filter ExecutionConnectionStateTests`
Expected: PASS before refactor, then remain PASS throughout controller changes.

- [ ] **Step 3: Write minimal implementation**

```csharp
while (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
{
    await state.ReleaseCompletedExecutionAsync();
    var command = await ReceiveRequestAsync<AgentRunCommand>(webSocket, cancellationToken);
    if (command == null) { ... }
    await HandleCommandAsync(...);
}
```

Split handling into:
- `HandleSettingCommandAsync`
- `HandleExecCommandAsync`
- `HandleInterruptCommandAsync`
- `DisposeCurrentSessionAsync`

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --filter ExecutionConnectionStateTests`
Expected: PASS

### Task 3: Verify project-level regression safety

**Files:**
- Modify: `src/backend/Agw.Agents/Controllers/AgentExecutionsController.cs`
- Create or restore if needed: `src/backend/Agw.Agents/Application/ActiveExecution.cs`

- [ ] **Step 1: Run targeted backend tests**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj`
Expected: PASS

- [ ] **Step 2: Run solution-level agent tests if runtime allows**

Run: `dotnet test Agw.slnx --filter Agw.Agents.Tests`
Expected: PASS
