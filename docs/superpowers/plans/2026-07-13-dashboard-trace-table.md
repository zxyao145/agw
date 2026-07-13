# Dashboard Trace Table Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a dashboard `TraceTable` that queries `/api/traces` with all supported filters and server-backed pagination.

**Architecture:** Keep request state and rendering inside a route-local client component. Put deterministic filter-to-query conversion and pagination calculations in a dependency-free utility so they can be exercised with the repository's Node test convention before production code is added.

**Tech Stack:** Next.js 16, React 19, TypeScript, TanStack Query, existing UI primitives, Node test runner, oxlint, and oxfmt.

## Global Constraints

- Preserve the user's staged dashboard summary-label changes.
- Do not modify generated OpenAPI artifacts or backend code.
- Do not add dependencies, automatic polling, URL synchronization, or standalone traces-page changes.
- Do not create a Git commit unless explicitly requested.

---

### Task 1: Trace query and pagination utilities

**Files:**
- Create: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts`
- Create: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table-utils.ts`

**Interfaces:**
- Produces: `TraceFilters`, `EMPTY_TRACE_FILTERS`, `buildTraceQuery(filters, pageIndex, pageSize)`, `getPaginationMeta(total, pageIndex, pageSize)`, `getTraceStatusLabel(status)`, and `getNodeKindLabel(kind)`.
- Consumes: no runtime project dependency; only generated OpenAPI types through a type-only import.

- [ ] **Step 1: Write failing utility tests**

Cover trimmed and omitted filters, local datetime conversion to ISO, page metadata for populated and empty results, known enum labels, and unknown enum fallbacks. The primary query assertion is:

```ts
assert.deepEqual(
  buildTraceQuery(
    {
      projectId: " project-id ",
      contextId: " context-id ",
      agentflowId: " agentflow-id ",
      fromUtc: "2026-07-13T09:00",
      toUtc: "2026-07-13T10:00",
    },
    2,
    50,
  ),
  {
    projectId: "project-id",
    contextId: "context-id",
    agentflowId: "agentflow-id",
    fromUtc: new Date("2026-07-13T09:00").toISOString(),
    toUtc: new Date("2026-07-13T10:00").toISOString(),
    pageIndex: 2,
    pageSize: 50,
  },
);
```

- [ ] **Step 2: Run tests and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test 'src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts'
```

Expected: FAIL because `trace-table-utils.ts` does not exist.

- [ ] **Step 3: Add the minimal utility implementation**

Implement these exact public signatures:

```ts
export type TraceFilters = {
  projectId: string;
  contextId: string;
  agentflowId: string;
  fromUtc: string;
  toUtc: string;
};

export const EMPTY_TRACE_FILTERS: TraceFilters;

export function buildTraceQuery(
  filters: TraceFilters,
  pageIndex: number,
  pageSize: number,
): NonNullable<paths["/api/traces"]["get"]["parameters"]["query"]>;

export function getPaginationMeta(total: number, pageIndex: number, pageSize: number): {
  start: number;
  end: number;
  totalPages: number;
  canGoPrevious: boolean;
  canGoNext: boolean;
};
```

Trim text filters, omit empty values, convert non-empty datetime values with `new Date(value).toISOString()`, include page values, calculate an empty range as `0–0`, map the four status values and eleven node-kind values, and fall back to `Unknown (<value>)`.

- [ ] **Step 4: Run tests and verify GREEN**

Run the same Node test command. Expected: all utility tests pass with zero failures.

### Task 2: TraceTable UI and dashboard integration

**Files:**
- Create: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table.tsx`
- Create: `src/clients/web/src/app/(app)/(overview)/dashboard/page.test.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/page.tsx`

**Interfaces:**
- Consumes: Task 1 utility exports, `apiGet`, `getApiErrorMessage`, TanStack Query, and existing Button/Input/Label/Select/Table/Badge primitives.
- Produces: named React component `TraceTable` with no props.

- [ ] **Step 1: Write the failing dashboard integration test**

Read `page.tsx` as text and assert that it imports `TraceTable` from `./components/trace-table`, renders `<TraceTable />`, and renders it after the summary-card/error section.

- [ ] **Step 2: Run integration and utility tests and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test \
  'src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts' \
  'src/app/(app)/(overview)/dashboard/page.test.ts'
