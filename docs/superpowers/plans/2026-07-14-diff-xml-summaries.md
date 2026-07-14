# Current Diff XML Summaries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add concise Chinese XML `<summary>` documentation to every production method or constructor newly added or behaviorally modified by the current staged and unstaged diff.

**Architecture:** Documentation-only changes remain next to the affected declarations. Tests, deleted methods, properties, fields, records, and files whose diff only changes imports are excluded.

**Tech Stack:** C# 14, .NET 10, XML documentation comments, xUnit verification.

## Global Constraints

- Preserve runtime behavior and method signatures.
- Add `<summary>` only; do not add empty `<param>` or `<returns>` tags.
- Describe intent and important behavioral boundaries, not implementation syntax.
- Do not stage or commit changes.

---

### Task 1: Agentflow builders and extracted helpers

**Files:**
- Modify: `src/server/Agw.Agents/Execution/Agentflows/AgentflowMessageTransforms.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/AgentflowNodeScopedAgent.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Builders/AgentflowBlockBuildContext.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Builders/AgentflowBlockBuildSupport.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Builders/ConcurrentBlockBuilder.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Builders/GroupChatBlockBuilder.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Builders/HandoffBlockBuilder.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/Builders/MagenticBlockBuilder.cs`

- [ ] **Step 1:** Add a Chinese `<summary>` above each constructor and method declaration in these newly extracted production files.
- [ ] **Step 2:** Confirm each summary explains the declaration's responsibility, including participant resolution, workflow binding, role reassignment, session initialization, tracing, and each orchestration strategy.

### Task 2: Existing Agentflow and agent runtime methods changed by the diff

**Files:**
- Modify: `src/server/Agw.Agents/Execution/Agentflows/AgentflowRuntimeService.cs`
- Modify: `src/server/Agw.Agents/Execution/Agentflows/AgentflowWorkflowCompiler.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateRuntime.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Execution.cs`
- Modify: `src/server/Agw.Agents/Execution/Runtimes/RuntimeFactory.cs`
- Modify: `src/server/Agw.Jobs/Application/Services/AgentExecutor.cs`

- [ ] **Step 1:** Add or retain a meaningful Chinese `<summary>` for each production method whose implementation changed.
- [ ] **Step 2:** Do not annotate methods that only share a file with the diff or methods deleted by the refactor.

### Task 3: Context normalization and SQLite compatibility methods

**Files:**
- Modify: `src/server/Agw.Shared/Utils/ContextIdUtil.cs`
- Modify: `src/server/Agw.Tasks/Application/ProjectContextAppService.cs`
- Modify: `src/server/Agw.Tasks/Application/TaskAppService.cs`
- Modify: `src/server/Agw.Tasks/Application/TaskExecutionAppService.cs`
- Modify: `src/server/Agw.Tasks/Application/TaskSessionBindingService.cs`
- Modify: `src/server/Agw.Tasks/Domain/Services/EfCoreChatHistoryProvider.cs`
- Modify: `src/server/Agw.Tasks/Infrastructure/ProjectContextUsageRecorder.cs`

- [ ] **Step 1:** Document generation, resolution, and canonical normalization of context IDs.
- [ ] **Step 2:** Document methods changed to reuse legacy differently-cased SQLite context rows.
- [ ] **Step 3:** Keep existing accurate summaries and avoid duplicate XML blocks.

### Task 4: Verification

**Files:**
- Verify all production `.cs` files in the staged and unstaged diff.

- [ ] **Step 1:** Search affected declarations and confirm each scoped method or constructor has an immediately preceding `<summary>`.
- [ ] **Step 2:** Run `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore` and expect zero failures.
- [ ] **Step 3:** Run `dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --no-restore` and expect zero failures.
- [ ] **Step 4:** Run `dotnet build Agw.slnx --no-restore` and expect zero errors.
- [ ] **Step 5:** Run `git diff --check` and confirm no whitespace errors.
