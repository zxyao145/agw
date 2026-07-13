# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Agw is an AssS (Agent as a Service) platform and agent gateway. It lets users create custom agents, integrate external agents such as Claude Code and Codex, run agent sessions, schedule jobs, and orchestrate multi-agent workflows.

The repository is a modular monolith with an ASP.NET Core + EF Core backend and a Next.js frontend. The backend is built around Microsoft.Agents.AI/MAF, MCP tool servers, A2A endpoints, and external agent SDK integrations.

## Architecture & Project Structure

### Backend (`src/server/`)

`src/server/Agw.Host` is the ASP.NET Core entry point. It wires controllers from module assemblies, middleware, OpenAPI/Scalar, Serilog, OpenTelemetry, static frontend hosting, and module service registration.

Core backend modules:

```text
Agw.Host/              ASP.NET Core entry point and composition root
Agw.Infrastructure/    EF Core DbContext, repositories, migrations, seeding
Agw.Shared/            Base entities, shared contracts, repository interfaces, results, exceptions
Agw.Agents/            Agent definitions, agentflows, MCP tools, runtime execution services
Agw.Providers/         LLM models, providers, model-provider links, auth configs
Agw.Tasks/             Projects, tasks, session records, chat history
Agw.Jobs/              Background jobs, project leases, scheduling
Agw.Integrations/      OAuth integrations, app definitions/instances, integration tools
Agw.A2A/               A2A protocol discovery/communication endpoints
Agw.Skills/            Skill archive management for uploaded ZIP/SKILL.md packages
Agw.Tools/             Tool discovery and registration system
Agw.Files/             File/workspace APIs and endpoint exception mapping
Agw.Setup/             First-run setup, initialization state, API-key guard middleware
```

`Agw.slnx` is the root solution and includes backend projects plus tests. `src/server/backend.sln` includes backend projects only.

`Agw.slnx` includes test projects for A2A, Agents, Files, Setup, Shared, Skills, Tasks, and Tools. `tests/Agw.Jobs.Tests` exists but is not currently included in `Agw.slnx`; run it explicitly when touching jobs/scheduler code.

### Module Layering

Each backend module follows lightweight Clean Architecture layering:

```text
Api → Application → Domain ← Infrastructure
```

- `Api`: controllers, DTOs, routing, validation
- `Application`: use cases, workflows, service coordination
- `Domain`: entities and value objects only
- `Infrastructure`: repositories, EF Core access, external API implementations

Domain objects are intentionally anemic: they contain data only. Put business behavior in Application-layer services such as AppServices, RuntimeServices, or DomainServices. Dependencies must point inward; Domain must not depend on other layers.

A typical backend flow is:

```text
Controller -> AppService / RuntimeService -> DomainService -> IRepository / IUnitOfWork -> EF Core
```

### Important Runtime Services

- `AgentRuntimeService`: builds `AIAgent` instances from persisted agents, hydrates provider config, selects enabled auth config, attaches registered/MCP tools, and supports OpenAI, Anthropic, Claude Code, and Codex-backed execution through Microsoft.Agents.AI integrations.
- `AgentflowRuntimeService`: executes multi-agent workflows, including Concurrent, Sequential, GroupChat, and Handoff orchestration patterns. Magentic scaffolding exists, but runtime execution currently returns `MagenticNotSupported`.
- `JobHostedService`: prefetches persistent jobs into an in-memory priority queue, serializes execution per project, and coordinates execution through `IProjectExecutionLock`.
- `ToolRegistryService`: discovers `[AiTool]` methods and `IAgwTool` implementations, caches metadata, and creates `AITool` instances via `AgwToolFactory`.
- `SkillAppService`: uploads/extracts skill archives, validates and rewrites `SKILL.md` metadata, and stores extracted skills under `wwwroot/skills/{skillName}/`.

### Web Client (`src/clients/web`)

The frontend uses Next.js 16 App Router, React 19, Tailwind CSS 4, Radix UI/Shadcn components, React Query, and `openapi-fetch` generated types. `next.config.ts` proxies `/api/*` and `/openapi/*` to the backend unless `NEXT_OUTPUT_MODE=export`.

## Build & Development Commands

### Backend

Run backend commands from the repository root unless noted otherwise.

```bash
# Restore and build the full root solution, including tests in Agw.slnx
dotnet restore Agw.slnx
dotnet build Agw.slnx

# Run the backend host; development backend listens on http://localhost:5015
dotnet run --project src/server/Agw.Host

# Run with hot reload
dotnet watch --project src/server/Agw.Host

# Run all tests included in the root solution
dotnet test Agw.slnx

# Run a specific included test project
dotnet test tests/Agw.Agents.Tests
dotnet test tests/Agw.Tasks.Tests
dotnet test tests/Agw.Skills.Tests
dotnet test tests/Agw.A2A.Tests
dotnet test tests/Agw.Files.Tests
dotnet test tests/Agw.Setup.Tests
dotnet test tests/Agw.Shared.Tests
dotnet test tests/Agw.Tools.Tests

# Agw.Jobs.Tests is not in Agw.slnx; run it explicitly when needed
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj

# Run one test by method/class name match
dotnet test <project-or-solution> --filter "FullyQualifiedName~MethodName"

# Format backend solution
dotnet format Agw.slnx

# EF Core migrations; do not run automatically without user approval
dotnet ef migrations add <MigrationName> \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host

dotnet ef database update \
  -p src/server/Agw.Infrastructure \
  -s src/server/Agw.Host
```

