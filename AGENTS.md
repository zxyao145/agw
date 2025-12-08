# Repository Guidelines

## Project Structure & Module Organization
- Backend lives in `src/backend`: `DSystem.Domain` (entities, enums, domain services), `DSystem.Infrastructure` (EF Core DbContext, repositories, database config), `DSystem.Manager.Api` (HTTP controllers and request/response contracts), `DSystem.Api` (shared API scaffolding), and `DSystem.Host` (ASP.NET Core host wiring everything together). `src/frontend` is currently empty and can be populated later.
- Configuration sits in `src/backend/DSystem.Host/appsettings*.json`; database settings are under the `Database` section.
- Migrations and runtime data are generated relative to the host project; keep repository code free of environment-specific paths.

## Build, Test, and Development Commands
- Restore dependencies: `dotnet restore D-System.slnx`.
- Build all projects: `dotnet build D-System.slnx` (targets `net10.0` with nullable reference types enabled).
- Run the API locally: `dotnet run --project src/backend/DSystem.Host` (OpenAPI available at `/openapi` in Development).
- Add/update EF Core migrations: `dotnet ef migrations add <Name> -p src/backend/DSystem.Infrastructure -s src/backend/DSystem.Host`; update database with `dotnet ef database update -p src/backend/DSystem.Infrastructure -s src/backend/DSystem.Host`.

## Coding Style & Naming Conventions
- Use 4-space indentation and follow C# conventions: `PascalCase` for types/fields on DTOs, `camelCase` for locals/parameters, `I` prefix for interfaces, and `*Controller.cs` for MVC controllers. Keep DTOs under `Contracts` and domain types under `Entities`/`Services`.
- Keep methods async when doing I/O; avoid synchronous EF Core calls. Favor constructor injection for dependencies.
- Run `dotnet format` before submitting to keep styling consistent.

## Testing Guidelines
- Preferred command: `dotnet test D-System.slnx` (no tests yet—add new suites under a `tests` folder mirroring namespaces, e.g., `tests/DSystem.Domain.Tests/AgentRuntimeServiceTests.cs`).
- Use xUnit with clear Arrange/Act/Assert sections. Name test methods with `Method_Condition_ExpectedResult`.
- For data access, use SQLite in-memory or a disposable file with migrations applied to keep runs deterministic.

## Database & Configuration Tips
- Defaults use SQLite (`Database:Provider=sqlite`, file `llmmanager.db`). Switch to PostgreSQL by setting `Database:Provider=postgres` and providing a full `ConnectionString`.
- Keep sensitive connection strings in environment variables; avoid committing secrets to `appsettings*.json`.
- When changing the model, regenerate migrations and ensure `LlmDbContext` configuration stays aligned with entity constraints (max lengths, composite keys, cascade rules).

## Commit & Pull Request Guidelines
- Follow Conventional Commit prefixes (`feat`, `fix`, `chore`, `refactor`, `docs`, `test`), matching existing history like `feat: add Agent entity`.
- One feature per PR; include a short summary, linked issue, and testing notes (commands run, migration impact). Add screenshots or curl examples for API changes when helpful.
- Ensure PRs build and `dotnet test` passes before requesting review; highlight breaking changes or migration requirements explicitly.
