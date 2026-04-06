# AGENTS.md

This file provides guidance to coding agents working in this repository.

## Project Overview

Agw is an ASP.NET Core + EF Core backend with a Next.js frontend for managing LLM agents, models, providers, and multi-agent workflows. It follows a modular architecture and integrates Microsoft.Agents.AI, MCP tool servers, A2A endpoints, and external agents such as Claude Code SDK.

## Architecture And Project Structure

### Backend Organization (`src/backend/`)

```text
Agw.Host/              # ASP.NET Core entry point, DI registration, hosted services
Agw.Infrastructure/    # EF Core DbContext, repositories, migrations
Agw.Shared/            # Base entities, enums, shared models, repository interfaces
Agw.Shared.Contract/   # Interfaces for cross-module interaction
Agw.Agents/            # Agent definitions, workflows, MCP tools, runtime services
Agw.Providers/         # LLM models, providers, model-providers, auth configs
Agw.Tasks/             # Projects, tasks, session records, chat history
Agw.Jobs/              # Background jobs and project leases
Agw.A2A/               # A2A protocol implementation for agent discovery and messaging
Agw.Skills/            # Skill archive management and agent-skill relationships
Agw.Tools/             # Tool discovery, metadata, and runtime registration
```

Notes:
- `Agw.slnx` includes the backend modules above plus `tests/Agw.A2A.Tests`, `tests/Agw.Agents.Tests`, `tests/Agw.Tasks.Tests`, and `tests/Agw.Skills.Tests`.
- Claude Code integration is provided through the `ClaudeCodeSdk.MAF` package referenced by backend projects.

### Frontend Organization (`src/frontend/web/`)

```text
src/app/(app)/
  (agents)/agents            # Agent CRUD
  (agents)/agentflows        # Workflow editor
  (agents)/mcp-tool-servers  # MCP server management
  (agents)/skills            # Skill archive management
  (external-agents)/claude-code
  (overview)/dashboard
  (overview)/traces
  (providers)/models
  (providers)/providers
  (providers)/model-providers
  (tasks)/projects
  (tasks)/jobs
  integrations
src/api/                     # Typed API helpers and generated OpenAPI types
src/components/              # Shared UI components
```

### Repository Layout

- `docs/` contains project documentation.
- `scripts/` contains helper scripts such as API smoke tests.
- `tests/` contains the current xUnit test projects.
- Treat `bin/`, `obj/`, `.next/`, and `node_modules/` as generated artifacts.

## Key Domain Concepts

- `Agent`: AI agent with prompt, type, model-provider linkage, and MCP tool bindings.
- `Agentflow`: Multi-agent workflow with nodes, edges, and orchestration pattern.
- `McpToolServer`: MCP server configuration for stdio, HTTP, or SSE transport.
- `LlmModel`, `Provider`, `ModelProvider`, `ProviderAuthConfig`: model and provider catalog plus auth configuration.
- `Skill`: uploaded skill archive with validated `SKILL.md` metadata and agent assignments.
- `Project`, `ProjectTask`, `TaskRecord`, `ProjectLease`: task execution, conversation persistence, and concurrency control.

## Core Runtime Services

- `src/backend/Agw.Agents/Application/Agents/AgentRuntimeService.cs`: creates runtime agents, hydrates provider configuration, wires tools, and supports external agent session resumption.
- `src/backend/Agw.Agents/Application/Agentflows/AgentflowRuntimeService.cs`: executes workflows with `Concurrent`, `Sequential`, `GroupChat`, `Handoff`, and `Magentic` orchestration patterns.
- `src/backend/Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs`: polls pending tasks, limits cross-project concurrency, and coordinates lease-based execution.
- `src/backend/Agw.Tools/ToolRegistryService.cs`: discovers tools from `[AiTool]` methods and `IAgwTool` implementations, then exposes runtime metadata/functions.
- `src/backend/Agw.Skills/Services/SkillAppService.cs`: validates uploaded skill archives, rewrites `SKILL.md` metadata, and manages the `wwwroot/skills/<name>/` content path.

## Build And Development Commands

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
- The development host launches on `http://localhost:5015` by default via `src/backend/Agw.Host/Properties/launchSettings.json`.
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
pnpm lint
pnpm lint:fix
pnpm format
pnpm gen:openapi
```

Notes:
- The Next.js dev server runs on `http://localhost:3000` by default.
- `src/frontend/web/next.config.ts` rewrites `/api/*` and `/openapi/*` to `BACKEND_API_BASE_URL` (default `http://localhost:5015`).
- Regenerate `src/frontend/web/src/api/openapi.d.ts` after backend contract changes.

## Configuration

### Backend Configuration

Primary settings live in `src/backend/Agw.Host/appsettings.json`:

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
- Register new backend services in `src/backend/Agw.Host/Program.cs` alongside the existing DI setup.
- A2A agent-card and JSON-RPC message endpoints are mounted under `/api/a2a/`; the global discovery list is `/.well-known/agents.json`.

## Coding Style

- Use 4-space indentation and standard C# naming: `PascalCase` for types and members, `camelCase` for locals and parameters, `I` prefix for interfaces.
- Keep request and response DTOs in `Contracts/` folders within the relevant backend module.
- Controllers should end with `Controller`.
- Prefer async methods for I/O and constructor injection for dependencies.
- Frontend code should use TypeScript, React function components, and kebab-case filenames.
- Do not edit generated artifacts unless the task is explicitly about generated output.

## Testing Guidance

- Use xUnit for backend tests when adding coverage.
- Prefer `tests/Agw.*.Tests/` with namespaces mirroring production code.
- Name test methods like `Method_Condition_ExpectedResult`.
- Run `dotnet test Agw.slnx` before handing off backend changes.
- Current in-repo test projects are `Agw.A2A.Tests`, `Agw.Agents.Tests`, `Agw.Tasks.Tests`, and `Agw.Skills.Tests`.

## Frontend Architecture

- Stack: Next.js 16 App Router, React 19, Tailwind CSS 4, Radix UI, TanStack React Query 5, `openapi-fetch`.
- Prefer the typed helpers in `src/frontend/web/src/api/client.ts` for HTTP calls.
- Use `src/frontend/web/src/api/execution-ws.ts` for task execution websocket flows.
- Use `src/frontend/web/src/api/files.ts` for backend file-management endpoints used by the Claude Code UI.
- Keep route-specific UI inside the relevant `src/app/(app)/...` segment and shared UI in `src/components/`.

## A2A Protocol

Agw maps JSON-RPC A2A endpoints by default:

- `GET /.well-known/agents.json`
- `GET /api/a2a/{agentName}/.well-known/agent-card.json`
- `POST /api/a2a/{agentName}`

Example:

```bash
curl http://localhost:5015/.well-known/agents.json
```

## Workflow Expectations

- Follow Conventional Commits such as `feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, and `test:`.
- Keep pull requests focused and include summary, linked issue, testing notes, and migration impact when applicable.
- Include screenshots for UI changes and example payloads or endpoint notes for API changes.
- Preserve unrelated user changes in a dirty worktree; do not revert or rewrite them unless explicitly asked.
