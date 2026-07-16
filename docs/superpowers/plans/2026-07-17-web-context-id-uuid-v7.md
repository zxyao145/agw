# Web Identifier UUID v7 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate new Web chat Context IDs and user message IDs as UUID version 7 values through the `uuid` package.

**Architecture:** Use one focused Web UUID utility that wraps `uuid.v7()` for both new Context IDs and new user message IDs. Remove `id128` after replacing its last production call; leave Mobile, backend, and Mermaid render IDs unchanged.

**Tech Stack:** Next.js 16, React 19, TypeScript 5.9, pnpm 11, Node.js test runner, `uuid` 11.1.

## Global Constraints

- Modify only `src/clients/web/` plus this design and plan documentation.
- Add `uuid@^11.1.1` as a direct Web dependency.
- Remove `id128`, `Uuid4`, and `Ulid` from Web production source and dependency metadata.
- Leave Mermaid's transient `crypto.randomUUID()` render ID unchanged.
- Do not modify Mobile, backend, generated OpenAPI artifacts, or native output.
- Do not stage or commit changes without explicit user authorization.

---

### Task 1: Add a tested UUID v7 generator

**Files:**
- Create: `src/clients/web/src/lib/uuid.ts`
- Create: `src/clients/web/src/lib/uuid.test.ts`

**Interfaces:**
- Consumes: `v7(): string`, `validate(value: string): boolean`, and `version(value: string): number` from `uuid`.
- Produces: `createUuidV7(): string` from `@/lib/uuid`.

- [x] **Step 1: Install the direct UUID dependency**

Run from `src/clients/web/`:

```bash
pnpm add 'uuid@^11.1.1'
```

Expected: `package.json` contains direct dependency `"uuid": "^11.1.1"` and `pnpm-lock.yaml` is updated. This dependency-only setup does not add production behavior.

- [x] **Step 2: Write the failing generator test**

```typescript
import assert from "node:assert/strict";
import test from "node:test";
import { validate, version } from "uuid";

test("createUuidV7 returns a valid UUID version 7", async () => {
  const uuidModule = await import("./uuid.ts").catch(() => null);

  assert.ok(uuidModule, "UUID v7 generator should exist");
  const value = uuidModule.createUuidV7();

  assert.equal(validate(value), true);
  assert.equal(version(value), 7);
});
```

- [x] **Step 3: Run the focused test and verify RED**

Run from `src/clients/web/`:

```bash
node --test src/lib/uuid.test.ts
```

Expected: FAIL with `UUID v7 generator should exist` because `src/lib/uuid.ts` does not exist.

- [x] **Step 4: Implement the minimal generator**

```typescript
import { v7 as uuidv7 } from "uuid";

export function createUuidV7(): string {
  return uuidv7();
}
```

- [x] **Step 5: Run the focused test and verify GREEN**

Run from `src/clients/web/`:

```bash
node --test src/lib/uuid.test.ts
```

Expected: PASS with one passing test.

### Task 2: Use UUID v7 for new Web Context IDs

**Files:**
- Modify: `src/clients/web/src/components/message/chat.tsx`
- Modify: `src/clients/web/src/components/message/chat-unification.test.ts`

**Interfaces:**
- Consumes: `createUuidV7(): string` from `@/lib/uuid`.
- Produces: New Chat Context IDs in canonical UUID v7 string form.

- [x] **Step 1: Write the failing Chat integration test**

Add this test to `src/components/message/chat-unification.test.ts`:

```typescript
test("shared Chat generates new Context IDs with UUID v7", async () => {
  const source = await readFile(CHAT_URL, "utf8");

  assert.match(source, /import \{ createUuidV7 \} from "@\/lib\/uuid"/);
  assert.match(source, /const nextId = contextId \?\? createUuidV7\(\)/);
  assert.doesNotMatch(source, /Uuid4|id128/);
});
```

- [x] **Step 2: Run the focused Chat test and verify RED**

Run from `src/clients/web/`:

```bash
node --test src/components/message/chat-unification.test.ts
```

Expected: FAIL because `chat.tsx` still imports `Uuid4` from `id128` and calls `nextContextId()`.

- [x] **Step 3: Replace the Chat Context ID generator**

In `src/components/message/chat.tsx`, remove:

```typescript
import { Uuid4 } from "id128";
```

Add:

```typescript
import { createUuidV7 } from "@/lib/uuid";
```

Remove the local `nextContextId()` wrapper and replace:

```typescript
const nextId = contextId ?? nextContextId();
```

with:

```typescript
const nextId = contextId ?? createUuidV7();
```

