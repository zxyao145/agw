# Agentflow Trace Cleanup and Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Do not dispatch subagents for this repository task.

**Goal:** Remove persisted agentflow traces with their owning project contexts and export node execution activities without workflow agent-name tagging.

**Architecture:** Task application services delete `AgentflowTrace` rows through the existing generic repository before deleting or clearing their owning context/project. The node execution `ActivitySource` moves under the registered `Agw.*` namespace, while `ObservabilityMiddleware` returns to logging-only behavior.

**Tech Stack:** .NET 10, EF Core `ExecuteDeleteAsync`, xUnit v3, `System.Diagnostics.Activity`.

## Global Constraints

- Do not create or apply EF migrations.
- Do not change trace queue behavior or frontend/OpenAPI contracts.
- Use explicit constructors; do not introduce primary constructors.
- Do not create a Git commit unless explicitly requested.
- Preserve all unrelated workspace changes.

---

### Task 1: Clean traces from project-context operations

**Files:**
- Modify: `src/server/Agw.Tasks/Application/ProjectContextAppService.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectContextAppServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectContextsControllerTests.cs`
- Test: `tests/Agw.Host.Tests/ProjectTraceCleanupTests.cs`

**Interfaces:**
- Consumes: `IRepository<AgentflowTrace>.Queryable`
- Produces: context clear/delete operations that remove matching traces without touching another context's traces

- [ ] **Step 1: Write failing integration tests**

Add SQLite-backed tests that seed two contexts and traces, then assert:

```csharp
await service.ClearRecordsAsync(projectId, "context-1");
Assert.DoesNotContain(dbContext.AgentflowNodeExecutionTraces, trace => trace.ContextId == "context-1");
Assert.Contains(dbContext.AgentflowNodeExecutionTraces, trace => trace.ContextId == "context-2");
```

Repeat the assertion pattern for `DeleteAsync` and `DeleteAllAsync`.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --no-restore --filter ProjectTraceCleanupTests
```

Expected: FAIL because context operations leave `AgentflowTrace` rows behind.

- [ ] **Step 3: Implement minimal context cleanup**

Inject `IRepository<AgentflowTrace>` and delete with the narrowest matching predicate:

```csharp
await _traceRepository.Queryable
    .Where(trace => trace.ProjectId == context.ProjectId && trace.ContextId == context.ContextId)
    .ExecuteDeleteAsync();
```

For `DeleteAllAsync`, delete by `project.Id`. Update existing test construction helpers with `EfRepository<AgentflowTrace>`.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the same filtered Host test command. Expected: all `ProjectTraceCleanupTests` pass.

---

### Task 2: Clean traces when deleting a project

**Files:**
- Modify: `src/server/Agw.Tasks/Application/ProjectAppService.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectAppServiceTests.cs`
- Test: `tests/Agw.Host.Tests/ProjectTraceCleanupTests.cs`

**Interfaces:**
- Consumes: `IRepository<AgentflowTrace>.Queryable`
- Produces: `ProjectAppService.DeleteAsync(Guid)` that removes only the deleted project's traces

- [ ] **Step 1: Write a failing project-deletion test**

Seed traces for the target project and a second project, delete the target, and assert only the second project's trace remains.

- [ ] **Step 2: Run the test and verify RED**

Run the filtered Host test command. Expected: FAIL because project deletion currently does not touch traces.

- [ ] **Step 3: Implement minimal project cleanup**

Inject `IRepository<AgentflowTrace>` and execute:

```csharp
await _traceRepository.Queryable
    .Where(trace => trace.ProjectId == id)
    .ExecuteDeleteAsync();
```

before removing the project. Update the existing test construction helper.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the filtered Host test command. Expected: all project cleanup tests pass.

---

### Task 3: Export persistence activities and remove workflow tagging

**Files:**
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Observability/AgentflowNodeExecutionActivity.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/Middleware/ObservabilityMiddleware.cs`
- Modify: `tests/Agw.Agents.Tests/AgentflowTraceTests.cs`
- Modify: `tests/Agw.Agents.Tests/ObservabilityMiddlewareTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentflowWorkflowCompilerTests.cs`

**Interfaces:**
- Produces: `AgentflowNodeExecutionActivity.SourceName == "Agw.Agentflow.Execution.Persistence"`
- Removes: workflow executor `gen_ai.agent.name` tagging from `ObservabilityMiddleware`

- [ ] **Step 1: Write failing source-name and middleware tests**

Assert a started node activity uses the registered source namespace:

```csharp
Assert.Equal("Agw.Agentflow.Execution.Persistence", scope.Activity!.Source.Name);
```

Change the middleware workflow-span test to assert no `gen_ai.agent.name` tag is added.

- [ ] **Step 2: Run focused tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "AgentflowTraceTests|ObservabilityMiddlewareTests|AgentflowWorkflowCompilerTests"
```

Expected: source-name and no-tag assertions fail.

- [ ] **Step 3: Implement minimal Activity changes**

Rename the source constant, remove both `TagCurrentWorkflowExecutor` calls and the method, and remove the now-unused `System.Diagnostics` import/constants. Remove the obsolete workflow-compiler assertion for `gen_ai.agent.name`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the same filtered Agents test command. Expected: all selected tests pass.

---

### Task 4: Final verification

**Files:**
- Review all files changed by Tasks 1-3

- [ ] **Step 1: Run formatting/diff validation**

```bash
git diff --check
```

Expected: exit code 0.

- [ ] **Step 2: Run affected test suites**

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore
dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --no-restore
dotnet build src/server/Agw.Tasks/Agw.Tasks.csproj --no-restore
```

Expected: both test projects pass and Tasks builds with zero errors. Record the pre-existing `Agw.Tasks.Tests` broken `src/backend` project references as a verification limitation rather than changing them.

- [ ] **Step 3: Review final diff**

Confirm every changed production line implements trace cleanup, source export, or middleware-tag removal, and no migration or commit was created.
