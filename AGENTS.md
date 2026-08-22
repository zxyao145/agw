# Repository Instructions

This document gives coding agents the repository context and mandatory constraints needed to work safely in Agw. `AGENTS.md` and `CLAUDE.md` must remain identical copies of this document.

## Project Overview

Agw is a modular-monolith AaaS platform and agent gateway for system and external Agents, scheduled Jobs, multi-agent Agentflows, and project-scoped Chat execution. Its ASP.NET Core and EF Core backend targets `.NET 10.0` and integrates Microsoft.Agents.AI/MAF, MCP, A2A, and external-agent SDKs from `src/server/Agw.Host/Program.cs`; Next.js, Electron, and Expo provide the clients.

## Repository Map

### Backend (`src/server/`)

```text
Agw.Host/            # ASP.NET Core entry point and composition root
Agw.Data/            # Entities, EF mappings, and repository/unit-of-work contracts
Agw.Infrastructure/  # DbContext, repositories, provider adapters, and seeding
Agw.Migrations.Sqlite/   # SQLite migrations and provider-specific model snapshot
Agw.Migrations.Postgres/ # PostgreSQL migrations and provider-specific model snapshot
Agw.Shared/          # Cross-module contracts, errors, results, and utilities
Agw.A2A/             # A2A discovery, protocol types, and endpoints
Agw.Auth/            # Cookie/Bearer/LocalTrusted auth, CSRF, and guards
Agw.Agents/          # Agent/Agentflow definitions, tools, and runtimes
Agw.Files/           # Project file/Git APIs and path security
Agw.Integrations/    # Plugin catalog, connections, credentials, OAuth, and MCP
Agw.Jobs/            # Scheduling, project leases, and execution logs
Agw.Providers/       # Models, providers, links, and auth configuration
Agw.Setup/           # First-run setup and server-state persistence
Agw.Skills/          # Local/remote Skills and execution adapters
Agw.Projects/        # Projects, tasks, contexts, and history
Agw.Tools/           # Tool/ToolBlock catalog and materialization
```

`Agw.slnx` is the root solution and includes all backend projects and tests.

### Module Layering

Backend modules use `Api → Application → Domain ← Infrastructure`. Api owns protocol adapters, Application owns use cases, Domain contains data-only entities/value objects, and Infrastructure implements persistence and external adapters. Dependencies point inward.

### Client Workspace (`src/clients/`)

`@agw/web`, `@agw/desktop`, `@agw/mobile`, and packages under `src/clients/packages/` share a pnpm Workspace and Turborepo. Run pnpm commands from `src/clients/`.

### Web Client (`src/clients/web/`)

`src/clients/web/src/app/` contains only routes, layouts, global CSS, and shell composition. Business domains, transport, and shared UI live in `src/clients/packages/`; Web routes import their public package entry points.

`src/clients/web/next.config.ts` proxies `/api/*` and `/openapi/*` to the backend unless `NEXT_OUTPUT_MODE=export`.

### Desktop Client (`src/clients/desktop/`)

Electron entry points are under `src/clients/desktop/src/{main,preload}/`. Desktop owns `renderer/`, keeps its bridge adapter in `renderer/src/runtime/` and cross-process contracts in `src/shared/contracts/`, and shares business modules with Web only through root `src/clients/packages/`. Web and Desktop never import or build each other. `@agw/chat-runtime` owns platform-neutral execution and session state; `@agw/chat` owns the DOM renderer and Web/Desktop compatibility surface.

### Mobile Client (`src/clients/mobile/`)

The Expo Router app root is `src/clients/mobile`. Follow the nested `src/clients/mobile/AGENTS.md`, run Mobile pnpm commands from `src/clients/`, and do not hand-maintain generated native projects.

## Deeper Documentation

- [`docs/2.Architecture.md`](docs/2.Architecture.md): module responsibilities, runtime boundaries, client packages, and domain relationships.
- [`docs/6.Agentflow.md`](docs/6.Agentflow.md): graph routing, cycle constraints, checkpoint branching, editor history, and Chat attribution.
- [`src/server/Agw.Agents/Execution/README.md`](src/server/Agw.Agents/Execution/README.md): SignalR commands, in-process/distributed runtimes, turn lifecycle, and extension points.
- [`src/server/Agw.Files/README.zh-CN.md`](src/server/Agw.Files/README.zh-CN.md): workspace resolution, path security, file APIs, and Git behavior.
- [`src/clients/desktop/README.md`](src/clients/desktop/README.md): Desktop runtime, packaging, server profiles, and security boundaries.

## Build, Run, and Test

### Backend

Run backend commands from the repository root:

```bash
dotnet restore Agw.slnx
dotnet tool restore
dotnet build Agw.slnx
dotnet run --project src/server/Agw.Host
dotnet watch --project src/server/Agw.Host
dotnet test Agw.slnx
dotnet csharpier format
```

Run a focused project with `dotnet test tests/Agw.Files.Tests` (or the matching `Agw.*.Tests` project), and use `--filter "FullyQualifiedName~MethodName"` for a specific test.

