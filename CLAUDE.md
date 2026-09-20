# Repository Instructions

Keep `AGENTS.md` and `CLAUDE.md` identical. Read mandatory [docs/rules.md](docs/rules.md) before coding.

## Project and Documentation

Agw is a modular-monolith agent gateway for Agents, Jobs, Agentflows, and Chat: ASP.NET Core/EF Core on .NET 10 with MAF/MCP/A2A, plus Next.js, Electron, and Expo clients.

| Reference | Read when working on |
| --- | --- |
| [Development](docs/1.Development.md) | Setup, build/test/format, API errors, and provider-specific migration commands |
| [Architecture](docs/2.Architecture.md) | Module ownership, Host roles, persistence, and client boundaries |
| [Module Organization](docs/3.Module%20Organization.md) | Behavior/Policy rules, persistence seams, and architecture guards |
| [Deployment](docs/4.Deployment.md) | Configuration precedence, initialization, split Hosts, releases, and upgrades |
| [Agentflow](docs/6.Agentflow.md) | Graph invariants, checkpoints, editor history, and Chat attribution |
| [Execution](src/server/Agw.Agents.Execution/README.md) | SignalR commands, runtime lifecycle, external Agents, and durable workers |
| [Files](src/server/Agw.Files/README.zh-CN.md) | Workspace resolution, path security, and Git |
| [Desktop](src/clients/desktop/README.md) | Electron runtime, packaging, and server profiles |

### Immutable approach documents

- Documents under `docs/approachs/` are append-only records. New documents MUST use the filename format `{时间：yyyy-MM-dd}-{方案名}`.
- Once a document under `docs/approachs/` has been committed to Git, it MUST NOT be modified, deleted, or renamed. This is an absolute repository rule.

## Repository Map

`Agw.slnx` is the root solution; backend projects live under `src/server/`:

- `Agw.Host` is the shared Hosting Module. `Agw.ControlPlane.Host` owns setup/Web/management/Jobs; `Agw.DataPlane.Host` owns Execution/A2A/workers; `Agw.Standalone.Host` combines them and produces `agw-server`.
- Clients and shared packages form one pnpm/Turborepo workspace under `src/clients/`. Run pnpm commands there.

## Backend Boundaries

- Use `Api → Application → Domain ← Infrastructure`; create only needed layers. Domain stays framework-free; Application owns use cases, authorization, external queries, transactions, persistence, and error mapping.
- Every persisted entity/table has one logical owner. Shared CLR/EF types in `Agw.Data` and a shared database/schema grant no cross-module query/write ownership.
- Modules use their own `Application/Persistence/I<Module>DbContext`. One scoped `AgwDbContext` implements nine seams. Cross-module access uses Contracts or approved Infrastructure adapters; cross-module transactions stay in Infrastructure.
- `Agw.Agents.Execution → Agw.Agents` is one-way. Both assemblies form the same Agents module. Host composes `AddAgents` and `AddAgentExecution`; topology, persistence seams, and data-only durable manifests stay in Agents. Management Mermaid uses `IAgentflowMermaidProvider` from Contracts.
- SQLite/PostgreSQL migrations use `NoForeignKeyModelDiffer`: never generate or manually add database foreign keys/foreign-key operations. EF relationships may remain; Application/Infrastructure validate references and clean up relations.
- Register services in the owning module's DI seam and compose them through the relevant Host Module.
- Only Data Plane and Standalone map authenticated A2A. Control Plane must not map A2A routes.
- Validate execution permission capabilities at `/api/agents/permission-capabilities`; Codex/Pi support FullAccess only. Permission changes affect the next turn; active turns and durable recovery keep the original snapshot.

## Anemic Data and Behavior

