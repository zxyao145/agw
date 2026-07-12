# AGENTS.md

This file gives coding agents the minimum repo context needed to work safely in this repository.

## Project Overview

Agw is a modular ASP.NET Core backend plus a Next.js frontend for managing agents, agentflows, providers, tools, skills, projects/tasks, jobs, integrations, and external-agent/chat execution. The backend targets `.NET 10.0`, uses EF Core for persistence, and wires modules together from `src/backend/Agw.Host/Program.cs`.

## Repository Map

### Backend (`src/backend/`)

```text
Agw.Host/            # ASP.NET Core entry point, OpenAPI, static files, websockets, DI, DB seeding
Agw.Files/           # File-management APIs, path security, file request validation, error mapping
Agw.A2A/             # A2A protocol types and route builders
Agw.Agents/          # Agents, agentflows, runtime execution services
Agw.Infrastructure/  # EF Core DbContext, repositories, migrations, seeding
Agw.Integrations/    # OAuth integrations, app definitions/instances, integration tools
Agw.Jobs/            # Scheduled jobs, execution logs, hosted scheduler
Agw.Providers/       # Models, providers, model-provider links, auth configs
Agw.Shared/          # Shared entities, contracts, exceptions, repository abstractions, utilities
Agw.Skills/          # Skill archive validation, storage, and agent-skill relations
Agw.Tasks/           # Projects, project tasks, task records, and task APIs
Agw.Tools/           # Tool discovery, metadata, and AI tool factory/registry
```

Notes:

- `Agw.slnx` includes all backend projects above plus `tests/Agw.A2A.Tests`, `tests/Agw.Agents.Tests`, `tests/Agw.Files.Tests`, `tests/Agw.Shared.Tests`, `tests/Agw.Tasks.Tests`, and `tests/Agw.Skills.Tests`.
- `tests/Agw.Jobs.Tests` exists in the repo but is not currently included in `Agw.slnx`.

### Frontend (`src/frontend/web/`)

```text
src/app/(app)/
  (agents)/agents            # Agent CRUD
  (agents)/agentflows        # Workflow editor
  (agents)/mcp-tool-servers  # MCP server management
  (agents)/skills            # Skill archive management
  (external-agents)/claude-code
  (interface)/chat
  (overview)/dashboard
  (overview)/traces
  (providers)/models
  (providers)/providers
  (providers)/model-providers
  (tasks)/projects
  (tasks)/jobs
  integrations
src/api/                     # Typed fetch helpers and generated OpenAPI types
src/components/              # Shared UI components
src/hooks/                   # Shared React hooks
src/lib/                     # Shared frontend utilities
src/types/                   # Shared frontend types
```

### Other Top-Level Paths

- `docs/` contains project documentation.
- `scripts/` contains helper scripts such as API smoke tests.
- `tests/` contains xUnit projects.
- Treat `bin/`, `obj/`, `.next/`, `node_modules/`, and `TestResults/` as generated artifacts.

## Key Runtime Entry Points

- `src/backend/Agw.Host/Program.cs`: bootstraps logging, OpenTelemetry, DI modules, OpenAPI/Scalar, websockets, static files, and DB seeding.
- `src/backend/Agw.Agents/Runtime/AgentRun/AgentRuntimeService.cs`: builds runtime agents from persisted agent, provider, skill, and tool configuration.
- `src/backend/Agw.Agents/Runtime/Agentflows/AgentflowRuntimeService.cs`: executes multi-agent workflows for the supported orchestration patterns.
- `src/backend/Agw.Jobs/HostedService/JobHostedService.cs`: in-memory scheduler backed by persistent job state and execution logs.
- `src/backend/Agw.Integrations/Controllers/OauthController.cs`: OAuth authorization start/callback endpoints for integration connections.
- `src/backend/Agw.Integrations/Tools/GitHub/GitHubTools.cs`: integration-backed GitHub tool implementations exposed to runtime agents.
- `src/backend/Agw.Skills/Application/SkillAppService.cs`: validates uploaded skill archives, rewrites `SKILL.md` metadata, and manages extracted skill content under `wwwroot/skills/`.
- `src/backend/Agw.Tools/ToolRegistryService.cs`: discovers `[AiTool]` methods and `IAgwTool` implementations and exposes them as runtime AI tools.
- `src/backend/Agw.Tasks/Application/ProjectTaskAppService.cs`: coordinates project task persistence, chat history, and task lifecycle operations.
- `src/backend/Agw.Shared/Exceptions/ErrorCodes.cs`: central catalog for backend `AgwException` error codes and HTTP status mapping.

## Important Domain Concepts

- `Agent`: persisted AI agent configuration with prompt, runtime type, model-provider linkage, tool bindings, and optional skill assignments.
- `Agentflow`: multi-agent workflow graph with nodes, edges, and orchestration pattern.
- `McpToolServer`: MCP server configuration for stdio, HTTP, or SSE transport.
- `LlmModel`, `Provider`, `ModelProvider`, `ProviderAuthConfig`: provider/model catalog and authentication setup.
- `Skill`: uploaded skill archive with validated `SKILL.md` metadata plus agent-skill relations.
- `Project`, `ProjectTask`, `TaskRecord`: project-scoped execution, persisted task state, and chat history.
- `Job`, `JobLog`: scheduled background execution and per-run logging.
- `AppDefinition`, `AppInstance`, `OAuthAuthorizationToken`: integration catalog, authorized app connections, and OAuth authorization state/token persistence.

## Build, Run, And Test

### Backend

Run from the repo root:

```bash
dotnet restore Agw.slnx
dotnet build Agw.slnx
dotnet run --project src/backend/Agw.Host
dotnet watch --project src/backend/Agw.Host
dotnet test Agw.slnx
dotnet format
```

