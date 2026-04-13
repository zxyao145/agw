# Backend Audit Fixes Design

## Context

`backend-low-quality-audit.md` identifies several backend quality issues. The first implementation round will focus on high-confidence fixes that improve safety and maintainability without changing public API contracts or large execution flows.

The current worktree also contains unrelated local changes, so implementation must keep edits tightly scoped and avoid rewriting unrelated files.

## Goals

- Replace unreliable file path traversal checks in `FilesController`.
- Reduce repeated path validation logic in `FilesController`.
- Remove the `Task.Run` wrapper around synchronous file search.
- Replace audit-pointed generic `Exception` throws with clearer exception types or existing domain exceptions.
- Rename `RuntimServiceBase` to `RuntimeServiceBase` and update references.
- Add focused backend tests for the new path security behavior.

## Non-Goals

- Do not split `AgentRuntimeService` or `AgwA2ARequestHandler` in this round.
- Do not enable A2A host wiring.
- Do not add or apply EF Core migrations.
- Do not modify frontend generated OpenAPI files.
- Do not change existing endpoint routes, request shapes, or response shapes except where invalid paths are rejected more reliably.

## Approach

Use a narrow P0-first refactor. Introduce a path security service in the `Agw.Tasks` module and inject it into `FilesController`. The service resolves requested paths against an allowed root and rejects paths outside that root using normalized full paths plus `Path.GetRelativePath`.

The allowed root should default to the application content root so the backend can continue browsing the repository workspace in development. The implementation should expose a small interface so tests can validate the security rule directly without starting ASP.NET Core.

`FilesController` should call a shared helper for required path validation and path normalization. Existing endpoint behavior should remain otherwise unchanged: missing files return `404`, invalid input returns `400`, unauthorized file system access returns `403`, and unexpected file system failures return `500`.

For exception cleanup, only replace the generic exceptions identified by the audit:

- `AgentRuntimeService`: use `InvalidOperationException` when an AI agent cannot be created for execution.
- `AgwA2ARequestHandler`: use `A2AException` with an existing error code when no handler is configured.
- File tools: use `ArgumentException`, `DirectoryNotFoundException`, and `FileNotFoundException`.
- `GitHubTools`: use `InvalidOperationException` for missing OAuth token in the list operation.

## Data Flow

For file endpoints:

1. Controller receives `path`.
2. Controller helper rejects null or whitespace paths.
3. `IPathSecurityService` normalizes the path and checks whether it stays under the allowed root.
4. Controller uses only the resolved full path for file system and git operations.
5. Endpoint-specific existence checks and current response mapping continue as before.

## Security Rules

The path security service must reject:

- Paths that resolve outside the allowed root.
- Relative paths with enough `..` segments to escape the root.
- Absolute paths outside the allowed root.

The path security service must allow:

- The allowed root itself.
- Files and directories under the allowed root.
- Relative child paths under the allowed root.

Comparisons must account for Windows case-insensitive paths and Unix case-sensitive paths.

## Testing

Add xUnit tests in `tests/Agw.Tasks.Tests` for the path security service:

- Allows the root path.
- Allows a child path.
- Allows a relative child path.
- Rejects an absolute sibling outside the root.
- Rejects a relative traversal outside the root.

Run targeted tests first, then run `dotnet test Agw.slnx`.

## Rollout

This change is backend-only and does not require database migration or frontend regeneration. The only behavior change is stricter rejection of unsafe file paths.
