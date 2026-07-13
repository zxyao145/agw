# Agent Environment Variables Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add first-class Agent definition environment variables, manage them in a sixth dialog tab, and inject them into scoped External Agent and stdio MCP processes.

**Architecture:** Store a `Dictionary<string, string>` on the Agent entity using the same JSON conversion pattern as MCP Tool Servers. Merge Agent defaults with execution variables centrally in `AgentRuntimeService`, then pass the effective dictionary into External Agent SDK options and stdio MCP transports without changing the host process environment.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core, xUnit, Next.js 16, React 19, TypeScript, Tailwind CSS, Radix/Shadcn, Node test runner.

## Global Constraints

- Preserve all unrelated dirty-worktree changes.
- Do not stage or commit; the repository forbids automatic Git commits without explicit authorization.
- Add but do not apply the authorized EF Core migration.
- Keep `Extra Settings` independent from environment variables.
- Environment-variable values remain ordinary visible strings, matching MCP Tool Server behavior.
- Never modify the host process environment.
- Use TDD for handwritten production behavior and regenerate generated artifacts only after contracts/models are correct.

---

### Task 1: Agent persistence and API contracts

**Files:**
- Modify: `src/server/Agw.Shared/Data/Entities/Agents/Agent.cs`
- Modify: `src/server/Agw.Infrastructure/Data/AgwDbContext.cs`
- Modify: `src/server/Agw.Agents/Definitions/Contracts/AgentRequests.cs`
- Modify: `src/server/Agw.Agents/Definitions/Controllers/AgentsController.cs`
- Modify: `src/server/Agw.Agents/Definitions/Domain/AgentDomainService.cs`
- Modify: `src/server/Agw.Shared/Exceptions/ErrorCodes.cs`
- Modify: `tests/Agw.Agents.Tests/AgentDomainServiceTests.cs`
- Modify: `tests/Agw.Agents.Tests/AgentRequestsTests.cs`

**Interfaces:**
- Produces: `Agent.EnvironmentVariables: Dictionary<string, string>`
- Produces: `AgentCreateRequest.EnvironmentVariables`, `AgentUpdateRequest.EnvironmentVariables`, and `AgentResponse.EnvironmentVariables`
- Produces: `ErrorCodes.InvalidAgentEnvironmentVariableName`

- [ ] **Step 1: Add failing domain and contract tests**

Add tests proving that create/update retain valid values including an empty value, null input normalizes to an empty dictionary, invalid blank/`=`/null-character keys throw `AgwException`, requests retain the map, and `AgentResponse.FromDomain` exposes it.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentDomainServiceTests|FullyQualifiedName~AgentRequestsTests"
```

Expected: compilation/test failure because `EnvironmentVariables` and its validation error do not exist.

- [ ] **Step 3: Implement the minimum entity, mapping, API, and normalization behavior**

Add the entity property:

```csharp
public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
```

Map it with the existing MCP JSON conversion pattern and column name `environment_variables`. Add nullable dictionaries at the end of create/update request parameters, return a non-null read-only dictionary in `AgentResponse`, and assign request values in both controller actions. In `AgentDomainService`, normalize null to an empty ordinal dictionary and reject keys that are blank or contain `=` or `\0` using `AgwException`.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Step 2 command. Expected: all selected tests pass.

### Task 2: Runtime merge and scoped process injection

**Files:**
- Modify: `src/server/Agw.Agents/Execution/Agents/Utils/AgentRuntimeServiceUtil.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateAiAgent.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateDefinitionAgents.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateExternalAgents.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Tools.cs`
- Modify: `src/server/Agw.Agents/Definitions/Agents/McpToolServerToolClient.cs`
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceCompositionTests.cs`
- Modify or create: `tests/Agw.Agents.Tests/McpToolServerToolClientTests.cs`

**Interfaces:**
- Consumes: `Agent.EnvironmentVariables`
- Produces: `AgentRuntimeServiceUtil.MergeEnvironmentVariables(agentVariables, executionVariables)`
- Produces: `McpToolServerToolClient.CreateEnvironmentVariables(serverVariables, effectiveAgentVariables)` if a pure helper is needed for direct testing

- [ ] **Step 1: Add failing precedence tests**

Cover these concrete maps:

```text
Agent:   SHARED=agent, AGENT_ONLY=agent
Session: SHARED=session, SESSION_ONLY=session
Result:  SHARED=session, AGENT_ONLY=agent, SESSION_ONLY=session
```

For MCP, start with `SHARED=server` and verify the effective Agent/session value wins. Keep the existing Claude Code and Codex option tests and add an Agent-definition case.

- [ ] **Step 2: Run the focused runtime tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentRuntimeServiceCompositionTests|FullyQualifiedName~McpToolServerToolClientTests"
```

Expected: compilation/test failure because the merge helpers and scoped MCP injection do not exist.

- [ ] **Step 3: Implement central merging and pass the effective dictionary**

Implement an ordinal merge that copies Agent variables first and execution variables second. Compute it inside the private `CreateAiAgentAsync` path so every overload receives definition defaults. Pass it into existing Claude Code/Codex option application and through `CreateDefinitionAgentAsync` → `CreateAgentTools` → stdio MCP transport creation. Merge MCP server values first and effective Agent/session values second. Do not call `Environment.SetEnvironmentVariable`.

- [ ] **Step 4: Run runtime and existing execution tests and verify GREEN**

Run the Step 2 command, then:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore --filter "FullyQualifiedName~ExecutionRequestsTests|FullyQualifiedName~ExecutionCommandHandlerTests"
```

Expected: all selected tests pass.

### Task 3: EF Core migration and generated API artifacts