```

Expected: utility tests pass and dashboard integration test fails because `TraceTable` is not imported or rendered.

- [ ] **Step 3: Implement the self-contained component**

Use separate `draftFilters` and `appliedFilters` state, `pageIndex` defaulting to `1`, and `pageSize` defaulting to `20`. Build the React Query key from applied filters and pagination and call:

```ts
apiGet("/api/traces", {
  params: { query: buildTraceQuery(appliedFilters, pageIndex, pageSize) },
  signal,
});
```

The form renders Project ID, Context ID, Agentflow ID, From, and To inputs. Submit copies draft filters to applied filters and returns to page 1. Reset restores both filter states and page 1. Page-size options are `10`, `20`, `50`, and `100`, and changing the size returns to page 1.

Render a bordered dashboard card containing the filter grid, horizontally scrollable table, and footer pagination. Table columns are Start time, Status, Node, Agent, Duration, Project, Context, Agentflow, Input, and Error. Use badge labels from Task 1, show node/agent identifier fallbacks, convert duration with `Number(...)`, truncate long values with a `title`, and render loading, error, and empty states in the table body. Previous/next buttons use `getPaginationMeta` and never cross a boundary.

- [ ] **Step 4: Render TraceTable below the existing dashboard content**

Add this import and render site while preserving all staged summary-label changes:

```tsx
import { TraceTable } from "./components/trace-table";

<TraceTable />
```

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the two-file Node test command. Expected: all tests pass with zero failures.

- [ ] **Step 6: Format only feature files**

Run from `src/clients/web`:

```bash
pnpm exec oxfmt --write \
  'src/app/(app)/(overview)/dashboard/components/trace-table-utils.ts' \
  'src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts' \
  'src/app/(app)/(overview)/dashboard/components/trace-table.tsx' \
  'src/app/(app)/(overview)/dashboard/page.test.ts' \
  'src/app/(app)/(overview)/dashboard/page.tsx'
```

Expected: exit code 0 without formatting unrelated files.

### Task 3: Full verification

**Files:**
- Verify only; no planned file changes.

**Interfaces:**
- Consumes the completed feature.
- Produces fresh test, lint, build, and diff evidence.

- [ ] **Step 1: Run focused tests**

Run the two Node test files. Expected: zero failures.

- [ ] **Step 2: Run frontend lint and formatting checks**

Run from `src/clients/web`:

```bash
pnpm lint
pnpm format:check
```

Expected: both commands exit 0.

- [ ] **Step 3: Run the production build**

Run from `src/clients/web`:

```bash
pnpm build
```

Expected: Next.js production build exits 0.

- [ ] **Step 4: Inspect the final diff**

Run from the repository root:

```bash
git diff --check
git status --short
git diff -- 'src/clients/web/src/app/(app)/(overview)/dashboard/page.tsx' 'src/clients/web/src/app/(app)/(overview)/dashboard/components'
```

Expected: no whitespace errors; staged dashboard labels and unrelated observability documents remain intact; no generated or backend files are changed by this feature.

### Task 4: Extract Input text from persisted trace JSON

**Files:**
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table-utils.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table.tsx`

**Interfaces:**
- Consumes: the persisted trace input JSON string, shaped as an array of messages with `contents` arrays.
- Produces: `extractTraceInputText(input: string): string`.

- [ ] **Step 1: Write failing parser tests**

Add focused tests that require the parser to read only non-empty string values from `contents[*].text`, preserve message/content order, join multiple values with `\n`, ignore non-text content, and return `—` for malformed JSON or JSON without text values:

```ts
assert.equal(
  extractTraceInputText(
    JSON.stringify([
      { contents: [{ text: "first" }, { value: "ignored" }] },
      { contents: [{ text: "second" }, { text: "  " }] },
    ]),
  ),
  "first\nsecond",
);
assert.equal(extractTraceInputText("not-json"), "—");
assert.equal(extractTraceInputText(JSON.stringify([{ contents: [{ value: "ignored" }] }])), "—");
```

- [ ] **Step 2: Run the utility test and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test 'src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts'
```

Expected: FAIL because `extractTraceInputText` is not exported.

- [ ] **Step 3: Implement the minimal parser**

Add this exact signature to `trace-table-utils.ts`:

```ts
export function extractTraceInputText(input: string): string;
```

Parse inside `try/catch`; accept only a top-level array; for every object message, accept only an array-valued `contents`; for every object content, collect `text` only when it is a string and `text.trim()` is non-empty. Join collected original text values with `\n`. Return `—` on parse failure, structural mismatch that yields no text, or no text values. Never return the raw JSON.

- [ ] **Step 4: Use extracted text in the Input cell**

Import `extractTraceInputText`, calculate the display value once per row, and use it for both visible content and `title`:

```tsx
const inputText = extractTraceInputText(trace.input);

<span className="block max-w-64 truncate whitespace-pre-line text-xs text-dust" title={inputText}>
  {inputText}
</span>
```

- [ ] **Step 5: Run focused verification**

Run the utility and dashboard integration tests, targeted oxlint, targeted oxfmt check, and `git diff --check`. Expected: all focused checks exit 0. Run `pnpm build` and report any unrelated repository-wide blocker without changing out-of-scope files.

### Task 5: Format Start time in the browser time zone

**Files:**
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table-utils.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table.tsx`

**Interfaces:**
- Consumes: the API's ISO `startTimeUtc` string.
- Produces: `formatTraceStartTime(value: string): string`.

- [ ] **Step 1: Write failing local-time formatting tests**

