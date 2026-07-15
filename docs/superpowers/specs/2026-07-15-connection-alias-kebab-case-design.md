# Connection Alias Kebab-Case Design

## Problem

The server accepts Connection aliases only when they use lowercase kebab-case, but the web client creates a snake_case default such as `github_account` and shows a snake_case placeholder. The dialog only checks that the Alias field is non-empty, so an invalid value reaches the server and fails during save.

## Design

Keep the server's existing alias contract as the source of truth and mirror it in a small web helper:

- aliases contain lowercase ASCII letters, digits, and single hyphen separators;
- aliases are between 1 and 128 characters;
- the default alias is derived as `{lowercase-plugin-id}-account`;
- the dialog preserves user input instead of silently rewriting it.

The Create connection dialog validates the Alias field before submission. An invalid non-empty alias is marked with `aria-invalid`, its helper text explains that only lowercase letters, numbers, and hyphens are allowed, and the submit button remains disabled. Existing connections keep their immutable, read-only Alias behavior. Tool names continue to use `{alias}__{operation}`.

## Verification

Add focused tests for valid and invalid aliases, default alias generation, page integration, and dialog validation wiring. Run the Integrations frontend tests, changed-file formatting check, web lint, web build, and Git diff checks.

Do not change the server contract, stage, commit, push, or create a PR.
