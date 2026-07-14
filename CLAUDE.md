# Repository Instructions

This document gives coding agents the repository context and mandatory constraints needed to work safely in Agw. `AGENTS.md` and `CLAUDE.md` must remain identical copies of this document.

## Project Overview

Agw is an AaaS (Agent as a Service) platform and agent gateway. It lets users create agents, integrate external agents such as Claude Code and Codex, run agent sessions, schedule jobs, orchestrate multi-agent workflows, and manage providers, tools, skills, projects, tasks, integrations, and chat execution.

The repository is a modular monolith with an ASP.NET Core and EF Core backend plus Next.js and Expo clients. The backend targets `.NET 10.0`, is built around Microsoft.Agents.AI/MAF, MCP tool servers, A2A endpoints, and external-agent SDK integrations, and is composed from `src/server/Agw.Host/Program.cs`.

## Repository Map

### Backend (`src/server/`)

```text
Agw.Host/            # ASP.NET Core entry point, composition root, middleware, OpenAPI, static files, websockets, and DB seeding
Agw.Infrastructure/  # EF Core DbContext, repositories, migrations, and seeding
Agw.Shared/          # Shared entities, contracts, exceptions, repository abstractions, results, and utilities
Agw.A2A/             # A2A protocol types, discovery, communication endpoints, and route builders
Agw.Agents/          # Agent definitions, agentflows, MCP tools, and runtime execution services
Agw.Files/           # File and workspace APIs, path security, request validation, and error mapping
Agw.Integrations/    # OAuth integrations, app definitions and instances, and integration tools
Agw.Jobs/            # Scheduled jobs, project leases, execution logs, and hosted scheduling
Agw.Providers/       # LLM models, providers, model-provider links, and auth configuration
Agw.Setup/           # First-run setup, initialization state, and API-key guard middleware
Agw.Skills/          # Skill archive validation, storage, and agent-skill relations
Agw.Projects/           # Projects, project tasks, task records, contexts, and task APIs
Agw.Tools/           # Tool discovery, metadata, and AI tool factory and registry
```

`Agw.slnx` is the root solution and includes the backend projects and tests. `src/server/server.sln` includes backend projects only. The root solution includes test projects for A2A, Agents, Files, Host, Jobs, Setup, Shared, Skills, Tasks, and Tools.

### Module Layering

Each backend module follows lightweight Clean Architecture layering:

```text
Api → Application → Domain ← Infrastructure
```

- `Api`: controllers, DTOs, routing, and validation.
- `Application`: use cases, workflows, and service coordination.
- `Domain`: entities and value objects only.
- `Infrastructure`: repositories, EF Core access, and external API implementations.

Dependencies must point inward. Domain objects are intentionally anemic and contain only data; put business behavior in Application-layer services such as AppServices, RuntimeServices, or DomainServices. Domain must not depend on other layers.

A typical backend flow is:

```text
Controller → AppService / RuntimeService → DomainService → IRepository / IUnitOfWork → EF Core
```

### Web Client (`src/clients/web/`)

The web client uses Next.js 16 App Router, React 19, Tailwind CSS 4, Radix UI/Shadcn components, React Query, and generated `openapi-fetch` types.

```text
src/app/(app)/
  (agents)/agents            # Agent CRUD
  (agents)/agentflows        # Workflow editor
  (interface)/chat
  (jobs)/jobs
  (overview)/dashboard
  (overview)/traces
  (providers)/models
  (providers)/providers
  (providers)/model-providers
  (tasks)/projects
  (tools)/mcp-tool-servers
  integrations
  settings
  skills
src/api/                     # Typed fetch helpers and generated OpenAPI types
src/components/              # Shared UI components
src/hooks/                   # Shared React hooks
src/lib/                     # Shared frontend utilities
src/types/                   # Shared frontend types
```

`src/clients/web/next.config.ts` proxies `/api/*` and `/openapi/*` to the backend unless `NEXT_OUTPUT_MODE=export`.

### Mobile Client (`src/clients/mobile/`)

The Expo app root is `src/clients/mobile/shared`. Follow the nested `src/clients/mobile/AGENTS.md`, run mobile npm commands from `shared/`, and do not hand-maintain generated native projects.

### Other Top-Level Paths

- `docs/` contains project documentation.
- `tests/` contains xUnit test projects.
- Treat `bin/`, `obj/`, `.next/`, `node_modules/`, and `TestResults/` as generated artifacts.

## Key Runtime Components

### Runtime Entry Points and Services

