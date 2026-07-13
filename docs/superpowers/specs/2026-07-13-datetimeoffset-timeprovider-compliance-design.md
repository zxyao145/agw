# DateTimeOffset and TimeProvider Compliance Design

## Goal

Bring all production code under `src/server/` into compliance with the repository rules that prohibit `DateTime` and require `TimeProvider` wherever it is applicable.

## Scope

- Replace `DateTime` and nullable `DateTime` values with `DateTimeOffset` equivalents in persistence entities, shared contracts, API request/response types, query parameters, application snapshots, and implementation code.
- Update tests that must change because production types or constructors change.
- Replace direct reads from `DateTime.UtcNow` and `DateTimeOffset.UtcNow` with `TimeProvider.GetUtcNow()` or `TimeProvider.System.GetUtcNow()`.
- Use TimeProvider-aware delay APIs for production scheduling and retry loops where the provider can be propagated without introducing an unrelated abstraction.
- Regenerate the frontend OpenAPI TypeScript declarations after backend contract changes.

Historical EF Core migrations and generated build artifacts are excluded. A new EF Core migration may be required, but this work will neither create nor apply one automatically.

## Approach

Use a layered TimeProvider strategy:

1. Register `TimeProvider.System` once in the host composition root.
2. Inject `TimeProvider` through explicit constructors into DI-managed classes where time affects persisted state, authorization, scheduling, retry behavior, cache behavior, or testable business decisions.
3. Use `TimeProvider.System` directly in leaf adapters and sample utilities when propagating a constructor dependency would only expand factories or third-party boundaries without improving a meaningful test seam.
4. Pass one captured `DateTimeOffset` value through an operation when several fields must share the same timestamp.

No custom clock interface will be introduced because `TimeProvider` already provides the required abstraction.

## Data and API Changes

- `BaseEntity.CreateTime` becomes `DateTimeOffset`; `UpdateTime` becomes nullable `DateTimeOffset`.
- Standalone persisted timestamps such as task record creation/update and agentflow trace start time become `DateTimeOffset`.
- Corresponding shared DTOs, projections, request records, response records, mapper inputs, filters, and controller query values use the same types end to end.
- Date arithmetic and comparisons remain in UTC using `TimeProvider.GetUtcNow()` and `DateTimeOffset` values.
- Tool metadata no longer advertises `DateTime` as a supported server-side type; `DateTimeOffset` remains supported.

These contract changes may alter the OpenAPI schema from `DateTime`-backed values to `DateTimeOffset`-backed values even though both normally serialize as ISO 8601 strings.

## TimeProvider Integration

The provider will be injected into business and infrastructure services that currently read the clock, including domain services, app services, repositories, controllers, A2A services, initialization state, and the job scheduler. The job scheduler and retry loops will use provider-aware delays so tests can control both timestamps and elapsed time.

Leaf code such as sample tools or adapter fallback timestamps may use `TimeProvider.System.GetUtcNow()` directly. This still removes direct framework clock access while avoiding constructor changes that provide no useful control point.

## Compatibility and Persistence

- Existing persisted values are assumed to represent UTC because current code writes `DateTime.UtcNow`.
- Historical migrations remain unchanged to preserve migration history.
- The implementation will report the affected EF model and the need for a follow-up migration; it will not generate or apply one.
- JSON field names and endpoint shapes remain unchanged apart from the underlying OpenAPI timestamp type.

## Testing and Verification

- Update focused domain, controller, repository, A2A, task, setup, and job tests affected by type and constructor changes.
- Use a controllable `TimeProvider` in tests where assertions depend on the current time or delay behavior; otherwise pass `TimeProvider.System` explicitly.
- Run tests for directly affected projects, including `tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj` because it is outside `Agw.slnx`.
- Run `dotnet test Agw.slnx` and `dotnet build Agw.slnx`.
- Regenerate and verify `src/clients/web/src/api/openapi.d.ts`.
- Re-scan non-generated server C# files and require no remaining `DateTime` identifiers or direct `DateTimeOffset.UtcNow`/`DateTime.UtcNow` calls. Any intentional exception must be documented explicitly.

## Non-goals

- No unrelated refactoring or cleanup.
- No custom date/time abstraction beyond `TimeProvider`.
- No automatic EF Core migration creation or database update.
- No change to historical migration source files.
