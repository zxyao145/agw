# Project Context Local Time Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display project-context API timestamps in the browser's local time zone and locale.

**Architecture:** Keep API timestamps as ISO strings and localize them only at the UI boundary. A shared frontend utility will normalize offset-less API timestamps as UTC, preserve explicit offsets, and provide browser-local formatting for all project-context views.

**Tech Stack:** TypeScript 5.9, React 19, Next.js 16, Node.js test runner, oxlint, oxfmt

## Global Constraints

- Do not change the backend API contract or timestamp serialization.
- Do not localize timestamps inside `task-client.ts`; raw ISO strings must remain sortable.
- Touch only project-context timestamp parsing and display code.
- Do not create a Git commit unless the user explicitly requests one.
- Preserve unrelated working-tree changes.

---

### Task 1: Add the shared API date-time utility

**Files:**
- Create: `src/clients/web/src/lib/date-time.test.ts`
- Create: `src/clients/web/src/lib/date-time.ts`

**Interfaces:**
- Consumes: ISO date-time strings from the project-context API.
- Produces: `parseApiDateTime(value: string): Date | null` and `formatLocalDateTime(value?: string | null): string`.

- [ ] **Step 1: Write the failing test**

```typescript
import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import { formatLocalDateTime, parseApiDateTime } from "./date-time.ts";

test("API timestamps use UTC semantics and display in the runtime local time zone", () => {
  const previousTimeZone = process.env.TZ;
  process.env.TZ = "Asia/Singapore";

  try {
    assert.equal(
      parseApiDateTime("2026-01-02T03:04:05Z")?.toISOString(),
      "2026-01-02T03:04:05.000Z",
    );
    assert.equal(
      parseApiDateTime("2026-01-02T03:04:05")?.toISOString(),
      "2026-01-02T03:04:05.000Z",
    );
    assert.equal(
      parseApiDateTime("2026-01-02T03:04:05+02:00")?.toISOString(),
      "2026-01-02T01:04:05.000Z",
    );
    assert.equal(
      formatLocalDateTime("2026-01-02T03:04:05"),
      new Date("2026-01-02T03:04:05Z").toLocaleString(),
    );
  } finally {
    if (previousTimeZone === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = previousTimeZone;
    }
  }
});

test("invalid and missing API timestamps use stable fallbacks", () => {
  assert.equal(parseApiDateTime("invalid"), null);
  assert.equal(formatLocalDateTime(null), "-");
  assert.equal(formatLocalDateTime("invalid"), "invalid");
});
```

- [ ] **Step 2: Run the test and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test src/lib/date-time.test.ts
```

Expected: FAIL with `ERR_MODULE_NOT_FOUND` for `./date-time`.

- [ ] **Step 3: Add the minimal implementation**

```typescript
const TIME_ZONE_SUFFIX_PATTERN = /(?:z|[+-]\d{2}:?\d{2})$/i;

export function parseApiDateTime(value: string): Date | null {
  const normalizedValue = value.trim();
  if (!normalizedValue) {
    return null;
  }

  const timestamp = TIME_ZONE_SUFFIX_PATTERN.test(normalizedValue)
    ? normalizedValue
    : `${normalizedValue}Z`;
  const date = new Date(timestamp);

  return Number.isNaN(date.getTime()) ? null : date;
}

export function formatLocalDateTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = parseApiDateTime(value);
  return date ? date.toLocaleString() : value;
}
```

- [ ] **Step 4: Run the test and verify GREEN**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test src/lib/date-time.test.ts
```

Expected: 2 tests pass, 0 fail.

---

### Task 2: Use the shared parser and formatter in project-context views

**Files:**
- Modify: `src/clients/web/src/components/task/conversation-list.tsx`
- Modify: `src/clients/web/src/app/(app)/(tasks)/projects/details/page.tsx`
- Modify: `src/clients/web/src/app/(app)/(tasks)/projects/conversations/details/page.tsx`
- Test: `src/clients/web/src/lib/date-time.test.ts`

**Interfaces:**
- Consumes: `parseApiDateTime` and `formatLocalDateTime` from `@/lib/date-time`.
- Produces: Browser-local context timestamps in all three existing views.

- [ ] **Step 1: Wire the project details page to `formatLocalDateTime`**

Add this import:

```typescript
import { formatLocalDateTime } from "@/lib/date-time";
```

Remove the page-local `formatDate` function and replace its timestamp rendering with:

```tsx
Updated: {formatLocalDateTime(project.updateTime ?? project.createTime)}
```

```tsx
Created: {formatLocalDateTime(conversation.createTime)} · Updated:{" "}
{formatLocalDateTime(conversation.updateTime)}
```

- [ ] **Step 2: Wire the conversation details page to `formatLocalDateTime`**

Add this import:

```typescript
import { formatLocalDateTime } from "@/lib/date-time";
```

Remove the page-local `formatDate` function and use the shared formatter in the existing fields:

```tsx
<div>{formatLocalDateTime(conversation.createTime)}</div>
```

```tsx
<div>{formatLocalDateTime(conversation.updateTime)}</div>
```

- [ ] **Step 3: Normalize API timestamp parsing in the chat sidebar**

Add this import:

```typescript
import { parseApiDateTime } from "@/lib/date-time";
```

Replace the existing relative-time formatter with:

