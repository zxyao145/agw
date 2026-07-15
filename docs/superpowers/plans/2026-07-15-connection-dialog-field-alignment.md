# Connection Dialog Field Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Top-align the Display name and Alias fields in the Create/Edit connection dialog when the Alias field includes helper text.

**Architecture:** Preserve the existing responsive grid and add cross-axis start alignment to its two field containers. Protect the intent with the repository's existing source-layout test pattern.

**Tech Stack:** Next.js 16, React 19, Tailwind CSS 4, Node.js test runner, oxlint, oxfmt.

## Global Constraints

- Change only the core Display name/Alias row in the connection dialog.
- Keep the mobile single-column layout, field behavior, and existing spacing unchanged.
- Do not add placeholder helper text or restructure the form.
- Do not stage, commit, push, or create a PR.

---

### Task 1: Top-align connection identity fields

**Files:**
- Create: `src/clients/web/src/app/(app)/integrations/components/connection-dialog-layout.test.ts`
- Modify: `src/clients/web/src/app/(app)/integrations/components/connection-dialog.tsx:66`

**Interfaces:**
- Consumes: Tailwind CSS `items-start` utility on the existing responsive grid.
- Produces: A field row whose child field containers retain intrinsic height and align from the top.

- [ ] **Step 1: Add the failing source-layout regression test**

Create `connection-dialog-layout.test.ts`:

```ts
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const CONNECTION_DIALOG_URL = new URL("./connection-dialog.tsx", import.meta.url);

test("Connection dialog top-aligns identity fields with different content heights", async () => {
  const source = await readFile(CONNECTION_DIALOG_URL, "utf8");

  assert.match(
    source,
    /<div className="grid items-start gap-4 sm:grid-cols-2">[\s\S]*connection-display-name[\s\S]*connection-alias/,
  );
});
```

- [ ] **Step 2: Run the focused test and verify Red**

From `src/clients/web`, run:

```bash
node --test 'src/app/(app)/integrations/components/connection-dialog-layout.test.ts'
```

Expected: FAIL because the grid currently lacks `items-start`.

- [ ] **Step 3: Add the minimal alignment class**

Change the identity field grid to:

```tsx
<div className="grid items-start gap-4 sm:grid-cols-2">
```

Do not modify either field container or helper text.

- [ ] **Step 4: Verify Green and changed-file quality checks**

From `src/clients/web`, run:

```bash
node --test 'src/app/(app)/integrations/components/connection-dialog-layout.test.ts'
pnpm exec oxfmt --check \
  'src/app/(app)/integrations/components/connection-dialog.tsx' \
  'src/app/(app)/integrations/components/connection-dialog-layout.test.ts'
pnpm lint
pnpm build
```

Expected: the focused test, formatting check, lint, and build all exit successfully. Existing non-error lint warnings may remain.

- [ ] **Step 5: Verify diff scope and Git state**

From the worktree root, run:

```bash
git diff --check
git diff --cached --quiet
```

Expected: no whitespace errors and the staging area remains empty.
