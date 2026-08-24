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

## 2. Module internal layering

Each backend module follows lightweight Clean Architecture layering.

```
Api → Application → Domain ← Infrastructure
```

- **Api**: Controllers, DTOs, routing, validation
- **Application**: Use cases, workflows, service coordination
- **Domain**: Entities, value objects, business rules (pure, framework-free)
- **Infrastructure**: Repositories, DB access, external APIs

Dependencies must always point **inward**. Domain never depends on anything else.

---

## 3. Anemic domain model

Domain objects (entities, value objects) contain **only data** — no business behavior. All business behavior lives in **Application-layer services** (AppService / DomainService).

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
- `Agw.Integrations` treats `IPluginCatalog` as the source of truth for plugin, connector, authentication, capability-source, and bundled Skill definitions; definitions are code/content assets and are not EF entities.
- Persist platform-level configuration in `PluginInstallation`, Agent-selectable accounts or endpoints in `Connection`, and protected or environment-referenced secrets in their dedicated credential entities.
- User-facing surfaces call catalog definitions Available integrations and Connection instances Configured integrations. Developer contracts retain `PluginDefinition`, `PluginInstallation`, `Connection`, and `ConnectionId`.
- `Connection.CreateBy` is the stable owner user ID. Alias values are immutable and unique within `(CreateBy, Alias)`. CRUD, OAuth, credential reads, binding projection, and every Native/MCP invocation scope must reject foreign Connection IDs without disclosing ownership.
- Agent and Project integration bindings reference concrete `ConnectionId` values and form per-user overlays on shared definitions. Updating one user's overlay must preserve other users' relations. Only owner-matched `Ready` Connections may contribute runtime tools or bundled Plugin Skills.
- Only the stable administrator user ID `1001` may mutate platform-wide `PluginInstallation` setup. A setup change still invalidates affected Connections across all owners.
- Never return or log plaintext installation secrets, access or refresh tokens, API keys, AK/SK values, protected credential payloads, or complete OAuth token responses.
- Plugin MCP sources that inject credentials into HTTP or SSE requests must use HTTPS endpoints. Materialize them with invocation-scoped credentials and dispose the client and transport after the invocation.

## 8. Current User ID Access

- Read the current authenticated user's stable ID from `UserInfoUtil.UserId` / `UserInfoUtil.RequiredUserId`, or from the corresponding properties on an injected `IUserInfoService`.
- Use `RequiredUserId` when authentication is mandatory. Use nullable `UserId` only when an unauthenticated context is explicitly supported.
- Do not add `user` or `userId` parameters merely to pass the current user through Controllers, Application services, runtime composition, credential readers, or tool invokers.
- An explicit user ID is allowed when it is domain data or must cross an authentication-context boundary, such as a protected OAuth state, a persisted execution manifest, a queued background operation, or an administrator acting on a specified owner. Resolve the current user before crossing that boundary and restore `UserInfoUtil.Current` after temporarily switching execution context.