- [x] **Step 4: Run both focused tests and verify GREEN**

Run from `src/clients/web/`:

```bash
node --test src/lib/uuid.test.ts src/components/message/chat-unification.test.ts
```

Expected: PASS with all focused tests passing.

### Task 3: Use UUID v7 for new Web user message IDs

**Files:**
- Modify: `src/clients/web/src/lib/execution-stream.ts`
- Modify: `src/clients/web/src/lib/execution-stream.test.ts`
- Modify: `src/clients/web/package.json`
- Modify: `src/clients/web/pnpm-lock.yaml`

**Interfaces:**
- Consumes: `createUuidV7(): string` from `@/lib/uuid`.
- Produces: New Web user messages whose `messageId` is a canonical UUID v7 string.

- [x] **Step 1: Write the failing execution-stream integration test**

Add this test to `src/lib/execution-stream.test.ts`:

```typescript
test("new user messages use the UUID v7 generator", async () => {
  const source = await readFile(EXECUTION_STREAM_URL, "utf8");

  assert.match(source, /import \{ createUuidV7 \} from "@\/lib\/uuid"/);
  assert.match(source, /messageId: createUuidV7\(\)/);
  assert.doesNotMatch(source, /id128|Ulid/);

  const { createUserTextMessage } = await loadExecutionStream();
  assert.equal(createUserTextMessage("hello").messageId, "generated-user-id");
});
```

- [x] **Step 2: Run the focused execution-stream test and verify RED**

Run from `src/clients/web/`:

```bash
node --test src/lib/execution-stream.test.ts
```

Expected: FAIL because `execution-stream.ts` still imports `Ulid` from `id128` and calls `Ulid.generate()`.

- [x] **Step 3: Replace the user message ID generator and update the test harness**

In `src/lib/execution-stream.ts`, replace:

```typescript
import { Ulid } from "id128";
```

with:

```typescript
import { createUuidV7 } from "@/lib/uuid";
```

Replace:

```typescript
messageId: Ulid.generate().toCanonical(),
```

with:

```typescript
messageId: createUuidV7(),
```

In `src/lib/execution-stream.test.ts`, replace the old `Ulid` import substitution with:

```typescript
source = source.replace(
  'import { createUuidV7 } from "@/lib/uuid";',
  'const createUuidV7 = () => "generated-user-id";',
);
```

- [x] **Step 4: Remove the obsolete dependency**

Run from `src/clients/web/`:

```bash
pnpm remove id128
```

Expected: `id128` is absent from `package.json` and its unused lockfile package entry is removed.

- [x] **Step 5: Run all focused UUID tests and verify GREEN**

Run from `src/clients/web/`:

```bash
node --test src/lib/uuid.test.ts src/lib/execution-stream.test.ts src/components/message/chat-unification.test.ts
```

Expected: all focused tests pass.

### Task 4: Verify the complete Web change

**Files:**
- Verify only; no additional files should change.

**Interfaces:**
- Consumes: Completed UUID utility, Chat integration, dependency metadata, and existing Web tests.
- Produces: Verification evidence for behavior, formatting, linting, types, and production compilation.

- [x] **Step 1: Run all Web unit tests**

Run from `src/clients/web/`:

```bash
find src -type f \( -name '*.test.ts' -o -name '*.test.tsx' \) -print0 | xargs -0 node --test
```

Expected: 243 tests run: 224 pass and the same 19 unrelated baseline tests fail. This task must not add any new failing test.

- [x] **Step 2: Run formatting and lint checks**

Run from `src/clients/web/`:

```bash
pnpm format:check
pnpm lint
```

Expected: both commands exit successfully with zero errors.

- [x] **Step 3: Run the production build**

Run from `src/clients/web/`:

```bash
pnpm build
```

Expected: Next.js production build succeeds.

- [x] **Step 4: Verify scope and dependency cleanup**

Run from the repository root:

```bash
rg -n 'id128|Uuid4|\bUlid\b|function nextContextId' \
  src/clients/web/src \
  src/clients/web/package.json \
  src/clients/web/pnpm-lock.yaml \
  -g '!*.test.ts' || true
rg -n 'createUuidV7|uuidv7' src/clients/web/src
rg -n 'crypto\.randomUUID\(\)' src/clients/web/src
git diff --check
git status --short
```

Expected: no production `id128`, `Uuid4`, `Ulid`, or old Context ID wrapper remains; both persisted Web identifier paths use the UUID v7 utility; the unchanged Mermaid render ID is the only `crypto.randomUUID()` call; no whitespace errors are reported; Mobile and backend have no new changes from this task.
