# AGENTS.md

This file provides guidance to coding agents working in this repository.

## Project Overview

D-System is an ASP.NET Core + EF Core backend with a Next.js frontend for managing LLM agents, models, providers, and multi-agent workflows. It follows a modular architecture and integrates Microsoft.Agents.AI, MCP tool servers, A2A endpoints, and external agents such as Claude Code SDK.

## Architecture And Project Structure

### Backend Organization (`src/backend/`)

```text
DSystem.Host/              # ASP.NET Core entry point, DI registration, hosted services
DSystem.Infrastructure/    # EF Core DbContext, repositories, migrations
DSystem.Shared/            # Base entities, enums, shared models, repository interfaces
DSystem.Shared.Contract/   # Interfaces for cross-module interaction
DSystem.Agents/            # Agent definitions, workflows, MCP tools, runtime services
DSystem.Providers/         # LLM models, providers, model-providers, auth configs
DSystem.Tasks/             # Projects, tasks, session records, chat history
DSystem.Jobs/              # Background jobs and project leases
DSystem.A2A/               # A2A protocol implementation for agent discovery and messaging
```

Notes:
- `D-System.slnx` also references sibling SDK projects in `../claude-code-sdk-csharp/`; full solution builds depend on that adjacent checkout.
- `src/backend/DSystem.Contract/` exists in this working tree only as generated `obj/` content and is not part of the solution.

### Frontend Organization (`src/frontend/web/`)

```text
src/app/(app)/
  (agents)/agents            # Agent CRUD
  (agents)/agentflows        # Workflow editor
  (agents)/mcp-tool-servers  # MCP server management
  (external-agents)/claude-code
  (overview)/dashboard
  (overview)/traces
  (providers)/models
  (providers)/providers
  (providers)/model-providers
  (tasks)/projects
src/api/                     # Typed API helpers and generated OpenAPI types
src/components/              # Shared UI components
```

### Repository Layout

- `docs/` contains project documentation.
- `scripts/` contains helper scripts such as API smoke tests.
- Treat `bin/`, `obj/`, `.next/`, and `node_modules/` as generated artifacts.

## Key Domain Concepts

- `Agent`: AI agent with prompt, type, model-provider linkage, and MCP tool bindings.
- `Agentflow`: Multi-agent workflow with nodes, edges, and orchestration pattern.
- `McpToolServer`: MCP server configuration for stdio, HTTP, or SSE transport.
- `LlmModel`, `Provider`, `ModelProvider`, `ProviderAuthConfig`: model and provider catalog plus auth configuration.
- `Project`, `ProjectTask`, `TaskRecord`, `ProjectLease`: task execution, conversation persistence, and concurrency control.

## Core Runtime Services

- `src/backend/DSystem.Agents/Services/AgentRuntimeService.cs`: creates runtime agents, hydrates provider configuration, wires tools, and supports external agent session resumption.
- `src/backend/DSystem.Agents/Services/AgentflowRuntimeService.cs`: executes workflows with `Concurrent`, `Sequential`, `GroupChat`, `Handoff`, and `Magentic` orchestration patterns.
- `src/backend/DSystem.Host/ProjectTaskSchedulerHostedService.cs`: polls pending tasks, limits cross-project concurrency, and coordinates lease-based execution.

## Build And Development Commands

### Backend

Run from the repo root:

```bash
dotnet restore D-System.slnx
dotnet build D-System.slnx
dotnet run --project src/backend/DSystem.Host
dotnet watch --project src/backend/DSystem.Host
dotnet test D-System.slnx
dotnet format
```

Notes:
- The development host launches on `http://localhost:5015` by default via `src/backend/DSystem.Host/Properties/launchSettings.json`.
- Do not add or apply EF Core migrations automatically. When needed, use:

```bash
dotnet ef migrations add <MigrationName> \
  -p src/backend/DSystem.Infrastructure \
  -s src/backend/DSystem.Host

dotnet ef database update \
  -p src/backend/DSystem.Infrastructure \
  -s src/backend/DSystem.Host
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
- Regenerate `src/frontend/web/src/api/openapi.d.ts` after backend contract changes.

## Configuration

### Backend Configuration

Primary settings live in `src/backend/DSystem.Host/appsettings.json`:

```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=d_system.db"
  },
  "OpenTelemetry": {
    "ServiceName": "DSystem",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Guidance:
- Supported database providers are `sqlite` and `postgres`.
- Keep secrets out of `appsettings*.json` and frontend env files; prefer environment-variable overrides.
- Register new backend services in `src/backend/DSystem.Host/Program.cs` alongside the existing DI setup.

## Coding Style

- Use 4-space indentation and standard C# naming: `PascalCase` for types and members, `camelCase` for locals and parameters, `I` prefix for interfaces.
- Keep request and response DTOs in `Contracts/` folders within the relevant backend module.
- Controllers should end with `Controller`.
- Prefer async methods for I/O and constructor injection for dependencies.
- Frontend code should use TypeScript, React function components, and kebab-case filenames.
- Do not edit generated artifacts unless the task is explicitly about generated output.

## Testing Guidance

- Use xUnit for backend tests when adding coverage.
- Prefer `tests/DSystem.*.Tests/` with namespaces mirroring production code.
- Name test methods like `Method_Condition_ExpectedResult`.
- Run `dotnet test D-System.slnx` before handing off backend changes.
- This checkout does not currently include in-repo test projects; add focused tests with new behavior when practical.

## Frontend Architecture

- Stack: Next.js 16 App Router, React 19, Tailwind CSS 4, Radix UI, TanStack React Query 5, `openapi-fetch`.
- Prefer the typed helpers in `src/frontend/web/src/api/client.ts` for HTTP calls.
- Use `src/frontend/web/src/api/execution-ws.ts` for task execution websocket flows.
- Keep route-specific UI inside the relevant `src/app/(app)/...` segment and shared UI in `src/components/`.

## A2A Protocol

D-System exposes agents over A2A endpoints:

- `GET /a2a/agents`
- `GET /a2a/{agentName}/v1/card`
- `POST /a2a/{agentName}/v1/message:stream`

Example:

```bash
curl -X POST http://localhost:5015/a2a/my-agent/v1/message:stream \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{"messages":[{"role":"user","content":"Hello!"}],"context":{"sessionId":null}}'
```

## Workflow Expectations

- Follow Conventional Commits such as `feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, and `test:`.
- Keep pull requests focused and include summary, linked issue, testing notes, and migration impact when applicable.
- Include screenshots for UI changes and example payloads or endpoint notes for API changes.
- Preserve unrelated user changes in a dirty worktree; do not revert or rewrite them unless explicitly asked.
