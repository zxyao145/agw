# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Agw is a modular-monolith agent gateway (Agents, Agentflows, Jobs, Chat, Integrations) built on ASP.NET Core + EF Core (.NET 10) with Microsoft.Agents.AI, MCP, and A2A. Clients are a Next.js Web app, an Electron Desktop app, and an Expo Mobile app in one pnpm/Turborepo workspace.

## Reference documents

| Document | Read when working on |
| --- | --- |
| [docs/1.Development.md](docs/1.Development.md) | Build/test commands, error-code rules, EF migration commands, client tasks |
| [docs/2.Architecture.md](docs/2.Architecture.md) | Backend project graph, Host roles, entity relationships, client package roles |
| [docs/4.Deployment.md](docs/4.Deployment.md) | Configuration precedence, first run, split Hosts, Distributed execution, OIDC |
| [docs/human/4.module-organization.md](docs/human/4.module-organization.md) | Layers, cross-module calls, Behavior rules, persistence |
| [docs/human/5.domain-architecture.md](docs/human/5.domain-architecture.md) | Behavior vs DomainService vs ApplicationService |
| [docs/human/1.code-specification.md](docs/human/1.code-specification.md), [2.csharp-coding-rules.md](docs/human/2.csharp-coding-rules.md), [6.repository-constraint.md](docs/human/6.repository-constraint.md) | Mandatory coding, logging, API, and exception rules |
| [src/server/Agw.Agents.Execution/README.md](src/server/Agw.Agents.Execution/README.md) | SignalR execution, runtimes, InProcess vs Distributed, HITL, history batching |
| [docs/ws-flow.md](docs/ws-flow.md) | Execution WebSocket/SignalR protocol |
| [src/clients/mobile/AGENTS.md](src/clients/mobile/AGENTS.md) | Mobile structure, allowed imports, token storage |

`docs/approachs/` holds technical proposals. Once a file there is committed it must never be modified, deleted, or renamed. New proposals are named `{yyyy-MM-dd}-{name}.md` and follow the template in `docs/approachs/README.md`.

## Commands

### Backend (repository root)

```bash
git config core.hooksPath .githooks   # once after cloning
dotnet restore Agw.slnx
dotnet tool restore                   # installs CSharpier
dotnet build Agw.slnx
dotnet watch --project src/server/Agw.Standalone.Host   # http://localhost:30816
dotnet test Agw.slnx
```

- Split Hosts: `dotnet run --project src/server/Agw.ControlPlane.Host` and `src/server/Agw.DataPlane.Host` (Data Plane on `30817`). They require PostgreSQL and `Execution:Provider=Distributed`.
- Tests are xUnit v3 on Microsoft.Testing.Platform (`global.json`). Focused runs:
  - `dotnet test tests/Agw.Files.Tests --filter "FullyQualifiedName~MethodName"`
  - `dotnet test --project tests/Agw.Files.Tests --filter-method "*MethodName"`
- Run `dotnet test tests/Agw.Architecture.Tests` after any change to project references, module boundaries, persistence access, entities, or Behaviors.
- Run `dotnet test tests/Agw.Shared.Tests` after changing `ErrorCodes` or exception rules.
- PostgreSQL-only durable execution tests need `AGW_TEST_POSTGRES_CONNECTION_STRING`. Postgres-backed Auth tests need `AGW_TEST_OIDC_POSTGRES`. The exact CI invocation is in `docs/1.Development.md`.
- Formatting: `dotnet csharpier format <changed files>`. Format only the files you touched. `./format.sh` formats changed `.cs` files and runs `pnpm fmt`. The pre-commit hook runs `dotnet csharpier check .` plus `oxlint`/`oxfmt --check` on `src/clients/web/src`.
- Server builds audit NuGet packages and fail on High/Critical advisories (`NU1903`/`NU1904` are errors).
- EF migrations are never added or applied automatically. A model change needs matching migrations in both `Agw.Migrations.Sqlite` and `Agw.Migrations.Postgres`. Generate them only when asked, using the provider-specific commands in `docs/1.Development.md`.
- Release packaging goes through `publish.sh` (`PUBLISH_MODE=portable|docker|all`).

