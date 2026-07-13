# Repository Guidelines

## Project Structure & Module Organization

Backend projects live in the repository-relative `src/server/` directory: `Agw.Host` is the ASP.NET Core entry point, `Agw.Setup` contains setup Razor/UI services, `Agw.Infrastructure` owns EF Core persistence, and domain modules such as `Agw.Agents`, `Agw.Tasks`, `Agw.Skills`, `Agw.Tools`, and `Agw.Integrations` keep feature code isolated. Web client code is in `src/clients/web`, with app routes under `src/app/(app)`, typed API helpers in `src/api`, and shared UI/utilities in `src/components`, `src/hooks`, and `src/lib`. Tests are in `tests/Agw.*.Tests`; project documentation is under `docs/`. Treat `bin/`, `obj/`, `.next/`, `node_modules/`, and `TestResults/` as generated.

## Build, Test, and Development Commands

Run backend commands from the repo root:

- `dotnet restore Agw.slnx` restores backend dependencies.
- `dotnet build Agw.slnx` compiles all solution projects.
- `dotnet test Agw.slnx` runs the normal xUnit suite.
- `dotnet run --project src/server/Agw.Host` starts the API host, usually on `http://localhost:5015`.
- `dotnet format` applies .NET formatting.

For Web client work, run commands in `src/clients/web`: `pnpm install`, `pnpm dev`, `pnpm build`, `pnpm lint`, and `pnpm format:check`.

## Coding Style & Naming Conventions

Use 4-space indentation for C#. Follow standard .NET naming: `PascalCase` for types and members, `camelCase` for locals and parameters, and `I` prefixes for interfaces. Keep API contracts in module-local `Contracts/` folders and name controllers with the `Controller` suffix. Prefer async methods for I/O and constructor injection for services. Frontend files use TypeScript, React function components, and kebab-case filenames. Do not edit generated artifacts unless the task explicitly requires it.

## Testing Guidelines

Backend tests use xUnit. Mirror production namespaces where practical and prefer method names like `Method_Condition_ExpectedResult`. Run `dotnet test Agw.slnx` before completing backend changes.

## Commit & Pull Request Guidelines

Use Conventional Commits, consistent with recent history: `feat(chat): add share url button`, `fix(a2a): resolve runtime service injection`, or `chore: dotnet format`. Keep PRs focused. Include a summary, linked issue when relevant, testing notes, migration impact, and screenshots for UI changes.

## Security & Configuration Tips

Keep secrets out of `appsettings*.json` and frontend env files; prefer environment-variable overrides. Do not add or apply EF Core migrations automatically. Reuse `AgwException` and centralized `ErrorCodes` for intentional backend errors.
