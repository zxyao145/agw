# Repository Instructions

This document gives coding agents the repository context and mandatory constraints needed to work safely in Agw. `AGENTS.md` and `CLAUDE.md` must remain identical copies of this document.

## Project Overview

Agw is an AaaS (Agent as a Service) platform and agent gateway. It lets users create agents, integrate external agents such as Claude Code and Codex, run agent sessions, schedule jobs, orchestrate multi-agent workflows, and manage providers, tools, skills, projects, tasks, integrations, and chat execution.

The repository is a modular monolith with an ASP.NET Core and EF Core backend plus Next.js and Expo clients. The backend targets `.NET 10.0`, is built around Microsoft.Agents.AI/MAF, MCP tool servers, A2A endpoints, and external-agent SDK integrations, and is composed from `src/server/Agw.Host/Program.cs`.

## Repository Map

### Backend (`src/server/`)

```text
Agw.Host/            # ASP.NET Core entry point, composition root, middleware, OpenAPI, static files, websockets, and DB seeding
Agw.Data/            # Persisted entities, EF configurations, repository abstractions, and unit-of-work contracts
Agw.Infrastructure/  # EF Core DbContext, repositories, migrations, and seeding
Agw.Shared/          # Shared contracts, exceptions, results, and utilities
Agw.A2A/             # A2A protocol types, discovery, communication endpoints, and route builders
Agw.Agents/          # Agent definitions, agentflows, MCP tools, and runtime execution services
Agw.Files/           # File and workspace APIs, path security, request validation, and error mapping
Agw.Integrations/    # Plugin catalog, installations, connections, credentials, OAuth, MCP, and connection-bound tools
Agw.Jobs/            # Scheduled jobs, project leases, execution logs, and hosted scheduling
Agw.Providers/       # LLM models, providers, model-provider links, and auth configuration
Agw.Setup/           # First-run setup, initialization state, and API-key guard middleware
Agw.Skills/          # Skill archive validation, storage, and agent-skill relations
Agw.Projects/         # Projects, project tasks, task records, contexts, and task APIs
Agw.Tools/           # Tool discovery, metadata, and AI tool factory and registry
```

`Agw.slnx` is the root solution and includes the backend projects and tests. `src/server/server.sln` includes backend projects only. The root solution includes test projects for A2A, Agents, Files, Host, Integrations, Jobs, Projects, Setup, Shared, Skills, and Tools.

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

### Client Workspace (`src/clients/`)

`@agw/web`, `@agw/desktop`, and `@agw/desktop-contracts` share a pnpm Workspace and use Turborepo for task orchestration. Run pnpm commands for these packages from `src/clients/`; one `pnpm install` installs the whole workspace. The Expo mobile app remains a separate npm workspace.

### Web Client (`src/clients/web/`)

The web client uses Next.js 16 App Router, React 19, Tailwind CSS 4, Radix UI/Shadcn components, React Query, and generated `openapi-fetch` types.

Routes live under `src/app/(app)/`, typed API helpers under `src/api/`, Desktop renderer integration under `src/features/desktop/`, shared UI under `src/components/`, and shared hooks, utilities, and types under `src/hooks/`, `src/lib/`, and `src/types/`.

`src/clients/web/next.config.ts` proxies `/api/*` and `/openapi/*` to the backend unless `NEXT_OUTPUT_MODE=export`.

### Desktop Client (`src/clients/desktop/`)

The Electron main and preload entry points are `src/main/index.ts` and `src/preload/index.ts`. The Next.js Web application remains the Desktop renderer; both applications consume the framework-free bridge, runtime, settings, server-profile, and execution contracts from `src/clients/packages/desktop-contracts/`.

### Mobile Client (`src/clients/mobile/`)

The Expo app root is `src/clients/mobile/shared`. Follow the nested `src/clients/mobile/AGENTS.md`, run mobile npm commands from `shared/`, and do not hand-maintain generated native projects.

## Key Runtime Components

### Runtime Entry Points and Services

