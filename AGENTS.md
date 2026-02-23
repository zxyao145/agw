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


## Skills
A skill is a set of local instructions to follow that is stored in a `SKILL.md` file. Below is the list of skills that can be used. Each entry includes a name, description, and file path so you can open the source for full instructions when using a specific skill.
### Available skills
- skill-creator: Guide for creating effective skills. This skill should be used when users want to create a new skill (or update an existing skill) that extends Codex's capabilities with specialized knowledge, workflows, or tool integrations. (file: /home/wsl/.codex/skills/.system/skill-creator/SKILL.md)
- skill-installer: Install Codex skills into $CODEX_HOME/skills from a curated list or a GitHub repo path. Use when a user asks to list installable skills, install a curated skill, or install a skill from another repo (including private repos). (file: /home/wsl/.codex/skills/.system/skill-installer/SKILL.md)
### How to use skills
- Discovery: The list above is the skills available in this session (name + description + file path). Skill bodies live on disk at the listed paths.
- Trigger rules: If the user names a skill (with `$SkillName` or plain text) OR the task clearly matches a skill's description shown above, you must use that skill for that turn. Multiple mentions mean use them all. Do not carry skills across turns unless re-mentioned.
- Missing/blocked: If a named skill isn't in the list or the path can't be read, say so briefly and continue with the best fallback.
- How to use a skill (progressive disclosure):
  1) After deciding to use a skill, open its `SKILL.md`. Read only enough to follow the workflow.
  2) When `SKILL.md` references relative paths (e.g., `scripts/foo.py`), resolve them relative to the skill directory listed above first, and only consider other paths if needed.
  3) If `SKILL.md` points to extra folders such as `references/`, load only the specific files needed for the request; don't bulk-load everything.
  4) If `scripts/` exist, prefer running or patching them instead of retyping large code blocks.
  5) If `assets/` or templates exist, reuse them instead of recreating from scratch.
- Coordination and sequencing:
  - If multiple skills apply, choose the minimal set that covers the request and state the order you'll use them.
  - Announce which skill(s) you're using and why (one short line). If you skip an obvious skill, say why.
- Context hygiene:
  - Keep context small: summarize long sections instead of pasting them; only load extra files when needed.
  - Avoid deep reference-chasing: prefer opening only files directly linked from `SKILL.md` unless you're blocked.
  - When variants exist (frameworks, providers, domains), pick only the relevant reference file(s) and note that choice.
- Safety and fallback: If a skill can't be applied cleanly (missing files, unclear instructions), state the issue, pick the next-best approach, and continue.
