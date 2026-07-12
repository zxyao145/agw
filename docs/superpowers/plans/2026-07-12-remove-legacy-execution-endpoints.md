# Remove Legacy Execution Endpoints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `GET /api/executions/{agentId:guid}/ws`, `POST /api/executions/{id:guid}/execute`, and code used only by those endpoints while preserving the SignalR execution flow.

**Architecture:** Keep the transport-neutral command contracts, runtime starter, runtime sessions, turn runner, and SignalR connection registry. Delete the MVC controller and legacy WebSocket dispatch pipeline, then relocate Web message/HumanGate helpers away from the obsolete `execution-ws` module. Mobile remains unchanged by explicit user direction.

**Tech Stack:** .NET 10, ASP.NET Core, SignalR, xUnit, Next.js, TypeScript, Node test runner, pnpm.

## Global Constraints

- Do not change `src/clients/mobile`.
- Do not create an EF Core migration.
- Do not create a Git commit.
- Preserve `SettingCommand`, `ExecCommand`, `ExecutionRuntimeStarter`, runtime sessions, and SignalR Hub behavior.

---

### Task 1: Lock the removal contract with a failing backend test

**Files:**
- Create: `tests/Agw.Agents.Tests/LegacyExecutionEndpointRemovalTests.cs`

**Interfaces:**
- Consumes: `Agw.Agents.Hubs.ExecutionHub` as the assembly anchor.
- Produces: regression coverage that rejects both legacy route templates and legacy-only implementation types.

- [ ] **Step 1: Write the failing test**

Add an assembly scan that asserts no MVC action exposes either legacy route and that `AgentExecutionsController`, `CommandDispatcher`, `ExecutionCommandContext`, `ExecutionConnectionState`, and the legacy command strategies are absent.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --filter FullyQualifiedName~LegacyExecutionEndpointRemovalTests`

Expected: FAIL because the controller routes and legacy-only types still exist.

- [ ] **Step 3: Keep the test unchanged until production cleanup is complete**

The test is the removal contract for Task 2.

### Task 2: Delete backend legacy endpoints and exclusive pipeline

**Files:**
- Delete: `src/server/Agw.Agents/Controllers/AgentExecutionsController.cs`
- Delete: `src/server/Agw.Agents/Controllers/AgentExecutionsController.Execute.cs`
- Delete: `src/server/Agw.Agents/Contracts/AgentExecutionRequest.cs`
- Delete: `src/server/Agw.Agents/Contracts/AgentExecutionResponse.cs`
- Delete: `src/server/Agw.Agents/Application/Execution/CommandDispatcher.cs`
- Delete: `src/server/Agw.Agents/Application/Execution/ExecutionCommandContext.cs`
- Delete: `src/server/Agw.Agents/Application/Execution/ExecutionConnectionState.cs`
- Delete: `src/server/Agw.Agents/Application/Execution/CommandStrategies/`
- Modify: `src/server/Agw.Agents/Application/Execution/ExecutionRuntimeStarter.cs`
- Modify: `src/server/Agw.Agents/DependencyInjection.cs`
- Delete/update: legacy-only tests under `tests/Agw.Agents.Tests/`

**Interfaces:**
- Consumes: existing `HubExecutionConnectionRegistry` and `IExecutionMessageSink`.
- Produces: an Agents module exposing execution only through `/api/hubs/exec`.

- [ ] **Step 1: Remove the MVC controller and synchronous REST DTOs**

Delete both partial controller files and their request/response contracts.

- [ ] **Step 2: Remove the WebSocket command pipeline**

Delete dispatcher, context, connection state, and command strategies. Remove their DI registrations and the WebSocket-specific message sink from `ExecutionRuntimeStarter`.

- [ ] **Step 3: Remove or update tests that covered only deleted legacy behavior**

Delete strategy/state tests, remove REST DTO assertions, and update structure assertions to cover only retained SignalR types.

- [ ] **Step 4: Run backend tests to verify green**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj`

Expected: PASS with zero failures.

### Task 3: Remove Web legacy helper without losing shared message utilities

**Files:**
- Delete: `src/clients/web/src/api/execution-ws.ts`
- Delete: `src/clients/web/src/api/execution-ws.test.ts`
- Modify: `src/clients/web/src/lib/execution-stream.ts`
- Modify: `src/clients/web/src/components/message/human-gate-approval.tsx`
- Test: `src/clients/web/src/api/execution-hub.test.ts`

**Interfaces:**
- Consumes: `AiMessage`, SignalR `ExecutionHubClient` raw message flow.
- Produces: transport-neutral `HumanGateRequest`, `HumanGateResponse`, user-input conversion, and message merge helpers in `execution-stream.ts`.

- [ ] **Step 1: Move retained types/helpers into `execution-stream.ts`**

Keep only utilities referenced by SignalR UI; remove URL construction, browser WebSocket lifecycle, JSON string parsing, and legacy command payload builders.

- [ ] **Step 2: Point HumanGate UI at the transport-neutral module**

Update its type-only import to `@/lib/execution-stream`.

- [ ] **Step 3: Delete obsolete WebSocket helper and tests**

Remove both files after all retained imports are redirected.

- [ ] **Step 4: Run focused Web tests**

Run: `node --experimental-strip-types --test src/api/execution-hub.test.ts`

Expected: PASS.

### Task 4: Refresh public snapshots and documentation

**Files:**
- Modify: `src/clients/web/openapi.json`
- Modify: `src/clients/web/src/api/openapi.d.ts`
- Modify: `docs/ws-flow.md`
- Modify: `docs/2.Architecture.md`

**Interfaces:**
- Consumes: backend OpenAPI output after controller removal.
- Produces: snapshots and docs with no claim that either legacy endpoint exists.

- [ ] **Step 1: Regenerate the Web OpenAPI snapshot/types**

Run the backend locally, fetch `/openapi/v1.json` into the tracked snapshot using the repository's existing generation workflow, then run `pnpm gen:openapi`.

- [ ] **Step 2: Remove legacy protocol documentation**

Document SignalR as the execution transport and remove references directing Web callers to `execution-ws.ts`.

- [ ] **Step 3: Verify no non-mobile legacy references remain**

Run: `rg -n 'api/executions/.+(ws|execute)|execution-ws|AgentExecutionsController|CommandDispatcher|ExecutionCommandContext|ExecutionConnectionState' src/server src/clients/web tests docs`

Expected: no legacy implementation or route references except this historical implementation plan.

### Task 5: Full verification

**Files:**
- Verify all files changed above.

**Interfaces:**
- Consumes: completed backend/Web cleanup.
- Produces: build and test evidence.

- [ ] **Step 1: Run backend verification**

Run: `dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj`, `dotnet test tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj`, and `dotnet build Agw.slnx --no-restore`.

- [ ] **Step 2: Run Web verification**

Run from `src/clients/web`: `node --experimental-strip-types --test src/api/execution-hub.test.ts`, `pnpm lint`, `pnpm format:check`, and `pnpm build`.

- [ ] **Step 3: Inspect the final diff**

Run: `git diff --check` and `git status --short`.

Expected: no whitespace errors, no accidental mobile changes, and no generated runtime artifacts.