Notes:

- The development host runs on `http://localhost:5015` by default via `src/backend/Agw.Host/Properties/launchSettings.json`.
- Do not add or apply EF Core migrations automatically. When needed, use:

```bash
dotnet ef migrations add <MigrationName> \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host

dotnet ef database update \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host
```

### Frontend

Run from `src/frontend/web`:

```bash
pnpm install
pnpm dev
pnpm build
pnpm start
pnpm lint
pnpm lint:fix
pnpm format
pnpm format:check
pnpm gen:openapi
```

Notes:

- The Next.js dev server runs on `http://localhost:3000` by default.
- Linting and formatting use `oxlint` and `oxfmt`, not ESLint/Prettier.
- `src/frontend/web/next.config.ts` rewrites `/api/*` and `/openapi/*` to `BACKEND_API_BASE_URL`, then `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:5015`.
- Regenerate `src/frontend/web/src/api/openapi.d.ts` after backend contract changes.

### Tests

- Backend tests use xUnit.
- Run `dotnet test Agw.slnx` for the normal repo-wide backend test pass.
- If you touch `Agw.Jobs`, also run `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj` because that project is not currently part of `Agw.slnx`.
- Prefer test namespaces that mirror production namespaces and method names like `Method_Condition_ExpectedResult`.

## Rules

Read [`docs/rules.md`](docs/rules.md) to obtain mandatory constraint rules for all coding, and strictly adhere to each item

## Commit Conventions

Follow Conventional Commits:

- `feat:` new features
- `fix:` bug fixes
- `refactor:` code restructuring
- `chore:` maintenance tasks
- `docs:` documentation
- `test:` tests
  
   

## Configuration

Primary backend settings live in `src/backend/Agw.Host/appsettings.json`:

```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=agw.db"
  },
  "OpenTelemetry": {
    "ServiceName": "Agw",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Guidance:

- Supported database providers are `sqlite` and `postgres`.
- Keep secrets out of `appsettings*.json` and frontend env files; prefer environment-variable overrides.
- Register new backend services in the relevant module `DependencyInjection.cs` and wire the module into `src/backend/Agw.Host/Program.cs`.

## Frontend Integration Notes

- Prefer the typed helpers in `src/frontend/web/src/api/client.ts` for REST calls.
- `src/frontend/web/src/api/client.ts` unwraps Bens.Results response envelopes before data reaches pages; update that central helper when the backend result wrapper contract changes.
- Use `src/frontend/web/src/api/task-client.ts` for project-task history/session helpers.
- Use `src/frontend/web/src/api/execution-ws.ts` for task execution websocket flows.
- Use `src/frontend/web/src/api/files.ts` for backend file-management endpoints used by the UI.
- Keep route-specific UI inside the matching `src/app/(app)/...` segment and shared UI in `src/components/`.

## A2A Status

- The `Agw.A2A` module is present in the codebase, including `src/backend/Agw.A2A/Extensions/A2ARoutesBuilderExtensions.cs`.
- In the current host wiring, `.AddA2A(...)` and `app.MapAgwA2A(...)` are commented out in `src/backend/Agw.Host/Program.cs`.
- Do not assume A2A endpoints are live until that wiring is re-enabled.

## Coding Conventions

- Use 4-space indentation and normal C# naming: `PascalCase` for types/members and `camelCase` for locals/parameters.
- Use the `I` prefix for interfaces.
- Keep request/response DTOs in `Contracts/` folders inside the owning module when adding new API contracts.
- Controllers should end with `Controller`.
- Prefer async methods for I/O and constructor injection for dependencies.
- For intentional backend errors, throw `Agw.Shared.Exceptions.AgwException` with an `ErrorCodes` entry. Do not add new `throw new ArgumentException`, `InvalidOperationException`, `NotSupportedException`, or protocol-specific exceptions in `src/backend`.
- Add reusable errors to `src/backend/Agw.Shared/Exceptions/ErrorCodes.cs`. `ErrorCode.Code` is 7 digits: first 3 digits match the HTTP status code, and the last 4 digits increment within that status group, for example `400_0001` or `404_0003`. Reuse existing codes before adding new ones and do not renumber existing codes.
- Use `new AgwException(ErrorCodes.SomeCode)` when the catalog message is sufficient. Use `new AgwException(ErrorCodes.SomeCode, $"...")` when the message needs runtime context such as an id, file path, provider name, or validation value.
- Preserve boundary-specific behavior by translating `AgwException` at the boundary instead of throwing protocol exceptions internally. For example, A2A implementation code throws `AgwException`, while `AgwA2AJsonRpcProcessor` maps it to A2A JSON-RPC errors.
- Non-WebSocket JSON API endpoints in `Agw.Tools`, `Agw.Tasks`, `Agw.Skills`, `Agw.Providers`, `Agw.Jobs`, `Agw.Integrations`, and `Agw.Agents` must return Bens.Results envelopes through `Agw.Shared.Results.AgwApiResult` or the configured Bens.Results boundary mapping. Do not return raw `Ok(...)`, `BadRequest(...)`, `NotFound(...)`, or `NoContent()` from those controllers. Protocol endpoints such as WebSocket handlers and OAuth redirect callbacks keep their protocol-specific responses.
- Frontend code should use TypeScript, React function components, and kebab-case filenames.
- Do not edit generated artifacts unless the task is explicitly about generated output.

## Workflow Expectations

- Follow Conventional Commits such as `feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, and `test:`.
- Keep pull requests focused and include summary, linked issue, testing notes, and migration impact when applicable.
- Include screenshots for UI changes and sample payloads or endpoint notes for API changes.
- Preserve unrelated local changes in a dirty worktree; do not revert or rewrite them unless explicitly asked.