- Entities, value objects, domain data, snapshots, and Decisions contain data only: no business methods, validation, normalization, transitions, derived domain decisions, or domain-event collections.
- Application constructs concrete owner-module `Domain/Behaviors/<Entity>Behavior` with `new`. Never register it with IoC, add `I<Entity>Behavior`, cache/serialize/share it, or reuse it across use cases.
- Behavior binds one root and may mutate only that root and its owned children. Root-local preconditions may run immediately; Application must fully load all owned children before a Behavior method reads or mutates them.
- External facts, time, and actor identity are method parameters. Behavior cannot depend on Policy, DomainService, Repository, DbContext, HTTP, files, MAF/MCP, current-user accessors, IServiceProvider, or Infrastructure adapters.
- Application independently evaluates Policy into a data-only Decision, then asks Behavior to apply it. Policy is manually constructed by default. A genuine cross-boundary DomainService may use IoC only if stateless and dependent solely on pure Domain components; it must not capture a Behavior or root.
- Reusable read-only algorithms belong in neutral Topology/Rules components. Application alone orchestrates Policy/DomainService/Behavior.
- Reconcile EF-tracked children in place by stable key. Never replace a tracked navigation before querying old rows, or delete/re-add separate instances with the same tracked key. Audit stamping stays in EF interceptors.
- Selective DDD is limited to Agentflow graph rules: `AgentflowDefinitionPolicy` produces Decisions, `AgentflowBehavior` applies them, and `AgentflowTopology` serves shared algorithms. CRUD, checkpoint/trace orchestration, and simple modules stay in Application/Infrastructure.
- Do not create empty Behaviors for simple CRUD, settings, audit rows, or read models. Do not add single-root DomainServices or expand architecture-test debt allowlists.

## Ownership and Integrations

- Business/configuration/execution roots use immutable `CreateBy` or existing `UserId`; children inherit root ownership. Missing/unstable authenticated IDs fail closed. Foreign IDs return the same NotFound/InvalidParam as nonexistent resources.
- Read the current owner via `UserInfoUtil.RequiredUserId` or injected `IUserInfoService`. Bearer identity is the API Token creator's stable ID, never a display name or authentication scheme. Preserve that ID in execution, audit, sessions, checkpoints, and User Memory.
- `UserScopeFilter` fails closed without user context. HTTP/execution establish the owner; only token validation, seeding, and scheduler scans may enter restricted system scope.
- Approved `AgentDeletionCoordinator` and `ProjectDeletionCoordinator` Infrastructure adapters may bypass only the named user filter for child cleanup tied to a verified surviving root owner. Retain soft-delete filtering; never infer ownership when every root is missing.
- Product terminology is **Available integrations** for catalog definitions and **Configured integrations** for user accounts/endpoints. Keep developer contracts precise: `PluginDefinition`, `PluginInstallation`, `Connection`, and `ConnectionId`; `Connector` means service/protocol variant. Preserve real transport/database connection terminology.
- Catalog definitions are globally readable to authenticated users. `PluginInstallation` is per-user `(CreateBy, PluginId)` setup; changes invalidate only that user's Connections. Seed owner `1001` is not a setup bypass.
- `Connection.CreateBy` is its owner. Alias is immutable and unique within `(CreateBy, Alias)`. CRUD, OAuth, credentials, binding projection, and Native/MCP invocation must reject foreign IDs without disclosing ownership. Only owner-matched Ready Connections contribute runtime capabilities.
- Agent/Project Connection bindings are owner overlays; preserve other users' bindings when editing.
- Auth owns local users, external identities, the transactional user-ID allocator, and one-time Desktop login grants. New user IDs start at `10000`; administrator `1001` is preserved. Claims and owner contracts retain decimal-string IDs. OIDC identities use verified `(issuer, sub)`, never email-based linking. Auth Infrastructure may bypass only the named user filter for verified identity lookup, proof-bound grant exchange, and expired-grant cleanup; business queries remain owner-scoped.

## APIs, Tools, and Coding

