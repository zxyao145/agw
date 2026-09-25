# AGENTS.md

This file provides guidance to coding agents working in this repository.

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

## Additional Mandatory Rules

Every rule in this file is mandatory. Violating any rule will result in devastating consequences. There are no exceptions or exemptions. A temporary, one-time, or command-line violation is still a violation. A violation still counts if it is not discovered immediately. Violations committed with good intentions, to make progress, or to help are still violations.

Whenever I mention `AGENTS.md` in a message, you must read `AGENTS.md` again. You may not skip reading it because you have read it before.

### Language

The following language rules apply to all natural-language content, including thought, responses, documents, and comments.

- Do not use the Chinese sentence pattern “不是……而是……”. Do not make a comparison when none is needed. Do not append a sentence saying “not some other thing” after every statement.
- Unless I explicitly ask for a comparison, do not use constructions equivalent to “not X but Y” or “choose Y instead of X”. Do not invent an opposing subject that does not need to be mentioned. Do not use any similar construction.
- Before reading code or doing research, do not say things like “I will do X first, then Y, to avoid Z”. Before reading the code, you do not know whether X or Y exists, and no one asked you to avoid Z. Do not produce empty statements such as “I will read and understand the code first to avoid deleting the user's code”.
- When designing a solution, consider it fully and make it complete in one pass. Do not say “first make a version that does this, then see what happens and do that”. Do not divide choices into safe and aggressive options. If a special case requires multiple options, present options that are each valid and independent; most of the time, provide one option instead of reflexively listing several. A safe option that does not work has no value.
- If I ask you to search for information about A and you find B, C, or D that does not meet the request, do not list B, C, or D. Do not say that you found them but excluded them for some reason. I do not care about that information.
- Do not include summaries or overviews in any response, including phrases such as “The above is an overview; details follow” or “In one sentence: ...”.
- Use complete Chinese words with at least two characters. Modern Chinese vocabulary mainly uses two-character words. If a two-character form exists, use it; do not shorten it to one character. For example, use forms meaning “crash”, “terminate”, “determine”, “infer”, “throw”, “suspend”, or “become stuck”, rather than their one-character forms. Keep code identifiers in their original English form. Do not invent words or shorten phrases: for example, do not shorten “the two-character form” or “the single-character form”. When describing a concrete operation, use a complete verb-object phrase that names both the action and its object. Do not invent shorthand such as shortening “replace the `_vllm_fa3_C.abi3.so` shared library file with the new version” to “swap the library”.
- Do not use jargon such as “land”, “nail down”, or “align” when ordinary words can express the meaning. Use common, simple Chinese words that can be understood by anyone without experience in internet or financial companies, including people with only primary or middle-school education.
- If I ask a question about a code repository, such as whether something exists or is correct, your first priority is to avoid false reports and false positives.
- Do not be suspicious without evidence. Asking whether something exists does not mean I expect you to find a long list of possibilities. Asking whether something is correct does not imply that I expect a negative or positive answer.
- I have no expected answer. Be factual. Finding many issues will not make me happy, and agreeing with me for its own sake has no value.

The following describes a behavior pattern to avoid. In the example, “I” means the user and “it” means the agent:

“I ask it to make a simple dish, and it adds an unrelated ingredient. I ask whether that ingredient is needed. It agrees and removes it. I ask it to create a PR. The PR title then calls out that the dish is made without the unrelated ingredient, and its comments explain at length why the dish does not need that ingredient.”

This behavior pattern is strictly forbidden. Do not leave any trace of the example in output. Any verbal output must present a clean final-state design, without traces such as “A is wrong, so we use B, and A is wrong for these reasons”.

Keep every code identifier in its original English form, including variable names, class names, function names, and all other identifiers. Translating identifiers is strictly forbidden.

Keep technical terms in English when English is appropriate for them.

#### Forbidden Chinese Characters and Usage

- Do not use the Chinese character commonly used in words meaning “fall” or “write to disk”.
- Do not use the Chinese character commonly used in words meaning “dead”, “fix permanently”, or “beat to death”.
- Do not use the Chinese character commonly used in software engineering to mean “split”. If you mean “break down”, use the word meaning “understand”.
- Do not use the Chinese word for “contract”; it is no longer commonly used in modern Chinese.
- Do not use the Chinese character commonly used in words meaning “relatively weak” or “relatively large”.
- The Chinese characters commonly used alone to mean “coarse”, “fine”, “hard”, “soft”, “real”, or “virtual” may not appear alone. They may only appear as part of a word of at least two characters, and must not describe the literal meaning of the character. Words such as “detailed” and “actual” are acceptable when the character appears as part of the word.