### Clients (run from `src/clients`)

```bash
pnpm install
pnpm dev:web        # http://localhost:3001, proxies /api and /openapi to :30816
pnpm dev:desktop    # renderer on http://localhost:3000; does not need dev:web
pnpm dev:mobile     # also android:mobile / ios:mobile
pnpm build | typecheck | lint | test | fmt | fmt:check
pnpm gen:api        # regenerate packages/api/src/openapi.d.ts after backend contract changes
pnpm test:boundaries
```

- One package: `pnpm exec turbo run test --filter=@agw/chat-core`, or `pnpm --filter @agw/mobile test`.
- One test file: packages and Desktop use the Node test runner through `tsx`, for example `pnpm --filter @agw/chat-core exec tsx --test src/auto-scroll.test.ts`. Mobile uses Jest.
- Web e2e: `pnpm --filter @agw/web exec playwright install chromium`, then `pnpm --filter @agw/web test:e2e`. It runs its own server on `127.0.0.1:3101` with stubbed APIs.
- `pnpm test` runs `test:boundaries` first (manifest checks plus dependency-cruiser with `.dependency-cruiser.cjs`), then every package's `typecheck` and `test`.
- Desktop installers: `AGW_PACKAGE_FLAVOR=client|full pnpm make:desktop [-- --arch=x64]`.

## Backend architecture

### Hosts

`Agw.Host` is the shared Hosting Module that composes every module's `Add<Module>()` registration. The executables are:

- `Agw.ControlPlane.Host`: Setup, auth management, management APIs, OpenAPI, static Web UI, Job scheduling.
- `Agw.DataPlane.Host`: the SignalR hub `/api/hubs/exec`, A2A routes, and durable execution workers. It does not map management controllers or Setup.
- `Agw.Standalone.Host`: both roles in one process (assembly `agw-server`). Defaults are SQLite and InProcess execution.

`Execution:Provider` (`InProcess` or `Distributed`) is chosen at startup and must be the same across a deployment. Distributed mode uses PostgreSQL for state and locks, and PostgreSQL or Redis for the event stream. It fails at startup when those are missing.

### Modules and dependencies

- Each business module (`Agw.Agents`, `Agw.Projects`, `Agw.Jobs`, `Agw.Integrations`, `Agw.Providers`, `Agw.Skills`, `Agw.Tools`, `Agw.Auth`, `Agw.Settings`, `Agw.Files`, …) follows `Api → Application → Domain ← Infrastructure` and creates only the layers it needs. Api holds Controllers, Endpoints, DTOs, and Validators. Application holds use cases and orchestration. Domain holds anemic entities, value objects, Behaviors, and DomainServices. Infrastructure holds technical IO.
- Cross-module calls go through an interface in `Agw.<Module>.Contracts`, implemented by a Facade that the owning module registers. Contracts projects contain only interfaces and DTOs. Never reference another module's Application/Domain/Infrastructure namespaces, `*AppService`, `*RuntimeService`, or `I*DbContext`.
- `Agw.Agents.Execution` references `Agw.Agents` one way. Together they form one logical Agents module. Persistence seams and durable manifest data stay in `Agw.Agents`.
- The allowed project-reference graph is fixed by `ProjectReferences_CurrentGraph_MatchesAllowedDependencyMatrix` in `tests/Agw.Architecture.Tests/BackendArchitectureTests.cs`. `Agw.Tools.Abstractions` may depend only on `Microsoft.Extensions.AI.Abstractions`.

### Persistence