- `src/server/Agw.Host/Program.cs`: bootstraps logging, OpenTelemetry, dependency injection, OpenAPI/Scalar, websockets, static files, module registration, and database seeding.
- `src/server/Agw.Agents/Execution/README.md`: documents the SignalR command boundary, reusable runtimes, turn lifecycle, message flow, and command extension model.
- `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.cs`: builds `AIAgent` instances from persisted agents, hydrates provider configuration, selects enabled auth configuration, attaches registered and MCP tools, and supports OpenAI, Anthropic, Claude Code, and Codex-backed execution through Microsoft.Agents.AI integrations.
- `src/server/Agw.Agents/Execution/Agentflows/AgentflowRuntimeService.cs`: executes persisted DAG workflows compiled by `AgentflowWorkflowCompiler`, including Agent, Workflow-as-Agent, HumanGate, Concurrent, GroupChat, Handoff, and Magentic nodes.
- `src/server/Agw.Jobs/Scheduling/Coordination/JobHostedService.cs`: prefetches persistent jobs into an in-memory priority queue, serializes execution per project, and coordinates execution through `IProjectExecutionLock`.
- `src/server/Agw.Tools/ToolRegistryService.cs`: discovers `[AiTool]` methods and `IAgwTool` implementations, caches metadata, and creates `AITool` instances through `AgwToolFactory`.
- `src/server/Agw.Skills/Application/SkillAppService.cs`: validates uploaded skill archives, rewrites `SKILL.md` metadata, and stores extracted skills below `AgwDataPaths.SkillsDirectory`.
- `src/server/Agw.Projects/Application/TaskAppService.cs`: resolves logical tasks from project contexts and task records for execution and history queries.
- `src/server/Agw.Files/Application/Storage/Resolver/ProjectScopedFileSystemResolver.cs`: resolves `Project.Workspace` to a host-visible local file system and caches `CachedEntry` values by Project ID.
- `src/server/Agw.Integrations/Application/Capabilities/ConnectionCapabilityResolver.cs`: resolves ready Connection-bound Native/MCP tools, bundled Skills, warnings, and leases; OAuth controllers and Native providers remain boundary adapters.
- `src/server/Agw.Shared/Exceptions/ErrorCodes.cs`: is the central catalog for backend `AgwException` error codes and HTTP status mapping.

### Important Domain Concepts

- `Agent`: persisted AI agent configuration with prompt, runtime type, model-provider linkage, tool bindings, and optional skill assignments.
- `Agentflow`: persisted multi-agent DAG with nodes, edges, and execution blocks.
- `McpToolServer`: MCP server configuration for stdio, HTTP, or SSE transport.
- `LlmModel`, `Provider`, `ModelProvider`, `ProviderAuthConfig`: provider and model catalog plus authentication configuration.
- `Skill`: uploaded skill archive with validated `SKILL.md` metadata and agent-skill relations.
- `Project`, `ProjectContext`, `TaskRecord`, `TaskProjection`: host-visible workspace configuration, conversation grouping, persisted execution records, and logical task views reconstructed from those records.
- `Job`, `JobLog`: scheduled background execution and per-run logging.
- `PluginDefinition`, `PluginInstallation`, `Connection`, and their credential entities: static integration capabilities, platform configuration, Agent-selectable external accounts or endpoints, and protected or environment-referenced secrets.

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

The development backend listens on `http://localhost:30815` by default through `src/server/Agw.Host/Properties/launchSettings.json`.

Run a focused project with `dotnet test tests/Agw.Files.Tests` (or the matching `Agw.*.Tests` project), and use `--filter "FullyQualifiedName~MethodName"` for a specific test.

Do not add or apply EF Core migrations automatically. When the user explicitly requests a migration, use:

```bash
dotnet ef migrations add <MigrationName> \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host

dotnet ef database update \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host
```

### Web and Desktop Clients

Run pnpm commands from `src/clients/`. The single install covers `@agw/web`, `@agw/desktop`, and `@agw/desktop-contracts`:

```bash
pnpm install
pnpm dev:web
pnpm build
pnpm lint
pnpm test
pnpm format
pnpm format:check
pnpm gen:api
```

