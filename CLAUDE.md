# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Agw is an ASP.NET Core + EF Core backend with a Next.js frontend for managing LLM agents, models, providers, tools, skills, jobs, and multi-agent workflows. It uses a modular project structure built around Microsoft.Agents.AI, MCP tool servers, A2A endpoints, and external agents such as Claude Code SDK.

## Architecture & Project Structure

### Backend Organization (`src/backend/`)

```
Agw.Host/              # ASP.NET Core entry point, DI registration, hosted services
Agw.Infrastructure/    # EF Core DbContext, repositories, migrations
Agw.Shared/            # Base entities, enums, shared models, exceptions, repository interfaces, contracts
Agw.Agents/            # Agent definitions, agentflows, MCP tools, execution services
Agw.Providers/         # LLM models, providers, model-providers, auth configs
Agw.Tasks/             # Projects, tasks, session records, chat history
Agw.Jobs/              # Background jobs, project leases
Agw.Integrations/      # OAuth integrations, app definitions/instances, integration tools
Agw.A2A/               # A2A protocol implementation for agent discovery/communication
Agw.Skills/            # Skill archive management (ZIP uploads, SKILL.md format)
Agw.Tools/             # Tool discovery and registration system
```

`Agw.slnx` includes these backend projects plus test projects: `Agw.A2A.Tests`, `Agw.Agents.Tests`, `Agw.Files.Tests`, `Agw.Shared.Tests`, `Agw.Tasks.Tests`, and `Agw.Skills.Tests`. `tests/Agw.Jobs.Tests` exists but is **not** currently included in `Agw.slnx`.

### Module Internal Layering

Each backend module follows lightweight Clean Architecture layering:

```
Api → Application → Domain ← Infrastructure
```

- **Api**: Controllers, DTOs, routing, validation
- **Application**: Use cases, workflows, service coordination
- **Domain**: Entities, value objects, business rules (pure, framework-free)
- **Infrastructure**: Repositories, DB access, external APIs

The project uses an **anemic domain model**: domain objects contain only data; all business behavior lives in Application-layer services.

Dependencies always point inward; Domain never depends on anything else.

### Core Services

**AgentRuntimeService** (`Agw.Agents/Application/Agents/AgentRuntimeService.cs`):

- Creates `AIAgent` instances from persisted `Agent` entities
- Hydrates provider config, selects random enabled auth config
- Builds tool list from registered functions + MCP tools
- Supports OpenAI and Anthropic providers via Microsoft.Agents.AI
- Handles Claude Code external agents with session resumption

**AgentflowRuntimeService** (`Agw.Agents/Application/Agentflows/AgentflowRuntimeService.cs`):

- Executes multi-agent workflows with different orchestration patterns
- Patterns: Concurrent, Sequential, GroupChat, Handoff, Magentic

**ProjectTaskSchedulerHostedService** (`Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs`):

- Background service polling for pending tasks every 2 seconds
- Max 4 projects executing in parallel, one task per project at a time
- DB-backed `ProjectLease` with 30-second TTL for distributed locking

**ToolRegistryService** (`Agw.Tools/ToolRegistryService.cs`):

- Discovers AI tools from `[AiTool]` attributes and `IAgwTool` implementations
- Singleton service that caches tool metadata on startup
- Creates `AITool` instances for agent execution via `AgwToolFactory`

**SkillAppService** (`Agw.Skills/Services/SkillAppService.cs`):

- Manages skill archives uploaded as ZIP files
- Extracts archives and validates SKILL.md frontmatter
- Rewrites SKILL.md metadata to match database values
- Skills stored in `wwwroot/skills/{skillName}/`

## Build & Development Commands

### Backend

