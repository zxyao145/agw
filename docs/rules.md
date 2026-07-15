# Backend Rules

## 1. All non-WebSocket JSON API endpoints must use Bens.Results

- All non-WebSocket JSON API endpoints in the backend must return responses wrapped in the `Bens.Results` envelope format. Use `Agw.Shared.Results.AgwApiResult` helpers or the configured Bens.Results boundary mapping.
- WebSocket handlers, OAuth redirect callbacks, A2A protocol endpoints, and static file endpoints may keep protocol-specific response formats.

### Applicable modules

- `Agw.Agents`
- `Agw.Providers`
- `Agw.Projects`
- `Agw.Jobs`
- `Agw.Integrations`
- `Agw.Skills`
- `Agw.Tools`

### What to do

- Return `AgwApiResult.Ok()`, `AgwApiResult.Ok<T>(data)`, `AgwApiResult.BadRequest(...)`, or the corresponding `ApiResult.*` helpers.
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

## 7. Backend HTTP Clients

- Do not instantiate `HttpClient` directly in backend code.
- Use `IHttpClientFactory`, typically through constructor injection or `IocUtil.GetSingletonRequiredService<IHttpClientFactory>()` where that repository pattern already applies.
- Create short-lived clients with `httpClientFactory.CreateClient()`.
- Do not hold a directly constructed `HttpClient` as a singleton field.

## 8. Backend Service Registration and Boundaries

- Register new backend services in the relevant module `DependencyInjection.cs` or extension method and ensure `Agw.Host/Program.cs` composes the module.
- `Agw.Integrations` treats `IPluginCatalog` as the source of truth for plugin, connector, authentication, capability-source, and bundled Skill definitions; definitions are code/content assets and are not EF entities.
- Persist platform-level configuration in `PluginInstallation`, Agent-selectable accounts or endpoints in `Connection`, and protected or environment-referenced secrets in their dedicated credential entities.
- Agent and Project integration bindings must reference concrete `ConnectionId` values. Only `Ready` Connections may contribute runtime tools or bundled Plugin Skills.
- Never return or log plaintext installation secrets, access or refresh tokens, API keys, AK/SK values, protected credential payloads, or complete OAuth token responses.
- Plugin MCP sources that inject credentials into HTTP or SSE requests must use HTTPS endpoints. Materialize them with invocation-scoped credentials and dispose the client and transport after the invocation.
