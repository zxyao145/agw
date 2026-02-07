# Repository Guidelines

## Project Structure & Module Organization
- `src/backend/` contains the .NET 10 solution projects:
  - `DSystem.Host` (startup host),
  - `DSystem.Api` and `DSystem.Manager.Api` (API/controller layers),
  - `DSystem.Domain`, `DSystem.Infrastructure`, `DSystem.A2A`, `DSystem.ExternalAgents`.
- `src/frontend/web/` is the Next.js 16 App Router UI (`src/app`, `src/components`, `src/api`).
- `tests/DSystem.ExternalAgents.Tests/` holds xUnit tests for backend behavior.
- `docs/` contains project documentation; `scripts/` contains API smoke scripts (for example `scripts/test-both-apis.sh`).
- Treat build outputs (`bin/`, `obj/`, `.next/`, `node_modules/`) as generated artifacts.

## Build, Test, and Development Commands
- Backend (run from repo root):
  - `dotnet restore D-System.slnx` - restore NuGet packages.
  - `dotnet build D-System.slnx` - build all backend projects.
  - `dotnet run --project src/backend/DSystem.Host` - start local APIs and host services.
  - `dotnet test D-System.slnx` - run all .NET tests.
  - `dotnet ef migrations add <Name> -p src/backend/DSystem.Infrastructure -s src/backend/DSystem.Host` - add EF Core migration.
- Frontend (run in `src/frontend/web`):
  - `pnpm install`, `pnpm dev`, `pnpm build`
  - `pnpm lint`, `pnpm lint:fix`, `pnpm format`
  - `pnpm gen:openapi` - regenerate `src/api/openapi.d.ts` after backend contract changes.

## Coding Style & Naming Conventions
- C#: 4-space indentation, `PascalCase` for types/members, `camelCase` for locals/parameters, `I` prefix for interfaces.
- Keep API request/response DTOs under each API project’s `Contracts/`; controllers end with `*Controller.cs`.
- Prefer async methods for I/O and constructor injection for services.
- Frontend: TypeScript + React function components; use kebab-case filenames (for example `chat-history-list.tsx`) and `useXxx` hook naming.

## Testing Guidelines
- Framework: xUnit (`Microsoft.NET.Test.Sdk`, `xunit`, `coverlet.collector`).
- Place tests under `tests/DSystem.*.Tests/`, mirroring production namespaces.
- Use method names like `Method_Condition_ExpectedResult`.
- Run one test: `dotnet test --filter "FullyQualifiedName~ClaudeCodeSessionTests.CancelActiveRequest_SetsCancellationToken"`.
- No coverage threshold is enforced in-repo; collect coverage with `dotnet test --collect:"XPlat Code Coverage"` when needed.

## Commit & Pull Request Guidelines
- Recent history follows Conventional Commits, often with scope and issue refs:
  - `feat(claude-code): ...`, `fix: ...`, `refactor: ...`, `chore: ...`, `docs: ...`.
- Keep PRs focused to one change set.
- Include: short summary, linked issue, testing notes (commands run), and migration impact if schema changed.
- For UI changes, include screenshots; for API changes, include example request/response or endpoint notes.
- Before review, ensure backend build/tests and frontend lint/build pass.

## Security & Configuration Tips
- Do not commit secrets to `appsettings*.json` or frontend env files.
- Configure database/provider in `src/backend/DSystem.Host/appsettings.json` (`Database.Provider`, `Database.ConnectionString`) and override sensitive values via environment variables.