Unit/composition tests must not construct real `CodexAIAgent` or `ClaudeCodeAIAgent` instances because their constructors may probe local CLIs. Use fake `AIAgent` implementations and pure option-normalization helpers. Real CLI tests must be explicit integration tests gated by opt-in configuration and executable availability, outside the default suite.

Do not add or apply EF Core migrations automatically. Each model change needs matching SQLite and PostgreSQL migrations. When the user explicitly requests migrations, use:

```bash
dotnet ef migrations add <MigrationName> \
  -p src/server/Agw.Migrations.Sqlite \
  -s src/server/Agw.Host \
  -- --provider sqlite

dotnet ef migrations add <MigrationName> \
  -p src/server/Agw.Migrations.Postgres \
  -s src/server/Agw.Host \
  -- --provider postgres

dotnet ef database update \
  --connection "<sqlite-connection-string>" \
  -p src/server/Agw.Migrations.Sqlite \
  -s src/server/Agw.Host \
  -- --provider sqlite

dotnet ef database update \
  --connection "<postgres-connection-string>" \
  -p src/server/Agw.Migrations.Postgres \
  -s src/server/Agw.Host \
  -- --provider postgres
```

### Clients

Run pnpm commands from `src/clients/`; one install covers every client and package:

```bash
pnpm install
pnpm dev:web
pnpm dev:desktop
pnpm dev:mobile
pnpm android:mobile
pnpm ios:mobile
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

The frontend proxy target is resolved in this order: `BACKEND_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:30816`.

Regenerate `src/clients/packages/api/src/openapi.d.ts` with `pnpm gen:api` after backend contract changes.

After the first clone, configure hooks with `git config core.hooksPath .githooks`. Backend tests use xUnit; mirror production namespaces and prefer names such as `Method_Condition_ExpectedResult`.

## Local Setup and Configuration

On the first backend run, open `http://localhost:30816/setup` to choose Standalone or Cluster deployment, enter structured SQLite or PostgreSQL settings, and create the administrator password. Standalone supports both databases; Cluster requires PostgreSQL and takes effect after a Server restart. Setup seeds the database and writes `server-state.json` below the Agw data directory.

Remote web access uses the administrator session cookie. Desktop, mobile, and automation clients use named `Authorization: Bearer agw_...` API tokens. The legacy `X-API-Key` setting is not supported.

Bearer principals inherit the API Token creator's user ID. Execution ownership, audit fields, task-session bindings, checkpoints, and User Memory must use that stable user ID rather than a display name or authentication scheme label.

Primary backend settings live in `src/server/Agw.Host/appsettings.json` under `Database`, `DistributedLock`, `Execution`, and `OpenTelemetry`.

Configuration guidance:

- `Database:Provider` supports `sqlite` and `postgres`.
- `Database:ConnectionString` defaults to `Data Source=agw.db`.
- An optional `Setup` section initializes an absent `server-state.json`; inject its passwords through environment variables or Secrets. Existing state always wins and must not be overwritten.
- `DistributedLock:Provider` supports `inmemory` and `postgres`; null or missing follows `Database:Provider`.
- When `DistributedLock:ConnectionString` is empty, a PostgreSQL lock reuses `Database:ConnectionString`.
- `Execution:Provider` supports `InProcess` and `Distributed`. Distributed execution requires PostgreSQL for both the database and distributed lock; its event stream defaults to PostgreSQL and may explicitly use Redis.
- `OpenTelemetry:OtlpEndpoint` defaults to `http://localhost:4317`.
- First-run configuration plus administrator password/session state live in `server-state.json` through the `Agw.Setup` persistence adapter. API Token hashes and audit metadata live in the `api_token` database table; do not reintroduce static `SystemInitialization` configuration.
- Keep secrets out of `appsettings*.json` and frontend environment files; prefer environment-variable overrides.
- Backend projects target `.NET 10.0` with nullable types, implicit usings, central packages, and build-time style checks.

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
- Use `@agw/chat-core` for platform-neutral message and presentation semantics, `@agw/chat-runtime` for SignalR execution, session/activity state, and Conversation control, `@agw/chat` for the Web/Desktop renderer, and `@agw/chat-native` for the React Native Chat vertical slice.
- Keep business code inside its owning `src/clients/packages/<domain>/` package; `@agw/web` routes should remain thin composition adapters.
- Keep platform-neutral UI in `@agw/components`. Desktop-only Electron React adaptation belongs in `src/clients/desktop/renderer/src/runtime/`, and its cross-process data shapes belong in `src/clients/desktop/src/shared/contracts/`.
- Web and Desktop must not import or depend on each other; application dependencies must resolve through root `src/clients/packages/` workspace packages.
- Mobile imports `@agw/chat-native` as its Chat host plus other React Native-safe domain packages such as `@agw/projects-core`. `@agw/mobile` must not depend on `@agw/chat-core` or `@agw/chat-runtime` directly; `@agw/chat-native` owns those transitive dependencies and the React Native Chat implementation. Mobile must not import `@agw/chat`, Web/Desktop applications, or DOM UI barrels.
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
