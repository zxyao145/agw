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
Agw.Auth/            # Administrator Cookie/Bearer authentication, LocalTrusted, CSRF, and authorization guards
Agw.Agents/          # Agent definitions, agentflows, MCP tools, and runtime execution services
Agw.Files/           # File and workspace APIs, path security, request validation, and error mapping
Agw.Integrations/    # Plugin catalog, installations, connections, credentials, OAuth, MCP, and connection-bound tools
Agw.Jobs/            # Scheduled jobs, project leases, execution logs, and hosted scheduling
Agw.Providers/       # LLM models, providers, model-provider links, and auth configuration
Agw.Setup/           # First-run setup, initialization state, and the combined server-state persistence adapter
Agw.Skills/          # Skill archive validation, storage, and agent-skill relations
Agw.Projects/         # Projects, project tasks, task records, contexts, and task APIs
Agw.Tools/           # Tool discovery, metadata, and AI tool factory and registry
```

`Agw.slnx` is the root solution and includes the backend projects and tests. `src/server/server.sln` includes backend projects only. The root solution includes test projects for A2A, Agents, Auth, Files, Host, Integrations, Jobs, Projects, Setup, Shared, Skills, and Tools.

### Module Layering

Backend modules use lightweight layering:

```text
Api → Application → Domain ← Infrastructure
```

Api owns protocol adapters; Application owns use cases and business behavior; Domain contains data-only entities and value objects; Infrastructure implements persistence and external adapters. Dependencies point inward. A typical flow is `Controller → AppService / RuntimeService → DomainService → IRepository / IUnitOfWork → EF Core`.

### Client Workspace (`src/clients/`)

`@agw/web`, `@agw/desktop`, and the `@agw/*` packages under `src/clients/packages/` share a pnpm Workspace and use Turborepo for task orchestration. Run pnpm commands from `src/clients/`; one `pnpm install` installs the whole workspace. The Expo mobile app remains a separate npm workspace.

### Web Client (`src/clients/web/`)

The web client uses Next.js 16 App Router, React 19, Tailwind CSS 4, Radix UI/Shadcn components, React Query, and generated `openapi-fetch` types.

`src/clients/web/src/app/` contains only Next.js routes, layouts, global CSS, and application-shell composition. Business domains live in `src/clients/packages/` (`agents`, `auth`, `chat`, `integrations`, `jobs`, `observability`, `projects`, `providers`, `settings`, and `skills`); transport and shared UI live in `http-client`, `api`, and `components`. Web routes import public package entry points rather than owning domain implementations.

`src/clients/web/next.config.ts` proxies `/api/*` and `/openapi/*` to the backend unless `NEXT_OUTPUT_MODE=export`.

### Desktop Client (`src/clients/desktop/`)

The Electron entry points are `src/clients/desktop/src/main/index.ts` and `src/clients/desktop/src/preload/index.ts`. Desktop owns an independent Next.js React renderer under `src/clients/desktop/renderer/`; its Electron bridge adapter lives in `src/clients/desktop/renderer/src/runtime/`, while cross-process data shapes remain internal under `src/clients/desktop/src/shared/contracts/`. Web and Desktop do not import, locate, build, or consume artifacts from each other. Both applications reuse business and infrastructure modules only through root `src/clients/packages/`. Chat owns its execution status model.

### Mobile Client (`src/clients/mobile/`)

The Expo app root is `src/clients/mobile/shared`. Follow the nested `src/clients/mobile/AGENTS.md`, run mobile npm commands from `shared/`, and do not hand-maintain generated native projects.

## Deeper Documentation

- [`docs/2.Architecture.md`](docs/2.Architecture.md): module responsibilities, runtime boundaries, client packages, and domain relationships.
- [`src/server/Agw.Agents/Execution/README.md`](src/server/Agw.Agents/Execution/README.md): SignalR commands, runtimes, turn lifecycle, and extension points.
- [`src/server/Agw.Files/README.zh-CN.md`](src/server/Agw.Files/README.zh-CN.md): workspace resolution, path security, file APIs, and Git behavior.
- [`src/clients/desktop/README.md`](src/clients/desktop/README.md): Desktop runtime, packaging, server profiles, and security boundaries.

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

Run pnpm commands from `src/clients/`. The single install covers Web, Desktop, and all packages under `src/clients/packages/`:

```bash
pnpm install
pnpm dev:web
pnpm build
pnpm lint
pnpm test
pnpm fmt
pnpm fmt:check
pnpm gen:api
```

Run `pnpm dev:web` for the browser application or `pnpm dev:desktop` for the independent Electron application and its renderer. Web listens on `http://localhost:3001`; the Desktop renderer listens on `http://localhost:3000` during development. Linting and formatting use `oxlint` and `oxfmt`, not ESLint or Prettier. Turborepo uses its local task cache only; remote caching is disabled.

Use root scripts where available, or run a package-specific Web task with `pnpm exec turbo run <task> --filter=@agw/web`. Package Desktop installers from `src/clients/` with:

```bash
AGW_PACKAGE_FLAVOR=client pnpm make:desktop
AGW_PACKAGE_FLAVOR=full pnpm make:desktop
pnpm make:desktop -- --arch=x64
```

The frontend proxy target is resolved in this order: `BACKEND_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:30815`.

Regenerate `src/clients/packages/api/src/openapi.d.ts` with `pnpm gen:api` after backend contract changes.

After the first clone, configure hooks with `git config core.hooksPath .githooks`. Backend tests use xUnit; mirror production namespaces and prefer names such as `Method_Condition_ExpectedResult`.

## Local Setup and Configuration

On the first backend run, open `http://localhost:30815/setup` to choose the database provider, connection string, and administrator password. Setup seeds the database and writes `server-state.json` below the Agw data directory.

Remote web access uses the administrator session cookie. Desktop, mobile, and automation clients use named `Authorization: Bearer agw_...` API tokens. The legacy `X-API-Key` setting is not supported.

Primary backend settings live in `src/server/Agw.Host/appsettings.json` under `Database`, `DistributedLock`, and `OpenTelemetry`.

Configuration guidance:

- `Database:Provider` supports `sqlite` and `postgres`.
- `Database:ConnectionString` defaults to `Data Source=agw.db`.
- `DistributedLock:Provider` supports `inmemory` and `postgres`; null or missing follows `Database:Provider`.
- When `DistributedLock:ConnectionString` is empty, a PostgreSQL lock reuses `Database:ConnectionString`.
- `OpenTelemetry:OtlpEndpoint` defaults to `http://localhost:4317`.
- First-run and authentication state, including API Tokens, live in `server-state.json` through the `Agw.Setup` persistence adapter; do not reintroduce static `SystemInitialization` configuration.
- Keep secrets out of `appsettings*.json` and frontend environment files; prefer environment-variable overrides.
- All backend projects target `.NET 10.0` and use nullable reference types, implicit usings, central package management, and code-style enforcement during builds.

## Mandatory Repository Rules

Read [`docs/rules.md`](docs/rules.md) before coding. Its rules are mandatory.

### Backend API Responses and Exceptions

- All non-WebSocket JSON endpoints return Bens.Results envelopes through `ApiResult` helpers or the configured boundary mapping; do not return bare MVC results.
- Return `ApiResult.Ok(...)` or another appropriate `ApiResult.*` helper directly. Use `ErrorCode.ToApiResult()` or `AgwException.ToApiResult()` for shared errors, and let `AgwApiExceptionMiddleware` map uncaught `AgwException` values. `[ProducesApiResult]` supplies metadata only.
- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep protocol-specific response formats.

### A2A

- `Agw.Host/Program.cs` registers A2A through `.AddA2A(builder.Configuration)`, maps it through `app.MapAgwA2A(a2AServerOptions.Prefix)`, and requires authentication at the host boundary.

### Project Workspaces

- `Project.Workspace` is the only file-root source. Files APIs and in-process consumers use `projectId` plus project-relative paths.
- The Workspace must be visible to the Agw process. Mount network storage through the operating system or container platform; do not reintroduce application-level SFTP or `fileStorage` backends.
- Files, Git, Claude Code, Codex, compilers, and shells must consume the same host-visible working tree.
- `ProjectScopedFileSystemResolver` caches `CachedEntry(FileSystem, CreatedAt)` by Project ID without TTL; changing an already-resolved Workspace requires restart unless explicit invalidation is designed.

## Frontend Integration

- Prefer the typed helpers exported by `@agw/api` from `src/clients/packages/api/src/client.ts` for REST calls. That client unwraps Bens.Results envelopes before data reaches domain packages.
- Use `@agw/projects` for project tasks, contexts, histories, and file-management flows.
- Use `@agw/chat` for SignalR execution, Chat state, and reusable React renderer UI.
- Keep business code inside its owning `src/clients/packages/<domain>/` package; `@agw/web` routes should remain thin composition adapters.
- Keep platform-neutral UI in `@agw/components`. Desktop-only Electron React adaptation belongs in `src/clients/desktop/renderer/src/runtime/`, and its cross-process data shapes belong in `src/clients/desktop/src/shared/contracts/`.
- Web and Desktop must not import or depend on each other; application dependencies must resolve through root `src/clients/packages/` workspace packages.
- Packages must not import `@agw/web`, `web/src`, or the Web `@/` alias. Run `pnpm test:boundaries` after changing package boundaries.

## Coding Conventions

### Backend

- Use 4-space indentation.
- Use `PascalCase` for types and members, `camelCase` for locals and parameters, and the `I` prefix for interfaces.
- Prefer async methods for I/O and explicit constructor injection for dependencies.
- Do not use C# primary constructors. Declare explicit constructors and backing fields or properties.
- Keep request and response DTOs in `Contracts/` folders inside the owning module.
- Controller class names must end with `Controller`.
- Persisted auditable entities use the shared `BaseEntity` and audit interfaces. Keep audit stamping and `ISoftDelete` handling in the registered EF Core interceptors instead of adding module-specific persistence paths.

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
