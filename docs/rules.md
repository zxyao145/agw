# Backend Rules

## 1. All non-WebSocket JSON API endpoints must use Bens.Results

- All non-WebSocket JSON API endpoints in the backend must return responses wrapped in the `Bens.Results` envelope format. Return `Bens.Results.ApiResult` directly or use the configured Bens.Results boundary mapping.
- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep protocol-specific response formats.

### Applicable modules

- `Agw.Host`
- `Agw.Auth`
- `Agw.Setup`
- `Agw.Files`
- `Agw.Agents`
- `Agw.Providers`
- `Agw.Projects`
- `Agw.Jobs`
- `Agw.Integrations`
- `Agw.Skills`
- `Agw.Tools`

### What to do

- Return `ApiResult.Ok()`, `ApiResult.Ok(data)`, `ApiResult.BadRequest(...)`, or another appropriate `ApiResult.*` helper directly.
- Use `ErrorCode.ToApiResult()` or `AgwException.ToApiResult()` when the response must preserve a shared error code and HTTP status.
- Use `[ProducesApiResult]` for OpenAPI response metadata where applicable; it does not replace direct `ApiResult` returns.
- Let `AgwApiExceptionMiddleware` handle `AgwException` mapping automatically.

### What NOT to do

- Do not return raw `Ok(...)`, `BadRequest(...)`, `NotFound(...)`, `NoContent()`, or other bare `IActionResult` responses from controllers in the modules listed above.

### Exceptions

- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep their protocol-specific response formats.

---

## 2. Backend architecture and module layering

Each backend module follows lightweight Clean Architecture layering.

```
Api → Application → Domain ← Infrastructure
```

- The backend MUST remain a modular monolith organized by business module. A module MUST create only the layers its actual complexity needs; empty layers and full Clean Architecture ceremony are not required.
- Persisted data ownership MUST be logical rather than physical. Every entity and table MUST have exactly one owning module, while entity CLR types and EF configurations MAY remain in `Agw.Data` and all module persistence seams MAY share the single `AgwDbContext`, database, and schema. Physical co-location MUST NOT grant cross-module query or write ownership.
- The EF Core model MAY retain navigation and relationship metadata, but SQLite and PostgreSQL migration generation MUST use `NoForeignKeyModelDiffer` and MUST NOT emit database foreign-key constraints or foreign-key migration operations. Hand-authored migrations MUST NOT add foreign keys. Application and Infrastructure flows MUST own referential validation and relation cleanup.

- **Api**: Controllers, DTOs, routing, validation
- **Application**: Use cases, workflows, service coordination, external-fact loading, and Domain rule invocation
- **Domain**: Anemic entities/value objects plus framework-free Behaviors, Policies, and genuine cross-boundary DomainServices
- **Infrastructure**: Repositories, DB access, external APIs

Dependencies must always point **inward**. Domain never depends on anything else.

---

## 3. Anemic domain model

Persisted entities, domain data objects, value objects, and state snapshots MUST remain anemic and contain data only. They MUST NEVER declare business methods, operator overloads, computed domain properties, state transitions, business validation or normalization, derived domain decisions, or domain-event collections.

Behavior that a rich model would place on an entity MUST live in the owner Module at `Domain/Behaviors/<Entity>Behavior.cs`.

### Behavior construction and lifetime

- An entity-bound Behavior MUST be a concrete `<Entity>Behavior` class and MUST be created manually with `new` by Application code.
- A Behavior constructor binds exactly one data root. Root-local preconditions MAY run before owned children are loaded; Application MUST load the complete consistency boundary before invoking any Behavior method that inspects or mutates those children.
- A Behavior MAY mutate the bound root and its owned children. It MUST NOT mutate foreign entities.
- External facts, time, and actor identity MUST be resolved by Application and passed to Behavior methods as values or read-only context.
- A Behavior MUST NEVER be registered with IoC, cached, serialized, shared across threads, or reused across use cases.
- A Behavior MUST NOT define an `I<Entity>Behavior` Interface unless two real Adapters exist and the architecture decision is explicitly approved.

### Behavior dependencies

A Behavior MUST remain framework-free and MUST NOT reference or construct a Policy, DomainService, EF Core, ASP.NET Core, `DbContext`, repositories, `IServiceProvider`, `HttpClient`, files, MAF/MCP, current-user accessors, or Infrastructure Adapters. Audit stamping remains in the EF interceptors.

Application owns authorization, external queries, Behavior construction, use-case ordering, transactions, persistence, and boundary error mapping. If a Behavior returns a transition fact, Application handles it only after persistence succeeds; data objects never hold domain-event collections.

### Policy and DomainService construction