```bash
# Restore and build
dotnet restore Agw.slnx
dotnet build Agw.slnx

# Run locally (default port 5015)
dotnet run --project src/backend/Agw.Host

# Run with hot reload
dotnet watch --project src/backend/Agw.Host

# Run tests
dotnet test Agw.slnx

# Run specific test project
dotnet test tests/Agw.Agents.Tests
dotnet test tests/Agw.Tasks.Tests
dotnet test tests/Agw.Skills.Tests
dotnet test tests/Agw.A2A.Tests
dotnet test tests/Agw.Files.Tests
dotnet test tests/Agw.Shared.Tests
# Agw.Jobs.Tests is not in Agw.slnx; run it explicitly:
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj

# Run a single test method
dotnet test <project> --filter "FullyQualifiedName~MethodName"

# Format
dotnet format

# EF Core migrations (DO NOT run automatically)
dotnet ef migrations add <MigrationName> \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host

dotnet ef database update \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host
```

### Frontend

```bash
cd src/frontend/web

pnpm install
pnpm dev          # port 3000
pnpm build
pnpm lint         # oxlint
pnpm lint:fix
pnpm format       # oxfmt
pnpm format:check
pnpm gen:openapi  # regenerate types from backend OpenAPI spec
```

The frontend dev server runs on `http://localhost:3000`. `next.config.ts` rewrites `/api/*` and `/openapi/*` to `BACKEND_API_BASE_URL`, which defaults to `http://localhost:5015`.

### Git Hooks

After first clone, configure hooks:

```bash
git config core.hooksPath .githooks
```

## Configuration

### Backend (`appsettings.json`)

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

Supported database providers: `sqlite`, `postgres`, `mysql`. Store sensitive values in environment variables.

### Service Registration (`Program.cs`)

Domain services are manually registered as `Scoped` or `Singleton`. When adding new services, register them in the same section.

## Coding Style

- 4-space indentation, C# conventions: `PascalCase` for types/members, `camelCase` for locals/parameters
- `I` prefix for interfaces, `*Controller.cs` for controllers
- DTOs under `Contracts/` folders in each module
- Async methods for I/O, constructor injection for dependencies
- Frontend: TypeScript + React function components, kebab-case filenames

## Backend Exception Policy

Intentional backend errors use the shared exception model in `src/backend/Agw.Shared/Exceptions/`:

- Throw `AgwException` for validation, domain, application, tool, scheduler, and runtime failures in `src/backend`.
- Pick an existing `ErrorCodes` entry before adding a new one. When adding one, use a 7-digit code whose first 3 digits match `HttpStatusCode` and whose last 4 digits increment within that status group, such as `400_0001`, `404_0001`, or `500_0001`.
- Keep `ErrorCodes` messages stable and reusable. If an error needs runtime details, pass an override message: `new AgwException(ErrorCodes.JobNotFound, $"Job not found: {jobId}")`.
- Do not introduce new explicit `throw new ArgumentException`, `InvalidOperationException`, `NotSupportedException`, `HttpRequestException`, or protocol exceptions in backend code for expected application failures.
- Translate at boundaries when required. A2A internals throw `AgwException`; `AgwA2AJsonRpcProcessor` converts those exceptions to A2A JSON-RPC errors. Controllers that need custom responses should catch `AgwException`.
- Update tests to assert `AgwException.Code` and, when relevant, `StatusCode`. `tests/Agw.Shared.Tests` contains guard tests for error-code format and `throw new` usage.

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

## A2A Protocol

The `Agw.A2A` module is present in the codebase, but `.AddA2A(...)` and `app.MapAgwA2A(...)` are currently commented out in `src/backend/Agw.Host/Program.cs`. Do not assume A2A endpoints are live until that wiring is re-enabled.

## Development Notes

- All projects target `.NET 10.0` with nullable reference types
- Migrations relative to host project; run with `-p` and `-s` flags
- Frontend uses App Router (Next.js 16)
- Background services handle graceful shutdown via `CancellationToken`
- OpenTelemetry integrated for tracing, metrics, and logging
- Serilog for structured logging with OTLP correlation