- Entity CLR types and EF configurations live centrally in `Agw.Data/Entities`. Each table still has exactly one logical owner module, listed in `EntityOwners` in `BackendArchitectureTests.cs`. Adding a `[Table]` entity requires adding it there.
- Each module declares `Application/Persistence/I<Module>DbContext` (DbSets plus the inherited `SaveChangesAsync`). The single scoped `Agw.Infrastructure/Data/AgwDbContext` implements all nine of them. A module uses only its own interface. Reusable complex persistence and cross-module transactions are declared as interfaces in the module's `Application/Persistence` and implemented under `Agw.Infrastructure/<Module>/`.
- The database has no foreign keys: `NoForeignKeyModelDiffer` strips them from migrations, so never hand-add them. Application and Infrastructure code validates references and cleans up related rows. Columns use `snake_case`.
- `EntityCreatorInterceptor`, `EntityModifierInterceptor`, and `EntitySoftDeleteInterceptor` handle audit stamping (`CreateBy`/`CreateTime`/`UpdateBy`/`UpdateTime`), keep `CreateBy` immutable, and validate ownership.
- Raw persistence access per file is tracked in an exact inventory (`RawPersistenceInventoryTests`, `ModuleSource_RawPersistenceAccessMatchesExactInventory`). Changing that access means updating the inventory to match exactly. Do not grow legacy allowlists.

### User isolation

- Business, configuration, and execution roots are owned by immutable `CreateBy` (or an existing `UserId`). Children inherit ownership from their root. Foreign IDs return the same NotFound/InvalidParam as nonexistent ones.
- A user-scope EF query filter fails closed without a user context. `IgnoreUserScope()` is allowed only in approved Infrastructure paths such as `AgentDeletionCoordinator`, `ProjectDeletionCoordinator`, token validation, and schedulers. Architecture tests enforce the list.
- Read the current user through `UserInfoUtil.RequiredUserId` or an injected `IUserInfoService`. Do not add `userId` parameters just to pass the current user down. Pass an explicit ID only across an auth-context boundary (OAuth state, durable manifest, queued work).

### Domain model

Follow [docs/human/5.domain-architecture.md](docs/human/5.domain-architecture.md). The backend uses an anemic domain model that keeps state and behavior apart:

| Part | Owns |
| --- | --- |
| Aggregate Root / Entity / Value Object | State |
| `Behavior` | Business behavior and state transitions inside one Aggregate Boundary |
| `DomainService` | Domain rules that one Aggregate's state cannot decide |
| `ApplicationService` | Use-case orchestration: load, coordinate, call Domain, persist, publish |
| Infrastructure | Technical IO: database, HTTP, message queues, Redis |

Where logic goes:

- Not a business rule: Application or Infrastructure.
- A business rule that one Aggregate's own state can decide: that Aggregate's Behavior.
- A business rule that needs facts beyond that Aggregate: a DomainService.

Behavior:

- Lives in the owning module at `Domain/Behaviors/<Entity>Behavior.cs`. It is a `sealed class` whose single constructor binds one aggregate root, for example `OrderBehavior(Order order)` with `Submit()` and `Cancel()`.
- Reads and modifies only its root and the root's owned children. It never modifies external entities and never queries other Aggregates to complete a rule.
- Application or DomainService code creates it with `new` for one use case. It is never registered with IoC, cached, serialized, shared across threads, or reused across use cases. Do not add an `I<Entity>Behavior` interface unless two real adapters exist and the decision has been explicitly approved.
- The caller resolves external facts, time, and actor identity and passes them in as values. The caller loads the whole consistency boundary before calling a method that inspects or changes owned children.

DomainService:

- Owns cross-Aggregate rules, rules over a set of Aggregates, uniqueness rules, and rules that need to query another Aggregate. The rule decides this, not the parameter list: `UserDomainService.CreateAsync(userName, email)` is a DomainService because email and user-name uniqueness depend on existing Users.
- May depend on a Domain Repository abstraction such as `IUserRepository.ExistsByEmailAsync` to get the facts it needs. The repository interface belongs to the Domain layer, and Infrastructure implements it. A DomainService contains no database, SQL, HTTP, or Redis code.

ApplicationService loads data, calls Behaviors and DomainServices, saves, and publishes. It does not implement domain rules.

The Domain layer may depend on Repository abstractions. It must not depend on concrete `DbContext`, SQL, `HttpClient`, Redis clients, or MQ clients.

Simple CRUD with no domain rules goes API → Application → Repository. Do not add a Behavior or DomainService just to have one.

