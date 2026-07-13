# Browser Local Date/Time Displays Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every browser-side date/time display use the browser's local time with the exact `yyyy-MM-dd HH:mm:ss` shape, while retaining friendly relative text for recent Chat Contexts.

**Architecture:** Keep API timestamps as strings until the display boundary, then route them through `src/lib/date-time.ts`, which treats timestamps without an explicit offset as UTC and formats the resulting `Date` with browser-local getters. Chat Contexts use a dedicated friendly wrapper over the same parser and exact formatter; input serialization and numeric localization remain unchanged.

**Tech Stack:** TypeScript, React 19, Next.js 16, Node's built-in test runner, oxlint, oxfmt.

## Global Constraints

- Browser display format is exactly `yyyy-MM-dd HH:mm:ss`.
- File comments always show the complete local date and time.
- Chat Contexts show `Just now`, `Xm ago`, or `Xh ago` only for timestamps from now through less than 24 hours ago; all other valid values use the exact local format.
- Keep native `datetime-local` input conversion and API `toISOString()` serialization unchanged.
- Keep numeric `toLocaleString()` calls unchanged.
- Preserve each screen's existing missing/invalid-value fallback.
- Work directly on `main` as explicitly authorized by the user.
- Do not create a Git commit unless the user explicitly asks.

---

### Task 1: Exact and friendly shared formatters

**Files:**
- Modify: `src/clients/web/src/lib/date-time.test.ts`
- Modify: `src/clients/web/src/lib/date-time.ts`
- Modify: `src/clients/web/src/components/task/conversation-list.tsx`

**Interfaces:**
- Produces: `formatLocalDateTime(value?: string | null): string`, returning the exact local form for valid API timestamps.
- Produces: `formatFriendlyLocalDateTime(value: string, now?: Date): string`, returning recent friendly text or the exact local form.

- [ ] **Step 1: Write failing formatter tests**

Add deterministic assertions under `TZ=Asia/Singapore`:

```ts
assert.equal(formatLocalDateTime("2026-01-02T03:04:05"), "2026-01-02 11:04:05");
assert.equal(
  formatFriendlyLocalDateTime("2026-01-02T03:03:30Z", new Date("2026-01-02T03:04:05Z")),
  "Just now",
);
assert.equal(
  formatFriendlyLocalDateTime("2026-01-02T02:34:05Z", new Date("2026-01-02T03:04:05Z")),
  "30m ago",
);
assert.equal(
  formatFriendlyLocalDateTime("2026-01-01T05:04:05Z", new Date("2026-01-02T03:04:05Z")),
  "22h ago",
);
assert.equal(
  formatFriendlyLocalDateTime("2026-01-01T03:04:05Z", new Date("2026-01-02T03:04:05Z")),
  "2026-01-01 11:04:05",
);
assert.equal(
  formatFriendlyLocalDateTime("2026-01-02T03:05:05Z", new Date("2026-01-02T03:04:05Z")),
  "2026-01-02 11:05:05",
);
```

- [ ] **Step 2: Run the formatter test and verify RED**

Run:

```bash
cd src/clients/web
node --test src/lib/date-time.test.ts
```

Expected: failure because `formatLocalDateTime` still uses `toLocaleString()` and `formatFriendlyLocalDateTime` is not exported.

- [ ] **Step 3: Implement the minimal shared behavior**

Use the existing parser and exact formatter:

```ts
export function formatLocalDateTime(value?: string | null): string {
  if (!value) return "-";

  const date = parseApiDateTime(value);
  return date ? formatLocalDateTimeExact(date) : value;
}

export function formatFriendlyLocalDateTime(value: string, now = new Date()): string {
  const date = parseApiDateTime(value);
  if (!date) return value;

  const diffMs = now.getTime() - date.getTime();
  if (diffMs >= 0) {
    const diffMinutes = Math.floor(diffMs / 60_000);
    if (diffMinutes < 1) return "Just now";
    if (diffMinutes < 60) return `${diffMinutes}m ago`;

    const diffHours = Math.floor(diffMs / 3_600_000);
    if (diffHours < 24) return `${diffHours}h ago`;
  }

  return formatLocalDateTimeExact(date);
}
```

Replace the component-local Chat Context formatter with:

```tsx
{formatFriendlyLocalDateTime(context.updateTime ?? context.createTime)}
```

- [ ] **Step 4: Run the formatter and chat tests and verify GREEN**

Run:

```bash
cd src/clients/web
node --test src/lib/date-time.test.ts 'src/app/(app)/(interface)/chat/page.test.ts'
```

Expected: all tests pass.

### Task 2: Migrate browser date/time display sites

