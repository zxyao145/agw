# Agw

Agw is a modular monolith for managing LLM agents, agentflows, providers, tools, skills, projects/tasks, jobs, and external-agent execution. It uses an ASP.NET Core + EF Core backend and a Next.js frontend.

## Documentation

The detailed project docs live under [`docs/`](docs/):

- [Development Guide](docs/1.%20Development.md): local setup, build/test/lint/format commands, and git hook configuration.
- [Architecture](docs/2.%20Architecture.md): system overview, backend/frontend structure, and core domain concepts.
- [Module Organization](docs/3.%20Module%20Organization.md): layering principles used inside modules.

## Architecture Snapshot

Agw is organized as a domain-oriented modular monolith. `src/backend/Agw.Host` is the composition root that wires the backend modules together, while the frontend lives under `src/frontend/web`.

A typical backend flow is:

```text
Controller -> AppService / RuntimeService -> DomainService -> IRepository / IUnitOfWork -> EF Core
```

Backend modules:

```text
src/backend/
  Agw.Host/            # ASP.NET Core entry point, DI, OpenAPI, hosted services
  Agw.A2A/             # A2A protocol types and route builders
  Agw.Agents/          # Agents, agentflows, runtime execution services
  Agw.Infrastructure/  # DbContext, repositories, migrations, seeding
  Agw.Jobs/            # Scheduled jobs and execution logs
  Agw.Providers/       # Models, providers, model-provider links, auth configs
  Agw.Shared/          # Shared entities, contracts, repository abstractions, utilities
  Agw.Skills/          # Skill archive validation, storage, and agent-skill relations
  Agw.Tasks/           # Projects, project tasks, task records, file/task APIs
  Agw.Tools/           # Tool discovery, metadata, and AI tool registry/factory
```

Frontend route groups:

```text
src/frontend/web/src/app/(app)/
  (agents)/            # agents, agentflows, MCP servers, skills
  (external-agents)/   # Claude Code UI
  (interface)/         # chat UI
  (overview)/          # dashboard, traces
  (providers)/         # models, providers, model-providers
  (tasks)/             # projects, jobs
  integrations/        # integration management
```

## Quick Start

### First clone

Configure the repo hooks once:

```bash
git config core.hooksPath .githooks
```

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

- The development host runs on `http://localhost:5015` by default.
- `Agw.slnx` includes `Agw.A2A.Tests`, `Agw.Agents.Tests`, `Agw.Tasks.Tests`, and `Agw.Skills.Tests`.
- `tests/Agw.Jobs.Tests` exists in the repo but is not currently included in `Agw.slnx`; run it directly if you change jobs code.

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

- The frontend dev server runs on `http://localhost:3000`.
- `/api/*` and `/openapi/*` are proxied to the backend base URL from `BACKEND_API_BASE_URL`, then `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:5015`.
- Frontend linting/formatting use `oxlint` and `oxfmt`.

## Configuration

Primary backend settings are in [`src/backend/Agw.Host/appsettings.json`](src/backend/Agw.Host/appsettings.json):

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

- Supported database providers are `sqlite` and `postgres`.
- Keep secrets out of committed config files; prefer environment-variable overrides.
- After backend contract changes, regenerate `src/frontend/web/src/api/openapi.d.ts` with `pnpm gen:openapi`.

## Development Notes

### Migrations

Do not add or apply EF Core migrations automatically. When needed:

```bash
dotnet ef migrations add <MigrationName> \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host

dotnet ef database update \
  -p src/backend/Agw.Infrastructure \
  -s src/backend/Agw.Host
```

### Current A2A Status

The `Agw.A2A` module exists in the repository, but `.AddA2A(...)` and `app.MapAgwA2A(...)` are currently commented out in `src/backend/Agw.Host/Program.cs`. Do not assume the A2A endpoints are live until that wiring is enabled.

## Tech Stack

Backend:

- .NET 10
- ASP.NET Core
- Entity Framework Core
- Microsoft.Agents.AI
- Serilog + OpenTelemetry

Frontend:

- Next.js 16 App Router
- React 19
- Tailwind CSS 4
- Radix UI
- TanStack React Query 5
- `openapi-fetch`