- `src/server/Agw.Host/Program.cs`: bootstraps logging, OpenTelemetry, dependency injection, OpenAPI/Scalar, websockets, static files, module registration, and database seeding.
- `src/server/Agw.Agents/Execution/README.md`: documents the SignalR command boundary, reusable runtimes, turn lifecycle, message flow, and command extension model.
- `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.cs`: builds `AIAgent` instances from persisted agents, hydrates provider configuration, selects enabled auth configuration, attaches registered and MCP tools, and supports OpenAI, Anthropic, Claude Code, and Codex-backed execution through Microsoft.Agents.AI integrations.
- `src/server/Agw.Agents/Execution/Agentflows/AgentflowRuntimeService.cs`: executes Concurrent, Sequential, GroupChat, and Handoff workflows. Magentic scaffolding exists, but runtime execution currently returns `MagenticNotSupported`.
- `src/server/Agw.Jobs/HostedService/JobHostedService.cs`: prefetches persistent jobs into an in-memory priority queue, serializes execution per project, and coordinates execution through `IProjectExecutionLock`.
- `src/server/Agw.Tools/ToolRegistryService.cs`: discovers `[AiTool]` methods and `IAgwTool` implementations, caches metadata, and creates `AITool` instances through `AgwToolFactory`.
- `src/server/Agw.Skills/Application/SkillAppService.cs`: validates uploaded skill archives, rewrites `SKILL.md` metadata, and stores extracted skills under `wwwroot/skills/{skillName}/`.
- `src/server/Agw.Projects/Application/TaskAppService.cs`: resolves logical tasks from project contexts and task records for execution and history queries.
- `src/server/Agw.Integrations/Controllers/OauthController.cs`: handles OAuth authorization start and callback endpoints for integration connections.
- `src/server/Agw.Integrations/Tools/GitHub/GitHubTools.cs`: provides integration-backed GitHub tools to runtime agents.
- `src/server/Agw.Shared/Exceptions/ErrorCodes.cs`: is the central catalog for backend `AgwException` error codes and HTTP status mapping.

### Important Domain Concepts

- `Agent`: persisted AI agent configuration with prompt, runtime type, model-provider linkage, tool bindings, and optional skill assignments.
- `Agentflow`: multi-agent workflow graph with nodes, edges, and an orchestration pattern.
- `McpToolServer`: MCP server configuration for stdio, HTTP, or SSE transport.
- `LlmModel`, `Provider`, `ModelProvider`, `ProviderAuthConfig`: provider and model catalog plus authentication configuration.
- `Skill`: uploaded skill archive with validated `SKILL.md` metadata and agent-skill relations.
- `Project`, `ProjectContext`, `TaskRecord`, `TaskProjection`: workspace configuration, conversation grouping, persisted execution records, and logical task views reconstructed from those records.
- `Job`, `JobLog`: scheduled background execution and per-run logging.
- `AppDefinition`, `AppInstance`, `OAuthAuthorizationToken`: integration catalog, authorized app connections, and OAuth authorization state and token persistence.

## Build, Run, and Test

### Backend

Run backend commands from the repository root:

```bash
dotnet restore Agw.slnx
dotnet build Agw.slnx
dotnet run --project src/server/Agw.Host
dotnet watch --project src/server/Agw.Host
dotnet test Agw.slnx
dotnet format Agw.slnx
```

The development backend listens on `http://localhost:5015` by default through `src/server/Agw.Host/Properties/launchSettings.json`.

Run a specific test project or filtered test when needed:

```bash
dotnet test tests/Agw.Agents.Tests
dotnet test tests/Agw.Projects.Tests
dotnet test tests/Agw.Skills.Tests
dotnet test tests/Agw.A2A.Tests
dotnet test tests/Agw.Files.Tests
dotnet test tests/Agw.Setup.Tests
dotnet test tests/Agw.Shared.Tests
dotnet test tests/Agw.Tools.Tests
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj
dotnet test <project-or-solution> --filter "FullyQualifiedName~MethodName"
```

Do not add or apply EF Core migrations automatically. When the user explicitly requests a migration, use:

```bash
dotnet ef migrations add <MigrationName> \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host

dotnet ef database update \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host
```

### Frontend

Run from `src/clients/web/`:

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

The Next.js development server listens on `http://localhost:3000`. Linting and formatting use `oxlint` and `oxfmt`, not ESLint or Prettier.

The frontend proxy target is resolved in this order: `BACKEND_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:5015`.

Regenerate `src/clients/web/src/api/openapi.d.ts` after backend contract changes.

### Git Hooks

After the first clone, configure repository hooks:

```bash
git config core.hooksPath .githooks
```

### Test Conventions

- Backend tests use xUnit.
- Run `dotnet test Agw.slnx` for the normal repository-wide backend test pass.
- Prefer namespaces that mirror production namespaces.
- Prefer method names such as `Method_Condition_ExpectedResult`.

## Local Setup and Configuration

On the first backend run, open `http://localhost:5015/setup` to choose the database provider, connection string, and administrator password. Setup seeds the database and writes `server-state.json` below the Agw data directory.