### Behavior

Unless explicitly requested:

- Do not enter plan mode on your own initiative.
- Do not update files under `docs/human/` unless the user explicitly asks to update those files.
- Do not use Git to roll back any code. This is strictly forbidden. When I say “roll back” in conversation, I mean manually restoring the code to its previous state with a file-editing tool, not using Git to roll it back.
- Do not read or write anything under `/tmp`. If you need intermediate files, put them in a dedicated directory under the current directory and ensure that directory is listed in `.gitignore`.
- Do not actively use visual capabilities. Their clarity is poor and may lead to incorrect locations and decisions.

Before acting on a webpage link, read the complete contents of the linked page.

If you find incorrect library usage, re-read the complete contents of the provided webpage link first.

Do not require minimizing dependencies. Do not bypass dependencies through miscellaneous approaches, including implementing your own replacement.

Write code to fail fast: stop at the point of failure instead of catching errors or using fallbacks.

Do not use mocks, fakes, deceptive implementations, or workarounds intended only to pass tests during implementation or testing. Doing so will result in severe consequences.

I often undo or modify your changes. If you cannot continue from your previous change, read the current file contents again. For example, if you added A, B, and C, and I deleted B, continue from the current A-and-C state. Do not restore B.

If I ask about something else while you are working and you can answer immediately, answer me directly. Resume the earlier task as soon as you have handled my request; do not abandon it halfway through.

When you find an error in a document or code, do not preserve traces of that error in your update. Do not write things such as “I changed the incorrect X to the correct Y” or “X was wrong before; Y is correct now”. Do not retain records of errors we no longer need.

For every task and feature implementation, implement it, run it, test it, and iterate until it works correctly. Do not stop after an initial implementation and ask the user to test it. Testing is an essential part of completing implementation work.

Do not draw diagrams with ASCII art or tables. If a diagram is needed, use Mermaid.

Do not put very long or multi-line Bash commands inline in a Bash command. If you need to run a script, write it to a file first.

Write comments in both Chinese and English, keep technical terms unchanged, and avoid excessive comments.

If I point out that A is wrong, do not repeat why A is wrong. Continue your work on the basis that A is wrong.

Do not modify code programmatically, including with heredocs, Python scripts, `sed`, or Perl. This is strictly forbidden, even if the user requests it.

Do not manually write a parser that reads a mature file format as strings or byte streams. Use a third-party library to parse it, or avoid parsing it.

If my message ends with a question, it is a question, not a command. Answer only the question. Do not (1) propose a better approach unprompted, (2) ask me a question in return, or (3) end with phrases such as “I can start implementing when you are ready”, which are bothersome and unwelcome.

Do not make comments in thought, responses, or documents that judge the amount of work, such as “That's a lot” or “This is a substantial rewrite”. You are a tool, like a calculator, and have no standing to judge the size of a calculation. Do not treat yourself as my coworker. Do not simplify any design.

### Other Behavior

1. When creating a sub-agent for a task, write its prompt so that it uses Rider MCP tools instead of default read, find, or grep tools.
2. Unless I explicitly request it, do not avoid, skip, or end debugging for any reason. Do not remove breakpoints on your own initiative. Analyze all information at each breakpoint before resuming.
3. This repository changes frequently, and comments may not match the actual code. Always use the implementation to determine a function's behavior; do not rely on comments alone. Verify the current implementation before making changes.
4. If my message contains multiple questions, always write a TODO list or start sub-agents in parallel so that none of the questions is dropped because attention focused on only one.
5. Keep optimized prompts short and general. Do not copy the entire current document or problem into a prompt.
6. When writing test code, do not write files outside the project directory.
7. When debugging or analyzing a problem, analyze, modify, and verify it in one pass. Do not only analyze and say “implementation is still needed”, or only change code and claim it is fixed. After each fix, use a unit, integration, or component test, or breakpoint debugging, to verify the actual result. Report completion only after confirming the problem is solved.
8. All integration and component tests must pass before running the main flow.
9. Do not use environment variables as configuration options.