- A pure Domain Policy SHOULD be constructed manually at its call site by default. This is a simplicity default, not a prohibition on IoC when a real composition need exists.
- A genuine DomainService MAY be managed by IoC only when its rule spans multiple data boundaries and does not belong to one root Behavior.
- An IoC-managed DomainService MUST remain stateless and its constructor dependencies MUST be pure Domain components. It MUST NOT depend on persistence, transport, current-user, clock, filesystem, MAF/MCP, Application, or Infrastructure services.
- A DomainService MUST NOT capture a Behavior or data root. Application passes domain data and external facts to its methods, then applies resulting decisions through the relevant Behaviors.

### Policy, Decision, and Behavior orchestration

- Application owns invocation order. It MAY construct and invoke Behavior first for root-local preconditions before performing external queries or Policy evaluation.
- Policy independently evaluates domain data plus external facts and returns a data-only Decision.
- Decision types MUST live in a neutral `Domain/Decisions` namespace and MUST remain anemic. They MUST NOT contain Policy references, mutation behavior, persistence state, or transport concerns.
- Application passes a valid Decision to the root-bound Behavior. Behavior applies that Decision only to its root and owned children.
- Behavior MUST NOT call, receive, inject, or construct a Policy or DomainService. This dependency direction is enforced by Architecture Tests.
- Reusable read-only algorithms that are not themselves domain decisions belong in neutral `Domain/Topology`, `Domain/Rules`, or `Domain/Algorithms` components. Policy, compiler, and runtime may consume them without routing through Behavior.

### EF-tracked consistency boundaries

- Application MUST fully load the root and every owned navigation that Behavior may mutate before constructing or invoking Behavior.
- Behavior MUST reconcile existing owned children in place by their stable keys. It MUST update matching instances, remove missing children, and add only genuinely new children.
- Behavior MUST NOT replace a tracked navigation and then query the old rows. EF relationship fix-up would merge old tracked children into the replacement collection.
- Application MUST NOT delete and re-add separate instances with the same tracked primary key in one unit of work.

Do not create an empty Behavior for simple CRUD, settings, audit rows, or read models. Existing entity-specific `DomainService` classes and existing entity methods are migration debt protected by a decreasing architecture-test allowlist; new single-root DomainServices are forbidden.

### Selective DDD scope

- Agentflow is the current selective-DDD subdomain. Its `Agentflow` root and owned `AgentflowNode`/`AgentflowEdge` graph form the consistency boundary for graph invariants and normalization.
- `AgentflowDefinitionPolicy` evaluates proposed graph data and external Agent/ModelProvider facts, returning `AgentflowDefinitionDecision`; `AgentflowBehavior` applies that Decision without referencing the Policy.
- `AgentflowTopology` owns reusable read-only topology/configuration algorithms used by definition Policy, compiler, and runtime.
- Agentflow update loads root, Nodes, and Edges together before Behavior invocation; Behavior reconciles NodeId/EdgeId collections in place so EF relationship fix-up and identity tracking remain correct.
- Agentflow CRUD, list/read projections, API mapping, compiler/runtime execution, checkpoint persistence, trace recording, and durable orchestration remain Application or Infrastructure concerns.
- Do not convert the whole `Agw.Agents` module, or simple CRUD portions of Auth, Jobs, Providers, Skills, Integrations, and Tools, into DDD aggregates or Behavior classes without a separate domain-complexity decision.

---

## 4. Do not instantiate HttpClient directly

All HTTP client usage must go through `IHttpClientFactory`. Never instantiate `HttpClient` with `new HttpClient()`.

### Applicable modules

- All backend modules under `src/server/`

### What to do

- Resolve `IHttpClientFactory` via `IocUtil.GetSingletonRequiredService<IHttpClientFactory>()`.
- Create short-lived `HttpClient` instances with `httpClientFactory.CreateClient()`.

### What NOT to do

- Do not call `new HttpClient()` directly.
- Do not hold `HttpClient` as a singleton field without using the factory.

### Rationale

Direct `new HttpClient()` causes socket exhaustion and DNS staleness. `IHttpClientFactory` manages connection pooling, DNS refresh, and handler lifecycle automatically.


---

## 5. Do not use `DateTime` in backend code

- Store backend date and time values in one consistent time zone. Prefer UTC; the server's local time zone is also allowed, but a deployment must choose one and use it consistently.
- Do not use `DateTime` in backend code; use `DateTimeOffset`.
- Use `TimeProvider` whenever it is applicable.
- Serialize API date and time values as RFC 3339 strings with a time-zone designator or offset (`Z` or `+/-HH:mm`). Do not return offset-free local date-time strings.
- Do not localize date and time values on the server. Clients are responsible for converting and formatting them according to the user's local time zone and locale.


---

## 6. Backend API Responses and Exceptions

