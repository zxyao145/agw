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
