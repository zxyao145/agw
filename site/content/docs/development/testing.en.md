---
title: "Testing and contribution"
description: "Validate the affected behavior and follow formatting, boundary, and migration rules."
weight: 50
lastmod: 2026-09-25
translationKey: docs/development/testing
---

Prerequisite: dependencies are installed. Read root `AGENTS.md` and the relevant rules under `docs/human/` before changes, and preserve unrelated local work.

## Backend checks

From the repository root:

```bash
dotnet build Agw.slnx
dotnet test Agw.slnx
dotnet csharpier check .
```

Test projects use xUnit v3 and run on Microsoft.Testing.Platform, selected by the root `global.json`. Start with relevant tests when investigating a failure, such as `dotnet test tests/Agw.Files.Tests`. Unit and composition tests use real implementations and pure option helpers, never mocks or fake implementations. Constructing `CodexAIAgent` or `ClaudeCodeAIAgent` probes the CLI, so those tests run as real CLI tests: they are opt-in, require the executable, and stay out of the default suite.

After changing error codes or exception rules, run `dotnet test tests/Agw.Shared.Tests`. After changing module dependencies, run the backend architecture tests with `dotnet test tests/Agw.Architecture.Tests`.

PostgreSQL tests for durable execution (lease protection, event order, active execution upgrades, and scheduler capacity) connect to an isolated test instance through `AGW_TEST_POSTGRES_CONNECTION_STRING`; the test role needs permission to create databases. Redis event-projection tests use `AGW_TEST_REDIS_CONNECTION_STRING`. Without these variables, the corresponding tests are skipped. CI runs the PostgreSQL tests on PostgreSQL 18 and checks the TRX results to confirm every required test ran and passed; see the [Development guide](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md) for the full commands.

For sign-in changes, run `dotnet test tests/Agw.Auth.Tests`. These tests use a controlled provider and per-test SQLite databases by default. To verify against PostgreSQL, set `AGW_TEST_OIDC_POSTGRES` to an isolated test server's admin connection string with a role that can create databases; never point it at a production server. Desktop main-process sign-in and credential-storage tests run with `pnpm --filter @agw/desktop test`.

## Client checks

From `src/clients`:

```bash
pnpm lint
pnpm test
pnpm fmt:check
pnpm build
```

Use oxlint/oxfmt, not ESLint/Prettier. Package-boundary changes must pass `pnpm test:boundaries`. After API changes, export the Development OpenAPI document to `src/clients/packages/api/openapi.json`, run `pnpm gen:api` to regenerate the typed client, and validate callers.

Component rendering tests use the shared `@agw/test-harness` package to set up a DOM environment. When a test needs API responses, its `startApiServer` starts a real local HTTP server so components exercise their own request path. For Web browser tests, run `pnpm --filter @agw/web exec playwright install chromium`, then `pnpm --filter @agw/web test:e2e`. Playwright starts an isolated Web server on `127.0.0.1:3101` and needs no backend.

## Data and commits

Model changes need matching SQLite and PostgreSQL migrations, but generate or apply them only with explicit authorization. Use `src/server/Agw.Migrations.Sqlite` or `src/server/Agw.Migrations.Postgres` as the migrations project and `src/server/Agw.Standalone.Host` as the startup project, ending the command with `-- --provider sqlite` or `-- --provider postgres`; see the Development guide for the full commands. `dotnet tool restore` installs only CSharpier, so install `dotnet ef` separately. `NoForeignKeyModelDiffer` prohibits database foreign keys; Application/Infrastructure own reference validation and cleanup.

Use explicit C# constructors, not primary constructors, and `DateTimeOffset` for dates. Follow root `AGENTS.md`. Commits need explicit authorization and use Conventional Commits.

## Match checks to the change

| Change | Minimum verification |
| --- | --- |
| Backend behavior fix | Build the affected project and verify a reproduction of the original problem |
| API or DTO change | Export the OpenAPI document, regenerate API types, and verify callers and error handling |
| Module or package dependencies | Run backend architecture tests or client boundary checks |
| Error code change | Pass `tests/Agw.Shared.Tests` |
| UI change | Inspect real interactions, screen widths, and relevant tests; run `test:e2e` for Web browser behavior |
| Site documentation | Pass strict Hugo, link, and translation checks and inspect rendered pages |

Keep failure details, fix the cause, and rerun affected checks. Describe the change, validation, and any database or deployment impact when submitting it.

## Completion criteria

Bug fixes need reproducible verification. Behavior changes cover relevant success and failure paths. Changes limited to `site` use its Hugo, link, and browser checks; documentation-only work does not need model services or database initialization.

## Implementation and references

- [Repository rules](https://github.com/zxyao145/agw/blob/main/AGENTS.md)
- [Development](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)
