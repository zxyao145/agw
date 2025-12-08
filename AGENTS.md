# Repository Guidelines

## Project Structure & Module Organization
- Solution root `D-System.slnx` targets `net10.0`.
- Backend lives in `src/backend`:
  - `DSystem.Domain`: entities, enums, repositories, domain services.
  - `DSystem.Infrastructure`: EF Core `LlmDbContext`, repository/unit-of-work implementations, DB settings for SQLite (default), PostgreSQL, MySQL.
  - `DSystem.Api`: ASP.NET Core entry point, DI wiring, controllers (`ProvidersController`, `ModelsController`, etc.), request/response contracts. Swagger in Development at `/openapi`.
- `src/frontend` is empty; add UI work there if introduced.
- Add automated tests under `tests/` (e.g., `DSystem.Tests`) mirroring Domain/Infrastructure/API namespaces.

## Build, Test, and Development Commands
- `dotnet restore D-System.slnx` — restore all projects.
- `dotnet build D-System.slnx` — compile solution.
- `dotnet run --project src/backend/DSystem.Api/DSystem.Api.csproj` — run API with SQLite file DB.
- Database migrations (from `src/backend/DSystem.Api`):
  - `dotnet ef migrations add <Name> -p ../DSystem.Infrastructure -s .`
  - `dotnet ef database update -p ../DSystem.Infrastructure -s .`
- Once tests exist: `dotnet test`.

## Coding Style & Naming Conventions
- C# 10+/ASP.NET Core defaults; nullable reference types on, implicit usings enabled.
- PascalCase for classes/methods/properties; camelCase for locals/parameters; interfaces prefixed with `I`; async methods end with `Async`.
- Keep controllers thin; prefer dependency injection; keep EF Core data access behind repository/unit-of-work abstractions.

## Testing Guidelines
- Use xUnit under `tests/`; follow Arrange/Act/Assert.
- Favor deterministic unit tests for domain services and integration tests for repositories/EF behaviors.
- Target high coverage on business logic and data access boundaries; run `dotnet test` before pushing.

## Commit & Pull Request Guidelines
- Commit messages: concise, imperative English (e.g., `Add provider creation endpoint`); group related changes per commit.
- Pull requests include scope/rationale, linked issue/feature when available, test notes (`dotnet test` or manual steps), and screenshots for API/UX-affecting changes.
- Keep diffs minimal; avoid unrelated formatting; commit generated assets only when needed.

## Security & Configuration Tips
- Configuration in `src/backend/DSystem.Api/appsettings*.json`; override via environment variables for deployment.
- Never commit real API keys or connection strings; use placeholders locally and secrets stores (e.g., environment variables or user secrets) in shared environments.