**Files:**
- Create: `src/clients/web/src/lib/date-time-usage.test.ts`
- Modify: `src/clients/web/src/components/file-explorer/comment-section.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agents/components/agents-table.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agentflows/components/agentflows-table.tsx`
- Modify: `src/clients/web/src/app/(app)/(providers)/models/components/models-table.tsx`
- Modify: `src/clients/web/src/app/(app)/(providers)/providers/components/providers-table.tsx`
- Modify: `src/clients/web/src/app/(app)/(tasks)/projects/page.tsx`
- Modify: `src/clients/web/src/app/(app)/skills/page.tsx`
- Modify: `src/clients/web/src/app/(app)/(jobs)/jobs/page.tsx`
- Modify: `src/clients/web/src/app/(app)/(jobs)/jobs/logs/page.tsx`
- Modify: `src/clients/web/src/app/(app)/integrations/types.ts`
- Modify: `src/clients/web/src/app/(app)/integrations/callback/page.tsx`
- Modify: `src/clients/web/src/app/(app)/settings/page.tsx`

**Interfaces:**
- Consumes: `formatLocalDateTime` and `formatLocalDateTimeExact` from `@/lib/date-time`.
- Preserves: `formatDateTime(null) === "Not available"` in integrations.

- [ ] **Step 1: Write a failing source-policy test**

Recursively inspect production `.ts` and `.tsx` files under `src`, excluding `*.test.ts` and generated `src/api/openapi.d.ts`, and report files that match any forbidden date display formatter:

```ts
const forbiddenDateFormatters = [
  /\.toLocaleDateString\s*\(/,
  /\.toLocaleTimeString\s*\(/,
  /new\s+Intl\.DateTimeFormat\s*\(/,
  /new\s+Date\([^)]*\)\.toLocaleString\s*\(/,
  /\b(?:date|d|parsedDate|timestamp)\.toLocaleString\s*\(/,
];
```

Assert that the violations array is empty. Numeric `value.toLocaleString()`, `maxTokens.toLocaleString()`, durations, and totals must not match these expressions.

- [ ] **Step 2: Run the policy test and verify RED**

Run:

```bash
cd src/clients/web
node --test src/lib/date-time-usage.test.ts
```

Expected: failure listing the existing page-local date formatters and the comment time-only formatter.

- [ ] **Step 3: Replace display-only formatters**

Import the shared helper and use these display forms:

```tsx
{formatLocalDateTime(entity.createTime)}
{formatLocalDateTimeExact(comment.timestamp)}
```

Remove only page-local formatter functions made unused by these changes. In integrations, keep the screen-specific fallback:

```ts
export function formatDateTime(value?: string | null): string {
  return value ? formatLocalDateTime(value) : "Not available";
}
```

In the OAuth callback, format only a present stored timestamp:

```tsx
{matchingRequest?.createdAt ? formatLocalDateTime(matchingRequest.createdAt) : "Unknown"}
```

For jobs, format the mixed trigger field only when it represents a one-time timestamp:

```tsx
{job.triggerType === TRIGGER_TYPE_ONCE
  ? formatLocalDateTime(job.triggerValue)
  : job.triggerValue}
```

Apply the same conditional in both the jobs table and detail dialog. Leave cron expressions, interval values, `datetime-local`, `toLocalInput`, `fromLocalInput`, and `toISOString()` untouched.

- [ ] **Step 4: Run the policy and focused regression tests and verify GREEN**

Run:

```bash
cd src/clients/web
node --test src/lib/date-time-usage.test.ts src/lib/date-time.test.ts 'src/app/(app)/(interface)/chat/page.test.ts' 'src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts'
```

Expected: all tests pass.

### Task 3: Static and build verification

**Files:**
- Verify all files modified by Tasks 1 and 2.

**Interfaces:**
- Consumes: the exact formatter and all migrated browser display sites.
- Produces: verification evidence only; no source interface changes.

- [ ] **Step 1: Format only changed frontend files**

Run `pnpm exec oxfmt` with the explicit changed `.ts` and `.tsx` paths. Do not reformat unrelated files.

Expected: changed files conform to repository formatting.

- [ ] **Step 2: Run frontend lint**

Run:

```bash
cd src/clients/web
pnpm lint
```

Expected: no new lint errors from the changed files.

- [ ] **Step 3: Run the production build**

Run:

```bash
cd src/clients/web
pnpm build
```

Expected: date/time changes compile. If the known pre-existing `src/api/task-client.ts` TS2352 diagnostic remains, record it without changing unrelated code.

- [ ] **Step 4: Run final scope checks**

Run:

```bash
git diff --check
rg -n --glob '*.{ts,tsx}' 'toLocaleDateString|toLocaleTimeString|Intl\.DateTimeFormat|new Date\([^)]*\)\.toLocaleString' src/clients/web/src
git status --short
```

Expected: no date-display formatter violations, no whitespace errors, and only intended plan/test/frontend files are modified. Numeric localization and input/API date conversion remain present and unchanged.