- Non-WebSocket JSON endpoints return Bens.Results envelopes through `ApiResult.Ok(...)`, other `ApiResult.*` helpers, or configured boundary mapping. Use `ErrorCode.ToApiResult()` / `AgwException.ToApiResult()` for shared errors; uncaught `AgwException` maps through `AgwApiExceptionMiddleware`. `[ProducesApiResult]` supplies metadata only.
- WebSockets, OAuth redirects, A2A, and static files may retain protocol-specific responses. Follow `docs/rules.md` for query/body identifiers and stable seven-digit error codes.
- Every `IAgwTool`, `IContextualTool`, attributed Tool, and ToolBlock member explicitly declares `AgwToolPermission`. Standalone Tools and attributed containers stay stateless; ToolBlock state belongs in its Provider, session, or owned storage.
- Shared Tool declarations live in `Agw.Tools.Abstractions`, which depends only on `Microsoft.Extensions.AI.Abstractions`. General-purpose implementations live under `Agw.Tools/Impl/Tools`, `Impl/ContextualTools`, and `Impl/ToolBlocks`; business Tools live in their owner module’s `Application/Tools`, with DTOs in `Contracts/Tools`.
- The global Tool catalog discovers only explicitly selected assemblies (default: `Agw.Tools`). Skill-owned Tools are declared through `IAgentSkillRegistration.Tools`, bound to the runtime Project, and validated/permission-bound by Execution; they are not global catalog entries. Keep tool declarations stateless and project bindings in materialized functions.
- Use 4-space indentation, PascalCase types/members, camelCase locals/parameters, `I`-prefixed interfaces, async I/O, and explicit constructor injection. Never use C# primary constructors.
- Keep DTOs in the owning module's `Contracts/` folders; Controller class names end with `Controller`.
- Auditable persisted entities use shared `BaseEntity` and audit interfaces. Keep audit stamping and `ISoftDelete` handling in registered EF interceptors.
- Use `DateTimeOffset`, never `DateTime`, and `TimeProvider` where applicable. Store one deployment-wide time zone (prefer UTC); serialize RFC 3339 with `Z` or an offset. Clients localize dates/times.

## Client Boundaries

- Use TypeScript, React functions, App Router, and kebab-case filenames.
- Web `src/app/` holds routes/layouts/global CSS/shell composition only. Business code belongs in root `packages/<domain>/`, imported through public entry points.
- Web and Desktop never import, locate, build, or consume each other's artifacts. Desktop owns `renderer/`, Electron adaptation in `renderer/src/runtime/`, and bridge contracts in `src/shared/contracts/`.
- `@agw/api` typed helpers unwrap Bens.Results. `@agw/projects` owns tasks/contexts/history/files; shared platform-neutral UI belongs in `@agw/components`.
- `@agw/chat-core` owns message/presentation semantics; `@agw/chat-runtime` owns SignalR execution, session/activity state, and Conversation control; `@agw/chat` owns the DOM renderer and Web/Desktop compatibility surface.
- Mobile imports `@agw/chat-native` as its Chat host plus React Native-safe packages such as `@agw/projects-core`. It must not directly depend on `@agw/chat-core`/`@agw/chat-runtime`, import `@agw/chat`, DOM UI barrels, or Web/Desktop apps. Follow `src/clients/mobile/AGENTS.md`; native projects are generated through Expo CNG.
- Packages never import `@agw/web`, `web/src`, or Web's `@/` alias. Run `pnpm test:boundaries` after boundary changes.

## Build, Run, and Test

From the repository root:

```bash
dotnet restore Agw.slnx
dotnet tool restore
dotnet build Agw.slnx
dotnet run --project src/server/Agw.Standalone.Host
dotnet watch --project src/server/Agw.Standalone.Host
dotnet test Agw.slnx
dotnet csharpier format
```

Run split Hosts with `dotnet run --project src/server/Agw.ControlPlane.Host` or `src/server/Agw.DataPlane.Host`. Focus xUnit tests with `dotnet test tests/Agw.Files.Tests --filter "FullyQualifiedName~MethodName"`. Mirror production namespaces; name tests `Method_Condition_ExpectedResult`.

Unit/composition tests use fake `AIAgent` and pure option helpers, never real `CodexAIAgent`/`ClaudeCodeAIAgent` constructors that probe CLIs. Real CLI tests require opt-in and executable availability outside the default suite.

Do not add or apply EF migrations automatically. Model changes need matching SQLite and PostgreSQL migrations; generate/apply them only when explicitly requested, using the Development Guide's provider-specific commands.

From `src/clients/`:

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

