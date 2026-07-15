# Connection Alias Kebab-Case Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the web Connection Alias defaults and validation match the server's lowercase kebab-case contract.

**Architecture:** Add a small, pure frontend alias helper that owns validation and default generation. Use it from the Integrations page and Connection dialog, while preserving the server as the authoritative validator and keeping existing aliases immutable.

**Tech Stack:** Next.js 16, React 19, TypeScript, Node.js test runner, Tailwind CSS 4, oxlint, oxfmt.

## Global Constraints

- Accept only aliases matching `^[a-z0-9]+(?:-[a-z0-9]+)*$` with a maximum length of 128 characters.
- Generate defaults as `{lowercase-plugin-id}-account`.
- Do not silently rewrite Alias field input.
- Keep tool names in the `{alias}__{operation}` format.
- Do not change the server contract.
- Do not stage, commit, push, or create a PR.

---

### Task 1: Align Connection Alias behavior with the server

**Files:**
- Create: `src/clients/web/src/app/(app)/integrations/connection-alias.test.ts`
- Create: `src/clients/web/src/app/(app)/integrations/connection-alias.ts`
- Modify: `src/clients/web/src/app/(app)/integrations/page.test.ts`
- Modify: `src/clients/web/src/app/(app)/integrations/page.tsx:195`
- Modify: `src/clients/web/src/app/(app)/integrations/components/connection-dialog-layout.test.ts`
- Modify: `src/clients/web/src/app/(app)/integrations/components/connection-dialog.tsx:79-126`

**Interfaces:**
- Produces: `isConnectionAliasValid(alias: string): boolean`.
- Produces: `createDefaultConnectionAlias(pluginId: string): string`.
- Consumes: the server alias contract documented by `IntegrationInputValidator.NormalizeAlias`.

- [ ] **Step 1: Add failing helper and UI wiring tests**

Create `connection-alias.test.ts` with tests that require:

```ts
import assert from "node:assert/strict";
import test from "node:test";

import { createDefaultConnectionAlias, isConnectionAliasValid } from "./connection-alias.ts";

test("isConnectionAliasValid accepts lowercase kebab-case aliases", () => {
  for (const alias of ["github", "github-account", "github-2-work"]) {
    assert.equal(isConnectionAliasValid(alias), true, alias);
  }
});

test("isConnectionAliasValid rejects aliases outside the server contract", () => {
  for (const alias of [
    "",
    "github_account",
    "GitHub-account",
    "github--account",
    "-github",
    "github-",
    " github-account ",
    `a${"b".repeat(128)}`,
  ]) {
    assert.equal(isConnectionAliasValid(alias), false, alias);
  }
});

test("createDefaultConnectionAlias creates a lowercase kebab-case account alias", () => {
  assert.equal(createDefaultConnectionAlias("GitHub"), "github-account");
});
```

Extend `page.test.ts` to require `createDefaultConnectionAlias(selection.plugin.id)` and reject `_account`. Extend `connection-dialog-layout.test.ts` to require the `github-work` placeholder, `aria-invalid`, the lowercase/hyphen guidance, and submit validation through `isConnectionAliasValid(editor.alias)`.

- [ ] **Step 2: Run the focused tests and verify Red**

From `src/clients/web`, run:

```bash
node --test \
  'src/app/(app)/integrations/connection-alias.test.ts' \
  'src/app/(app)/integrations/page.test.ts' \
  'src/app/(app)/integrations/components/connection-dialog-layout.test.ts'
```

Expected: FAIL because `connection-alias.ts` does not exist and the page/dialog still use snake_case without format validation.

- [ ] **Step 3: Add the pure alias helper**

Create `connection-alias.ts`:

```ts
const CONNECTION_ALIAS_REGEX = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const CONNECTION_ALIAS_MAX_LENGTH = 128;

export function isConnectionAliasValid(alias: string): boolean {
  return (
    alias.length > 0 &&
    alias.length <= CONNECTION_ALIAS_MAX_LENGTH &&
    CONNECTION_ALIAS_REGEX.test(alias)
  );
}

export function createDefaultConnectionAlias(pluginId: string): string {
  return `${pluginId.toLowerCase()}-account`;
}
```

- [ ] **Step 4: Use the helper in the page and dialog**

In `page.tsx`, import `createDefaultConnectionAlias` and replace the `_account` default with:

```tsx
alias: createDefaultConnectionAlias(selection.plugin.id),
```

In `connection-dialog.tsx`, import `isConnectionAliasValid`, derive `aliasValid` and `aliasInvalid`, change the placeholder to `github-work`, set `aria-invalid={aliasInvalid}`, show `Use lowercase letters, numbers, and hyphens.` when invalid, and include `!aliasValid` in the submit button's disabled condition. Keep the read-only edit state and `{alias}__operation` preview unchanged.

- [ ] **Step 5: Verify Green and frontend quality checks**

From `src/clients/web`, run:

```bash
node --test \
  'src/app/(app)/integrations/connection-alias.test.ts' \
  'src/app/(app)/integrations/callback-url.test.ts' \
  'src/app/(app)/integrations/form-state.test.ts' \
  'src/app/(app)/integrations/page.test.ts' \
  'src/app/(app)/integrations/components/connection-dialog-layout.test.ts'
pnpm exec oxfmt --check \
  'src/app/(app)/integrations/connection-alias.ts' \
  'src/app/(app)/integrations/connection-alias.test.ts' \
  'src/app/(app)/integrations/page.tsx' \
  'src/app/(app)/integrations/page.test.ts' \
  'src/app/(app)/integrations/components/connection-dialog.tsx' \
  'src/app/(app)/integrations/components/connection-dialog-layout.test.ts'
pnpm lint
pnpm build
```

Expected: tests, formatting, lint, and build exit successfully. Existing non-error lint warnings may remain.

- [ ] **Step 6: Verify diff scope and Git state**

From the worktree root, run:

```bash
git diff --check
git diff --cached --quiet
```

Expected: no whitespace errors and the staging area remains empty.