### APIs, errors, tools

- Non-WebSocket JSON endpoints return Bens.Results envelopes via `ApiResult.Ok(...)` and the other `ApiResult.*` helpers. Never return bare `Ok()`/`NotFound()`. `[ProducesApiResult]` only adds OpenAPI metadata. WebSockets, OAuth redirects, A2A, and static files keep their own protocol responses.
- Route data goes in query or body parameters. Do not use path parameters.
- Expected failures throw `AgwException(ErrorCodes.X[, message])`. `AgwApiExceptionMiddleware` maps it to an API result, and `AgwA2AJsonRpcProcessor` maps it to A2A errors. `Agw.Shared/Exceptions/ErrorCodes.cs` is the only catalog. New codes have 7 digits: the HTTP status followed by an incrementing 4-digit sequence (e.g. `404_0003`). Never renumber existing codes. Do not throw `ArgumentException`/`InvalidOperationException` and similar for expected failures.
- Every Tool (`IAgwTool`, `IContextualTool`, attributed tools, ToolBlock members) explicitly declares `AgwToolPermission`. General tools live in `Agw.Tools/Impl/{Tools,ContextualTools,ToolBlocks}`. Business tools live in the owning module's `Application/Tools`, with DTOs in `Contracts/Tools`. Skill-owned tools come through `IAgentSkillRegistration.Tools`, not the global catalog.

## Client architecture

- `web/src/app/` holds only routes, layouts, global CSS, and shell composition. Business code lives in `packages/<domain>` and is imported through package entry points. Packages never import `@agw/web`, `web/src`, or Web's `@/` alias.
- Web and Desktop never import, build, or consume each other's code or artifacts. Desktop owns its renderer (`desktop/renderer/`), Electron adaptation (`renderer/src/runtime/`), and bridge contracts (`desktop/src/shared/contracts/`).
- Chat is split into `@agw/chat-core` (message semantics and render models), `@agw/chat-runtime` (SignalR execution, session/activity state, Conversation control), `@agw/chat` (DOM renderer for Web/Desktop), and `@agw/chat-native` (React Native renderer). Mobile may import only `@agw/api`, `@agw/chat-native`, `@agw/projects-core`, and other React Native-safe packages. Its `android/`/`ios/` directories are generated by Expo CNG.
- `@agw/api` provides typed `apiGet/apiPost/apiPut/apiDelete` over generated OpenAPI types and unwraps Bens.Results.
- Lint and format use `oxlint`/`oxfmt` with the shared `.oxlintrc.json`/`.oxfmtrc.json`. Shared dependency versions live in the `catalog` of `pnpm-workspace.yaml`. Packages declare React/Next/`lucide-react`/`sonner`/`next-themes` as `catalog:` peer dependencies. Files use kebab-case names.

## Conventions

- C#: follow `docs/human/2.csharp-coding-rules.md`. Highlights: `DateTimeOffset` and `TimeProvider` only, never `DateTime`. API timestamps are RFC 3339 with `Z` or an offset, and clients localize them. `IHttpClientFactory`, never `new HttpClient()`. Structured log templates with semantic placeholders, never interpolation. No `#region`. No empty `catch`. Existing server code uses explicit constructors, and Behaviors must.
- Do not reformat code you are not changing. CSharpier (`printWidth` 120) and the hooks handle formatting.
- Tests mirror production namespaces, are named `Method_Condition_ExpectedResult`, and use real implementations. The repository has no mocking library. Tests must not reimplement business logic. `tests/TestTimeProvider.cs` is linked into every test project.
- Configuration: `src/server/Agw.Host/appsettings.json` with standard ASP.NET precedence. `AgwDataDir` defaults to `~/agw`, and `AgwLogDir` defaults to `./logs`. Unattended first run uses `Setup__AdminPassword`. Auth and initialization state lives in the global `auth` group of the Settings-owned `setting` table. Secrets come from environment variables or user secrets, never from appsettings or frontend env files.
- Commits use Conventional Commits (`feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `chore:`).
