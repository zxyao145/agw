---
title: "Testing and contribution"
description: "Validate the affected behavior and follow formatting, boundary, and migration rules."
weight: 50
lastmod: 2026-09-15
translationKey: docs/development/testing
---

Prerequisite: dependencies are installed. Read root AGENTS.md and `docs/rules.md` before changes, and preserve unrelated local work.

## Backend checks

From the repository root:

```bash
dotnet build Agw.slnx
dotnet test Agw.slnx
dotnet csharpier check .
```

Start with relevant tests when investigating a failure, such as `dotnet test tests/Agw.Files.Tests`. Use fake `AIAgent` instances. Default unit/composition tests must not create Codex/Claude agents that probe CLIs. Real CLI tests require opt-in and executable availability.

## Client checks

From `src/clients`:

```bash
pnpm lint
pnpm test
pnpm fmt:check
pnpm build
```

Use oxlint/oxfmt, not ESLint/Prettier. Package-boundary changes must pass `pnpm test:boundaries`. After API changes, regenerate the typed client and validate callers.

## Data and commits

Model changes need matching SQLite and PostgreSQL migrations, but generate or apply them only with explicit authorization. `NoForeignKeyModelDiffer` prohibits database foreign keys; Application/Infrastructure own reference validation and cleanup.

Use explicit C# constructors, not primary constructors, and `DateTimeOffset` for dates. Keep AGENTS.md and CLAUDE.md identical. Commits need explicit authorization and use Conventional Commits.

## Match checks to the change

| Change | Minimum verification |
| --- | --- |
| Backend behavior fix | Build the affected project and verify a reproduction of the original problem |
| API or DTO change | Regenerate API types and verify callers and error handling |
| Module or package dependencies | Run backend architecture tests or client boundary checks |
| UI change | Inspect real interactions, screen widths, and relevant tests |
| Site documentation | Pass strict Hugo, link, and translation checks and inspect rendered pages |

Keep failure details, fix the cause, and rerun affected checks. Describe the change, validation, and any database or deployment impact when submitting it.

## Completion criteria

Bug fixes need reproducible verification. Behavior changes cover relevant success and failure paths. Changes limited to `site` use its Hugo, link, and browser checks; documentation-only work does not need model services or database initialization.

## Implementation and references

- [Repository rules](https://github.com/zxyao145/agw/blob/main/AGENTS.md)
- [Development](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)