### Frontend

```bash
cd src/clients/web

pnpm install
pnpm dev          # Next.js dev server on http://localhost:3000
pnpm build
pnpm lint         # oxlint ./src
pnpm lint:fix
pnpm format       # oxfmt ./src
pnpm format:check
pnpm gen:openapi  # openapi-typescript ./openapi.json -o src/api/openapi.d.ts
```

The frontend proxy target is resolved in this order: `BACKEND_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:5015`.

### Mobile Client (`src/clients/mobile`)

The Expo app root is `src/clients/mobile/shared`. Follow the nested `src/clients/mobile/AGENTS.md` and run mobile npm commands from `shared/`; generated native projects are not hand-maintained source.

### Git Hooks

After first clone, configure the repository hooks:

```bash
git config core.hooksPath .githooks
```

## Local Setup and Configuration

On first backend run, open `http://localhost:5015/setup` to choose the database provider, connection string, and administrator password. Setup seeds the database and writes `server-state.json` below the Agw data directory. Remote Web access uses the administrator session cookie; desktop, mobile, and automation clients use named `Authorization: Bearer agw_...` API Tokens. The legacy `X-API-Key` setting is not supported.

Primary backend settings live in `src/server/Agw.Host/appsettings.json`:

- `Database:Provider`: supports `sqlite`, `postgres`, and `mysql`
- `Database:ConnectionString`: defaults to `Data Source=agw.db`
- `Redis:ConnectionString`: defaults to `localhost:6379,abortConnect=false`
- `OpenTelemetry:OtlpEndpoint`: defaults to `http://localhost:4317`
- `SystemInitialization`: controls first-run initialization/API-key state

All backend projects target `.NET 10.0`, use nullable reference types, implicit usings, central package management, and code style enforcement in build.

## Backend API and Exception Rules

Read `docs/rules.md` before backend coding. Its rules are mandatory.

- Non-WebSocket JSON API endpoints in `Agw.Agents`, `Agw.Providers`, `Agw.Tasks`, `Agw.Jobs`, `Agw.Integrations`, `Agw.Skills`, and `Agw.Tools` must return the Bens.Results envelope via `AgwApiResult`/`ApiResult` helpers or configured boundary mapping.
- Do not return raw `Ok(...)`, `BadRequest(...)`, `NotFound(...)`, `NoContent()`, or other bare MVC responses from those JSON controllers. WebSocket handlers, OAuth redirects, A2A protocol endpoints, and static file endpoints may keep protocol-specific formats.
- Do not use path parameters in API routes unless specifically justified; pass identifiers and filters via query parameters or request body instead.
- Expected backend application failures should throw `AgwException` with an `ErrorCodes` entry from `src/server/Agw.Shared/Exceptions/`.
- When adding an error code, use a 7-digit code whose first 3 digits match the HTTP status and whose last 4 digits increment within that group, e.g. `400_0001`, `404_0001`, `500_0001`.
- Keep `ErrorCodes` messages stable and reusable. Pass runtime-specific details as the override message when needed.
- Do not introduce new explicit `throw new ArgumentException`, `InvalidOperationException`, `NotSupportedException`, `HttpRequestException`, or protocol exceptions for expected backend application failures.
- Do not instantiate `HttpClient` directly in backend code. Use `IHttpClientFactory`, typically resolved through dependency injection or `IocUtil.GetSingletonRequiredService<IHttpClientFactory>()` where that project pattern is already used.

`AgwApiExceptionMiddleware` maps `AgwException` to API results. A2A internals also use `AgwException`, with protocol-specific conversion at the A2A boundary.

## Service Registration and Boundaries

- Register new backend services in the relevant module extension method and ensure `Agw.Host/Program.cs` composes the module.
- `Agw.Host/Program.cs` currently enables A2A via `.AddA2A(builder.Configuration)` and `app.MapAgwA2A(a2AServerOptions.Prefix)`.
- `Agw.Integrations` treats `IntegrationConstants.AppList` as the single source of truth for `AppDefinition`; do not add `DbSet<AppDefinition>` or migrations for it. Persisted integration configuration belongs in `AppInstance` and `OAuthAuthorizationToken`.

## Coding Style

- C#: 4-space indentation, `PascalCase` for types/members, `camelCase` for locals/parameters, `I` prefix for interfaces, async methods for I/O, constructor injection for dependencies.
- Do not use `DateTime` in backend code; use `DateTimeOffset` instead.
- Use `TimeProvider` whenever it is applicable.
- Do not use C# primary constructors. Declare explicit constructors and backing fields/properties; dependency-injected services must use explicit constructor injection.
- Backend DTOs/contracts live under module `Contracts/` folders.
- Frontend: TypeScript React function components, App Router conventions, and kebab-case filenames.

## Commit Conventions

Use Conventional Commits:

- `feat:` new features
- `fix:` bug fixes
- `refactor:` code restructuring
- `chore:` maintenance tasks
- `docs:` documentation
- `test:` tests