**Files:**
- Create: `src/server/Agw.Infrastructure/Migrations/*_AddAgentEnvironmentVariables.cs`
- Create: `src/server/Agw.Infrastructure/Migrations/*_AddAgentEnvironmentVariables.Designer.cs`
- Modify: `src/server/Agw.Infrastructure/Migrations/LlmDbContextModelSnapshot.cs`
- Modify (generated): `src/clients/web/openapi.json`
- Modify (generated): `src/clients/web/src/api/openapi.d.ts`

**Interfaces:**
- Consumes: the Task 1 EF model and API contracts
- Produces: a non-null `agent.environment_variables` JSON-text column with `{}` for existing rows and generated TypeScript `Record<string, string>` contracts

- [ ] **Step 1: Generate the authorized migration**

Run:

```bash
dotnet ef migrations add AddAgentEnvironmentVariables -p src/server/Agw.Infrastructure -s src/server/Agw.Host
```

Do not run `dotnet ef database update`.

- [ ] **Step 2: Inspect the generated migration**

Verify `Up` adds `environment_variables` to `agent` as non-null text with `{}` for existing rows and `Down` removes only that column. Patch the migration surgically if the generated default is not `{}`.

- [ ] **Step 3: Regenerate OpenAPI**

Start the worktree backend on an unused local port with an isolated temporary SQLite database, fetch `/openapi/v1.json` into `src/clients/web/openapi.json`, stop the server, then run:

```bash
cd src/clients/web
pnpm gen:api
```

Expected: Agent create/update/response schemas contain `environmentVariables` as an object with string values.

### Task 4: Environment-variable editor and dialog state

**Files:**
- Create: `src/clients/web/src/app/(app)/(agents)/agents/components/agent-environment-variables.ts`
- Create: `src/clients/web/src/app/(app)/(agents)/agents/components/agent-environment-variables.test.ts`
- Create: `src/clients/web/src/app/(app)/(agents)/agents/components/agent-environment-variables-editor.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/components/agent-form-fields.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/components/create-agent-dialog.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/components/edit-agent-dialog.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/components/types.ts`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/page.tsx`
- Modify: `src/clients/web/src/types/agentflow.ts`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/components/agent-dialog-layout.test.ts`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/page.test.ts`

**Interfaces:**
- Produces: `AgentEnvironmentVariableEntry { key: string; value: string }`
- Produces: `normalizeAgentEnvironmentVariables(entries): Record<string, string>`
- Produces: `getAgentEnvironmentVariablesError(entries): string | null`
- Produces: `toAgentEnvironmentVariableEntries(record): AgentEnvironmentVariableEntry[]`

- [ ] **Step 1: Add failing pure-helper and dialog source tests**

Test empty values, trimming keys, duplicate trimmed keys, blank keys, `=`, null characters, record-to-row conversion, the sixth `Environment Variables` tab, create/update request inclusion, edit initialization, and successful-create reset.

- [ ] **Step 2: Run the frontend tests and verify RED**

Run:

```bash
cd src/clients/web
node --test --experimental-strip-types \
  'src/app/(app)/(agents)/agents/components/agent-environment-variables.test.ts' \
  'src/app/(app)/(agents)/agents/components/agent-dialog-layout.test.ts' \
  'src/app/(app)/(agents)/agents/page.test.ts'
```

Expected: module/assertion failures because the helper and sixth tab do not exist.

- [ ] **Step 3: Implement the list editor and state wiring**

Add a tab containing an Add button, an empty state, and controlled Key/Value rows with remove buttons. Show one validation message and block Create/Update while invalid. Add create/edit row state to `page.tsx`, initialize edit rows from `AgentDto.environmentVariables`, send normalized records in both requests, and clear state after successful mutations.

- [ ] **Step 4: Run all focused Agent frontend tests and verify GREEN**

Run:

```bash
cd src/clients/web
node --test --experimental-strip-types \
  'src/app/(app)/(agents)/agents/components/agent-dialog-layout.test.ts' \
  'src/app/(app)/(agents)/agents/components/agent-extra-settings.test.ts' \
  'src/app/(app)/(agents)/agents/components/agent-environment-variables.test.ts' \
  'src/app/(app)/(agents)/agents/components/app-selector.test.ts' \
  'src/app/(app)/(agents)/agents/page.test.ts'
```

Expected: all focused tests pass.

### Task 5: Final verification and browser acceptance

**Files:**
- Verify all files from Tasks 1–4

**Interfaces:**
- Consumes: completed backend, generated contracts, and frontend dialog behavior
- Produces: verification evidence only; no commit or deployment

- [ ] **Step 1: Format only touched handwritten files**

Run `dotnet format` only if it can be scoped safely; otherwise rely on build formatting enforcement. Run `pnpm exec oxfmt` with the explicit touched frontend file list, then restore CRLF line endings to match this worktree.

- [ ] **Step 2: Run backend verification**

Run:

```bash
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj --no-restore
dotnet build Agw.slnx --no-restore
```

Expected: zero test/build failures. Report existing package warnings separately.

- [ ] **Step 3: Run frontend verification**

Run the Task 4 focused tests plus:

```bash
cd src/clients/web
pnpm lint
pnpm format:check
pnpm exec tsc --noEmit
```

Record any pre-existing unrelated TypeScript failures precisely rather than claiming a clean full typecheck.

- [ ] **Step 4: Browser-test Create and Edit**

Using the authorized local password, open the worktree web app, create an Agent with at least two variables including an empty value, edit it, verify values are returned, update/remove a value, save, reopen, and confirm persistence. Also verify tab scrolling and that existing Select/Dropdown wheel scrolling still works.

- [ ] **Step 5: Review the final diff**

Run:

```bash
git status --short
git diff --check
git diff --stat
```

Confirm every changed line maps to the approved Agent environment-variable feature or the earlier uncommitted Agent dialog work. Do not stage or commit.