- Do not use path parameters in API routes unless specifically justified; pass identifiers and filters through query parameters or request bodies.
- Expected backend application failures must throw `Agw.Shared.Exceptions.AgwException` with an `ErrorCodes` entry.
- Reuse existing error codes before adding new ones, and never renumber existing codes.
- New `ErrorCode.Code` values contain seven digits: the first three match the HTTP status code and the final four increment within that status group, for example `400_0001`, `404_0003`, or `500_0001`.
- Keep catalog messages stable and reusable. Use `new AgwException(ErrorCodes.SomeCode)` when the catalog message is sufficient; pass an override message when runtime context such as an ID, path, provider, or validation value is required.
- Do not introduce new explicit `throw new ArgumentException`, `InvalidOperationException`, `NotSupportedException`, `HttpRequestException`, or protocol-specific exceptions for expected backend application failures.
- Preserve boundary-specific behavior by translating `AgwException` at the boundary. For example, A2A internals throw `AgwException`, while `AgwA2AJsonRpcProcessor` maps it to A2A JSON-RPC errors.

## 7. Backend Service Registration and Boundaries

- Register new backend services in the relevant module `DependencyInjection.cs` or extension method and ensure `Agw.Host/Program.cs` composes the module.
- `Agw.Integrations` treats `IPluginCatalog` as the source of truth for plugin, connector, authentication, capability-source, and bundled Skill definitions; definitions are code/content assets and are not EF entities. These Available integrations are global read-only catalog data.
- Persist each user's setup in `PluginInstallation`, Agent-selectable accounts or endpoints in `Connection`, and protected or environment-referenced secrets in their dedicated credential entities.
- User-facing surfaces call catalog definitions Available integrations and Connection instances Configured integrations. Developer contracts retain `PluginDefinition`, `PluginInstallation`, `Connection`, and `ConnectionId`.
- `Connection.CreateBy` is the stable owner user ID. Alias values are immutable and unique within `(CreateBy, Alias)`. CRUD, OAuth, credential reads, binding projection, and every Native/MCP invocation scope must reject foreign Connection IDs without disclosing ownership.
- Every persisted business/configuration/execution root is owned by its immutable `CreateBy` (or its existing `UserId` owner). Application list, detail, mutation, binding, and runtime-resolution queries MUST constrain that owner. Child tables such as JobLog, traces, sessions, and conversation history MUST resolve ownership through their root.
- Agent and Project integration bindings reference concrete `ConnectionId` values. Only owner-matched `Ready` Connections may contribute runtime tools or bundled Plugin Skills.
- `PluginInstallation` setup is user-scoped and keyed by `(CreateBy, PluginId)`. A setup change invalidates only that user's affected Connections. The administrator identity `1001` is only the owner of legacy seeded data, not a global setup bypass.
- `UserScopeFilter` is the named Infrastructure query filter for persisted owners and MUST fail closed when no user context is active. HTTP and execution contexts MUST establish a stable user ID; only token validation, seeding, and scheduler scans may use the restricted system scope or narrowly scoped bypasses while retaining soft-delete filtering.
- `ApiToken.CreateBy` is both token owner and Bearer principal user ID; do not add an `OwnerUserId` column. Token names are unique within `(CreateBy, NormalizedName)`.
- `ProjectType` remains unchanged. The protected default projects are `default-built-in` and `a2a`, both `DefaultBuiltIn`; ordinary execution resolves them by `(CreateBy, Name)` and missing projects return NotFound.
- This phase does not add a User table, OIDC provisioning, or a user-ID counter. A future User table uses numeric primary keys beginning at `10010`; claims and owner contracts keep the decimal-string representation.
- Never return or log plaintext installation secrets, access or refresh tokens, API keys, AK/SK values, protected credential payloads, or complete OAuth token responses.
- Plugin MCP sources that inject credentials into HTTP or SSE requests must use HTTPS endpoints. Materialize them with invocation-scoped credentials and dispose the client and transport after the invocation.

## 8. Current User ID Access

- Read the current authenticated user's stable ID from `UserInfoUtil.UserId` / `UserInfoUtil.RequiredUserId`, or from the corresponding properties on an injected `IUserInfoService`.
- Use `RequiredUserId` when authentication is mandatory. Use nullable `UserId` only when an unauthenticated context is explicitly supported.
- Do not add `user` or `userId` parameters merely to pass the current user through Controllers, Application services, runtime composition, credential readers, or tool invokers.
- An explicit user ID is allowed when it is domain data or must cross an authentication-context boundary, such as a protected OAuth state, a persisted execution manifest, a queued background operation, or an administrator acting on a specified owner. Resolve the current user before crossing that boundary and restore `UserInfoUtil.Current` after temporarily switching execution context.