For live Desktop renderer development, run `pnpm dev:web` in one terminal, wait for Next.js to be ready, then run `pnpm dev:desktop` in a second terminal. The Next.js development server listens on `http://localhost:3000`. Linting and formatting use `oxlint` and `oxfmt`, not ESLint or Prettier. Turborepo uses its local task cache only; remote caching is disabled.

Use root scripts where available, or run a package-specific Web task with `pnpm exec turbo run <task> --filter=@agw/web`. Package Desktop installers from `src/clients/` with:

```bash
AGW_PACKAGE_FLAVOR=client pnpm make:desktop
AGW_PACKAGE_FLAVOR=full pnpm make:desktop
pnpm make:desktop -- --arch=x64
```

The frontend proxy target is resolved in this order: `BACKEND_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:30815`.

Regenerate `src/clients/web/src/api/openapi.d.ts` with `pnpm gen:api` after backend contract changes.

After the first clone, configure hooks with `git config core.hooksPath .githooks`. Backend tests use xUnit; mirror production namespaces and prefer names such as `Method_Condition_ExpectedResult`.

## Local Setup and Configuration

On the first backend run, open `http://localhost:30815/setup` to choose the database provider, connection string, and administrator password. Setup seeds the database and writes `server-state.json` below the Agw data directory.

Remote web access uses the administrator session cookie. Desktop, mobile, and automation clients use named `Authorization: Bearer agw_...` API tokens. The legacy `X-API-Key` setting is not supported.

Primary backend settings live in `src/server/Agw.Host/appsettings.json` under `Database`, `DistributedLock`, `OpenTelemetry`, and `SystemInitialization`.

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

- All non-WebSocket JSON API endpoints in backend modules, including `Agw.Host`, `Agw.Setup`, and `Agw.Files`, must return Bens.Results envelopes directly through `Bens.Results.ApiResult` or the configured boundary mapping.
- Return helpers such as `ApiResult.Ok()`, `ApiResult.Ok(data)`, and `ApiResult.BadRequest(...)`. Use `ErrorCode.ToApiResult()` or `AgwException.ToApiResult()` when mapping shared application errors, and let `AgwApiExceptionMiddleware` map uncaught `AgwException` instances automatically.
- Use `[ProducesApiResult]` for OpenAPI response metadata where applicable; it does not replace direct `ApiResult` returns.
- Do not return raw `Ok(...)`, `BadRequest(...)`, `NotFound(...)`, `NoContent()`, or other bare MVC responses from those controllers.
- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep protocol-specific response formats.

### A2A

- `Agw.Host/Program.cs` registers A2A through `.AddA2A(builder.Configuration)`, maps it through `app.MapAgwA2A(a2AServerOptions.Prefix)`, and requires authentication at the host boundary.

### Project Workspaces

- `Project.Workspace` is the only file-root source. Files APIs and in-process consumers use `projectId` plus project-relative paths.
- The Workspace must be visible to the Agw process. Mount network storage through the operating system or container platform; do not reintroduce application-level SFTP or `fileStorage` backends.
- Files, Git, Claude Code, Codex, compilers, and shells must consume the same host-visible working tree.
- `ProjectScopedFileSystemResolver` caches `CachedEntry(FileSystem, CreatedAt)` by Project ID without TTL; changing an already-resolved Workspace requires restart unless explicit invalidation is designed.

## Frontend Integration

- Prefer the typed helpers in `src/clients/web/src/api/client.ts` for REST calls.
- `src/clients/web/src/api/client.ts` unwraps Bens.Results response envelopes before data reaches pages; update this central helper when the backend envelope contract changes.
- Use `src/clients/web/src/api/task-client.ts` for project task, context, and history helpers.
- Use `src/clients/web/src/api/execution-hub.ts` for SignalR execution flows.
- Use `src/clients/web/src/api/files.ts` for backend file-management endpoints used by the UI.
- Keep Electron renderer integration in `src/clients/web/src/features/desktop/` and shared cross-process data shapes in `@agw/desktop-contracts`; do not duplicate bridge or runtime contracts in either application.
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

Follow Conventional Commits with `feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, or `test:`.