Remote web access uses the administrator session cookie. Desktop, mobile, and automation clients use named `Authorization: Bearer agw_...` API tokens. The legacy `X-API-Key` setting is not supported.

Primary backend settings live in `src/server/Agw.Host/appsettings.json`:

```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=agw.db"
  },
  "DistributedLock": {
    "Provider": null,
    "ConnectionString": ""
  },
  "OpenTelemetry": {
    "ServiceName": "Agw",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Configuration guidance:

- `Database:Provider` supports `sqlite` and `postgres`.
- `Database:ConnectionString` defaults to `Data Source=agw.db`.
- `DistributedLock:Provider` supports `inmemory` and `postgres`; null or missing follows `Database:Provider`.
- When `DistributedLock:ConnectionString` is empty, a PostgreSQL lock reuses `Database:ConnectionString`.
- `OpenTelemetry:OtlpEndpoint` defaults to `http://localhost:4317`.
- `SystemInitialization` controls first-run initialization and API-token state.
- Keep secrets out of `appsettings*.json` and frontend environment files; prefer environment-variable overrides.
- All backend projects target `.NET 10.0` and use nullable reference types, implicit usings, central package management, and code-style enforcement during builds.

## Mandatory Repository Rules

Read [`docs/rules.md`](docs/rules.md) before coding. Its rules are mandatory.

### Backend API Responses and Exceptions

- All non-WebSocket JSON API endpoints in `Agw.Agents`, `Agw.Providers`, `Agw.Projects`, `Agw.Jobs`, `Agw.Integrations`, `Agw.Skills`, and `Agw.Tools` must return Bens.Results envelopes through `Agw.Shared.Results.AgwApiResult`, `ApiResult` helpers, or the configured boundary mapping.
- Return helpers such as `AgwApiResult.Ok()`, `AgwApiResult.Ok<T>(data)`, and `AgwApiResult.BadRequest(...)`; let `AgwApiExceptionMiddleware` map `AgwException` automatically.
- Do not return raw `Ok(...)`, `BadRequest(...)`, `NotFound(...)`, `NoContent()`, or other bare MVC responses from those controllers.
- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep protocol-specific response formats.

### A2A

- `Agw.A2A` includes `src/server/Agw.A2A/Extensions/A2ARoutesBuilderExtensions.cs`.
- `Agw.Host/Program.cs` registers A2A through `.AddA2A(builder.Configuration)` and maps it through `app.MapAgwA2A(a2AServerOptions.Prefix)`.
- A2A routes require authentication at the host boundary.

## Frontend Integration

- Prefer the typed helpers in `src/clients/web/src/api/client.ts` for REST calls.
- `src/clients/web/src/api/client.ts` unwraps Bens.Results response envelopes before data reaches pages; update this central helper when the backend envelope contract changes.
- Use `src/clients/web/src/api/task-client.ts` for project task, context, and history helpers.
- Use `src/clients/web/src/api/execution-hub.ts` for SignalR execution flows.
- Use `src/clients/web/src/api/files.ts` for backend file-management endpoints used by the UI.
- Keep route-specific UI inside the matching `src/app/(app)/...` segment and shared UI in `src/components/`.

## Coding Conventions

### Backend

- Use 4-space indentation.
- Use `PascalCase` for types and members, `camelCase` for locals and parameters, and the `I` prefix for interfaces.
- Prefer async methods for I/O and explicit constructor injection for dependencies.
- Do not use C# primary constructors. Declare explicit constructors and backing fields or properties.
- Keep request and response DTOs in `Contracts/` folders inside the owning module.
- Controller class names must end with `Controller`.

### Date and Time

- Store backend date and time values in one consistent time zone. Prefer UTC; the server's local time zone is also allowed, but a deployment must choose one and use it consistently.
- Do not use `DateTime` in backend code; use `DateTimeOffset`.
- Use `TimeProvider` whenever it is applicable.
- Serialize API date and time values as RFC 3339 strings with a time-zone designator or offset (`Z` or `+/-HH:mm`). Do not return offset-free local date-time strings.
- Do not localize date and time values on the server. Clients are responsible for converting and formatting them according to the user's local time zone and locale.

### Frontend

- Use TypeScript and React function components.
- Follow App Router conventions.
- Use kebab-case filenames.

### Generated Artifacts

Do not edit generated artifacts unless the task explicitly concerns generated output.

## Workflow Expectations

- Preserve unrelated local changes in a dirty worktree; do not revert or rewrite them unless explicitly asked.
- Keep pull requests focused and include a summary, linked issue, testing notes, and migration impact when applicable.
- Include screenshots for UI changes and sample payloads or endpoint notes for API changes.

## Commit Conventions

Follow Conventional Commits:

- `feat:` new features
- `fix:` bug fixes
- `refactor:` code restructuring
- `chore:` maintenance tasks
- `docs:` documentation
- `test:` tests
