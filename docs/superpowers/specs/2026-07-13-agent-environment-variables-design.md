# Agent Environment Variables Design

## Goal

Allow every Agent definition to own environment variables and manage them from a dedicated tab in the full-screen Create/Edit Agent dialog.

## Decisions

- Add a first-class `EnvironmentVariables` dictionary to `Agent`; do not embed it in `Extra`.
- Persist the dictionary as JSON in a new non-null `agent.environment_variables` column, defaulting existing rows to `{}`.
- Expose the dictionary on Agent create, update, and response contracts.
- Store and return values as ordinary strings, matching the existing MCP Tool Server behavior. Values are not masked or encrypted.
- Make the tab editable for both System and External agents.
- Never mutate the Agw host process environment.
- Merge configuration from least to most specific: existing process/component configuration, Agent definition, then execution-session variables.
- Pass the effective variables to Claude Code/Codex child processes and to stdio MCP servers launched for System agents.
- Do not make in-process registered tools read the variables through `Environment.GetEnvironmentVariable`.

## UI

The right side of the Agent dialog gains an `Environment Variables` tab. It contains a Key/Value list with Add and Remove actions. Empty values are valid. Keys must be non-blank, unique after trimming, and must not contain `=` or a null character. Validation is shown in the tab and blocks Create/Update.

Create starts with an empty list. Edit converts the response dictionary into rows and sends the normalized dictionary back on update. Successful mutations clear the corresponding dialog state.

## Runtime

Agent definition variables are defaults. The existing environment variables sent by Chat/Execution override matching Agent keys for that execution. For stdio MCP servers, the existing server variables are the component baseline, followed by the effective Agent/execution variables. External Agent SDK option variables are likewise the baseline.

HTTP/SSE MCP servers, model clients, Apps, and in-process registered tools do not receive process environment variables.

## Verification

- Backend domain and contract tests cover create/update normalization and response mapping.
- Runtime tests cover definition/session precedence and External Agent option injection.
- MCP tests cover server/Agent/session precedence without changing the host process environment.
- Frontend unit tests cover row normalization, invalid/duplicate keys, empty values, request payloads, and the sixth tab.
- Regenerate and verify the OpenAPI snapshot and TypeScript types.
- Run focused backend/frontend tests, lint/format checks, and browser-test Create/Edit persistence.