Import `formatTraceStartTime`. Temporarily set `process.env.TZ` to `Asia/Singapore`, restore it in `finally`, and assert that both `2026-01-02T03:04:05` and `2026-01-02T03:04:05Z` format as `2026-01-02 11:04:05`. Assert separately that `2026-01-02T03:04:05+02:00` respects its explicit offset and formats as `2026-01-02 09:04:05`. Add a final assertion that `invalid` formats as `—`.

```ts
const previousTimeZone = process.env.TZ;
process.env.TZ = "Asia/Singapore";
try {
  assert.equal(formatTraceStartTime("2026-01-02T03:04:05Z"), "2026-01-02 11:04:05");
} finally {
  process.env.TZ = previousTimeZone;
}

assert.equal(formatTraceStartTime("invalid"), "—");
```

- [ ] **Step 2: Run the utility test and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test 'src/app/(app)/(overview)/dashboard/components/trace-table-utils.test.ts'
```

Expected: FAIL because `formatTraceStartTime` is not exported.

- [ ] **Step 3: Implement exact local formatting**

Add this exact signature:

```ts
export function formatTraceStartTime(value: string): string;
```

Trim the input. Detect a trailing `Z`, `±HH:mm`, or `±HHmm` suffix. Append `Z` only when none is present, then construct `new Date(normalizedValue)`. Return `—` when `Number.isNaN(date.getTime())`; otherwise use local `getFullYear`, `getMonth`, `getDate`, `getHours`, `getMinutes`, and `getSeconds`. Pad every component except the year to two digits and return exactly `yyyy-MM-dd HH:mm:ss`. Do not override an explicit time-zone suffix, pass a `timeZone`, or use UTC getters.

- [ ] **Step 4: Use the formatter in TraceTable**

Import `formatTraceStartTime` and replace the current `new Date(trace.startTimeUtc).toLocaleString()` expression with:

```tsx
{formatTraceStartTime(trace.startTimeUtc)}
```

- [ ] **Step 5: Run focused verification**

Run the utility and dashboard integration tests, targeted oxlint, targeted oxfmt check, and `git diff --check`. Expected: all focused checks exit 0. Run `pnpm build` and report the existing `src/api/task-client.ts:82` blocker if it remains.

### Task 6: Show complete Error content with shadcn Tooltip

**Files:**
- Generate or update: `src/clients/web/src/components/ui/tooltip.tsx`
- Modify: `src/clients/web/package.json`
- Modify: `src/clients/web/pnpm-lock.yaml`
- Modify: `src/clients/web/src/app/layout.tsx`
- Create: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table.test.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/components/trace-table.tsx`

**Interfaces:**
- Consumes: `Tooltip`, `TooltipTrigger`, and `TooltipContent` from `@/components/ui/tooltip`.
- Produces: hover- and focus-accessible complete Error content while preserving the truncated table cell.

- [ ] **Step 1: Install the shadcn Tooltip component**

Run exactly from `src/clients/web`:

```bash
pnpm dlx shadcn@latest add tooltip
```

If prompted because `tooltip.tsx` already exists, allow replacement of that component only. Inspect the resulting diff and reject unrelated file changes.

- [ ] **Step 2: Write the failing Error Tooltip integration test**

Read `trace-table.tsx` as text and assert that it imports `Tooltip`, `TooltipTrigger`, and `TooltipContent`, no longer contains `title={trace.error`, uses `TooltipTrigger asChild`, makes the truncated trigger keyboard-focusable with `tabIndex={0}`, and renders `{trace.error}` inside Tooltip content. Read `src/app/layout.tsx` and assert that it imports `TooltipProvider` and wraps the application content with it.

- [ ] **Step 3: Run the integration test and verify RED**

Run from `src/clients/web`:

```bash
node --experimental-strip-types --test 'src/app/(app)/(overview)/dashboard/components/trace-table.test.ts'
```

Expected: FAIL because the Error cell still uses the native `title` attribute and no shadcn Tooltip.

- [ ] **Step 4: Implement the Error Tooltip**

Import the three shadcn Tooltip exports. When `trace.error` is truthy, render the truncated Error span as an `asChild` trigger with `tabIndex={0}` and wrap it in `Tooltip`; render the complete error inside:

```tsx
<TooltipContent
  side="top"
  className="max-h-80 max-w-[min(40rem,calc(100vw-2rem))] overflow-y-auto whitespace-pre-wrap break-words text-left"
>
  {trace.error}
</TooltipContent>
```

When `trace.error` is falsy, render `—` without Tooltip. Remove the native `title` attribute.

Import `TooltipProvider` into `src/app/layout.tsx` and wrap the existing `QueryProvider` subtree with it so both the new Error Tooltip and pre-existing Tooltip consumers use the generated shadcn provider.

- [ ] **Step 5: Run focused verification**

Run the new integration test and existing utility test, targeted oxlint and oxfmt checks for `trace-table.tsx`, `trace-table.test.ts`, and `tooltip.tsx`, plus `git diff --check`. Run `pnpm build` and report unrelated repository blockers without modifying them.