```typescript
const formatDate = (timestamp: string) => {
  const date = parseApiDateTime(timestamp);
  if (!date) return timestamp;

  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMs / 3600000);

  if (diffMins < 1) return "Just now";
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;

  return date.toLocaleDateString(undefined, {
    month: "short",
    day: "numeric",
  });
};
```

- [ ] **Step 4: Re-run the focused test**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test src/lib/date-time.test.ts
```

Expected: 2 tests pass, 0 fail.

---

### Task 3: Verify the complete frontend change

**Files:**
- Verify: `src/clients/web/src/lib/date-time.ts`
- Verify: `src/clients/web/src/lib/date-time.test.ts`
- Verify: `src/clients/web/src/components/task/conversation-list.tsx`
- Verify: `src/clients/web/src/app/(app)/(tasks)/projects/details/page.tsx`
- Verify: `src/clients/web/src/app/(app)/(tasks)/projects/conversations/details/page.tsx`

**Interfaces:**
- Consumes: The completed project-context localization change.
- Produces: Fresh test, lint, format, build, and diff evidence.

- [ ] **Step 1: Run all standalone frontend tests**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test $(find src -name '*.test.ts' -print)
```

Expected: all tests pass with 0 failures.

- [ ] **Step 2: Run lint and formatting checks**

Run from `src/clients/web`:

```bash
pnpm lint
pnpm format:check
```

Expected: both commands exit with code 0.

- [ ] **Step 3: Run the production build**

Run from `src/clients/web`:

```bash
pnpm build
```

Expected: Next.js build exits with code 0.

- [ ] **Step 4: Inspect the final diff**

Run from the repository root:

```bash
git diff --check
git diff -- src/clients/web/src/lib/date-time.ts src/clients/web/src/lib/date-time.test.ts src/clients/web/src/components/task/conversation-list.tsx 'src/clients/web/src/app/(app)/(tasks)/projects/details/page.tsx' 'src/clients/web/src/app/(app)/(tasks)/projects/conversations/details/page.tsx'
```

Expected: no whitespace errors, and every changed production line traces to project-context time localization.

---

### Task 4: Use an exact timestamp for older Chat Contexts

**Files:**
- Modify: `src/clients/web/src/lib/date-time.test.ts`
- Modify: `src/clients/web/src/lib/date-time.ts`
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/page.test.ts`
- Modify: `src/clients/web/src/components/task/conversation-list.tsx`

**Interfaces:**
- Consumes: A valid local `Date` returned by `parseApiDateTime`.
- Produces: `formatLocalDateTimeExact(date: Date): string` with exact `yyyy-MM-dd HH:mm:ss` output.

- [ ] **Step 1: Add a failing exact-format assertion**

Replace the existing named import with a namespace import, keep the existing `@ts-expect-error` comment, and add this assertion inside the existing `Asia/Singapore` test:

```typescript
// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import * as dateTime from "./date-time.ts";

const { formatLocalDateTime, parseApiDateTime } = dateTime;
const exactFormatter = (
  dateTime as typeof dateTime & {
    formatLocalDateTimeExact?: (date: Date) => string;
  }
).formatLocalDateTimeExact;

assert.equal(exactFormatter?.(new Date("2026-01-02T03:04:05Z")), "2026-01-02 11:04:05");
```

- [ ] **Step 2: Run the utility test and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test src/lib/date-time.test.ts
```

Expected: FAIL because the exact formatter result is `undefined`.

- [ ] **Step 3: Add the minimal exact formatter**

```typescript
const padDateTimeComponent = (value: number) => String(value).padStart(2, "0");

export function formatLocalDateTimeExact(date: Date): string {
  return `${date.getFullYear()}-${padDateTimeComponent(date.getMonth() + 1)}-${padDateTimeComponent(date.getDate())} ${padDateTimeComponent(date.getHours())}:${padDateTimeComponent(date.getMinutes())}:${padDateTimeComponent(date.getSeconds())}`;
}
```

- [ ] **Step 4: Re-run the utility test and verify GREEN**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test src/lib/date-time.test.ts
```

Expected: 2 tests pass, 0 fail.

- [ ] **Step 5: Add a failing Chat Contexts wiring test**

Append this source-level regression test to `src/clients/web/src/app/(app)/(interface)/chat/page.test.ts`:

```typescript
test("chat contexts display older timestamps in exact local format", async () => {
  const conversationListSource = await readFile(CONVERSATION_LIST_URL, "utf8");

  assert.match(conversationListSource, /formatLocalDateTimeExact/);
  assert.match(conversationListSource, /return formatLocalDateTimeExact\(date\);/);
  assert.doesNotMatch(conversationListSource, /date\.toLocaleDateString/);
});
```

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test 'src/app/(app)/(interface)/chat/page.test.ts'
```

Expected: FAIL because `conversation-list.tsx` still calls `toLocaleDateString`.

- [ ] **Step 6: Wire the exact formatter into Chat Contexts**

Update the import and final branch in `conversation-list.tsx`:

```typescript
import { formatLocalDateTimeExact, parseApiDateTime } from "@/lib/date-time";
```

```typescript
return formatLocalDateTimeExact(date);
```

- [ ] **Step 7: Run focused verification**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test src/lib/date-time.test.ts 'src/app/(app)/(interface)/chat/page.test.ts'
pnpm lint
pnpm exec oxfmt --check src/lib/date-time.ts src/lib/date-time.test.ts
```

Expected: 9 tests pass, lint has 0 errors, and the utility files pass formatting checks.