- Web uses `3001`; Desktop renderer uses `3000` independently. Prefer root scripts; focus tasks with `pnpm exec turbo run <task> --filter=@agw/web`.
- Lint/format use `oxlint`/`oxfmt`, not ESLint/Prettier. Turborepo uses local cache only.
- Package Desktop with `AGW_PACKAGE_FLAVOR=client pnpm make:desktop` or `AGW_PACKAGE_FLAVOR=full pnpm make:desktop`; pass architecture with `pnpm make:desktop -- --arch=x64`.
- After backend contract changes, run `pnpm gen:api` to regenerate `packages/api/src/openapi.d.ts`.

## Configuration and Workspaces

- `src/server/Agw.Host/appsettings.json` follows standard ASP.NET precedence above built-in defaults. Its five deployment defaults stay omitted. Override database provider/connection string together; restart for deployment changes.
- Defaults: SQLite (`Data Source=agw.db`) and InProcess. Split Hosts require PostgreSQL database/locks and Distributed execution; initialize Control Plane before Data Plane. Replay defaults to PostgreSQL; Redis is optional.
- `DistributedLock:Provider` supports `inmemory`/`postgres`; absent/null follows `Database:Provider`. An empty PostgreSQL lock connection string reuses `Database:ConnectionString`.
- First run: `/setup` at port `30816` or injected `Setup:AdminPassword`; successful setup initializes the database and the global auth configuration group without restart. Existing auth wins; old Setup deployment fields are rejected before initialization.
- Initialization and administrator password hashes/session versions live in the global `auth` group in the Settings-owned `setting` table; all Hosts read the same database. API Token hashes/audit live in `api_token`. Never read or write `server-state.json`, import legacy JSON Tokens, or restore legacy deployment fallback, `SystemInitialization`, or `X-API-Key`. Remote Web uses administrator or OIDC user cookies; Desktop uses OIDC-issued or manually configured Tokens, and Mobile/automation use named `Authorization: Bearer agw_...` Tokens.
- `AgwDataDir` defaults to `~/agw` and is the only data-root configuration key. Independent `AgwLogDir` defaults to `./logs`. Both use standard precedence, expand `~`, resolve other relative paths from the working directory, and require restart.
- History uses `ConversationHistory:Mode=Interval`: the Host template sets `FlushIntervalSeconds=10`, with a 5-second code fallback when omitted. Blank/missing `OpenTelemetry:OtlpEndpoint` falls back to `http://localhost:4317`. Inject secrets through environment/Secrets, never appsettings or frontend env files.
- Web proxies `/api/*` and `/openapi/*` unless `NEXT_OUTPUT_MODE=export`. Target precedence is `BACKEND_API_BASE_URL`, `NEXT_PUBLIC_API_BASE_URL`, then `http://localhost:30816`.
- `Project.Workspace` defines the primary directory and default cwd; blank values retain `~/.agw/projects/{projectId:N}`. Project-owned `AdditionalDirectories` stores stable IDs and host paths in a JSON column. Changing an additional path creates a new ID; removing an association never deletes files. Mount network storage through the OS/container; never add SFTP or `fileStorage` backends.
- File/Git APIs use `projectId`, optional `directoryId`, and relative paths, returning `ApiResult.Ok` envelopes. An omitted directory ID selects the primary root; foreign or unavailable directories fail without fallback. Web/Desktop and Mobile select the browsing root with a dropdown; browsing never changes Agent cwd.
- `ProjectScopedFileSystemResolver` reauthorizes the Project and caches by Project ID, directory ID, and normalized path. Project updates/deletion invalidate local entries. Files refresh immediately; Agents capture an immutable directory snapshot and fingerprint each turn, including child execution and durable recovery. Changes rebuild runtimes next turn while preserving conversation/session identities. Durable recovery requires an explicit owner and captured workspace snapshot; incomplete manifests are rejected without backfill. All execution nodes must see the captured host paths.

## Workflow

- Preserve unrelated local changes. Touch only what the task requires; do not edit generated artifacts unless generated output is part of the task.
- After cloning, configure hooks with `git config core.hooksPath .githooks`.
- Never create a commit or remote PR automatically; require explicit user authorization. Commit messages use Conventional Commits (`feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, `test:`).
- Keep PRs focused; include summary, linked issue, testing notes, migration impact, UI screenshots, and API payload/endpoint notes where applicable.
