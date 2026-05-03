# Backend Rules

## 1. All non-WebSocket JSON API endpoints must use Bens.Results

All non-WebSocket JSON API endpoints in the backend must return responses wrapped in the `Bens.Results` envelope format. Use `Agw.Shared.Results.AgwApiResult` helpers or the configured Bens.Results boundary mapping.

### Applicable modules

- `Agw.Agents`
- `Agw.Providers`
- `Agw.Tasks`
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
