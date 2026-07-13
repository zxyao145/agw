# Dashboard Token Usage Cards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display global input, output, and total token usage in the dashboard summary grid.

**Architecture:** Keep the feature inside the existing dashboard page because the response type and `SummaryCards` are already local to that route. Add only the three API fields, a small number formatter, three card definitions, and a source-level regression test matching the existing test style.

**Tech Stack:** Next.js 16, React 19, TypeScript 5.9, Tailwind CSS, Node test runner

## Global Constraints

- Preserve all existing dashboard cards and TraceTable behavior.
- Use labels `TotalInputToken`, `TotalOutputToken`, and `TotalToken` exactly.
- Format available token values with `Number.prototype.toLocaleString()`.
- Render `—` when token data is unavailable.
- Do not create a Git commit without explicit user authorization.

---

### Task 1: Dashboard token usage cards

**Files:**
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/page.test.ts`
- Modify: `src/clients/web/src/app/(app)/(overview)/dashboard/page.tsx`

**Interfaces:**
- Consumes: `usageInputTokenCount`, `usageOutputTokenCount`, and `usageTotalTokenCount` from `GET /api/dashboard/stats`
- Produces: three `SummaryCards` entries labeled `TotalInputToken`, `TotalOutputToken`, and `TotalToken`

- [x] **Step 1: Write the failing source-level test**

Append this test to `page.test.ts`:

```ts
test("dashboard summary maps and formats all token usage totals", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /usageInputTokenCount: number;/);
  assert.match(source, /usageOutputTokenCount: number;/);
  assert.match(source, /usageTotalTokenCount: number;/);
  assert.match(
    source,
    /label: "TotalInputToken",[\s\S]*?formatStat\(stats\?\.usageInputTokenCount, hasData\)/,
  );
  assert.match(
    source,
    /label: "TotalOutputToken",[\s\S]*?formatStat\(stats\?\.usageOutputTokenCount, hasData\)/,
  );
  assert.match(
    source,
    /label: "TotalToken",[\s\S]*?formatStat\(stats\?\.usageTotalTokenCount, hasData\)/,
  );
  assert.match(source, /return value\.toLocaleString\(\);/);
});
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```bash
node --test 'src/app/(app)/(overview)/dashboard/page.test.ts'
```

from `src/clients/web`.

Expected: FAIL because `page.tsx` does not contain the token response fields or cards.

- [x] **Step 3: Implement the response fields and formatter**

Add these fields to `DashboardStatsResponse`:

```ts
usageInputTokenCount: number;
usageOutputTokenCount: number;
usageTotalTokenCount: number;
```

Add this helper before `SummaryCards`:

```ts
function formatStat(value: number | undefined, hasData: boolean): string {
  if (!hasData || value === undefined) {
    return "—";
  }

  return value.toLocaleString();
}
```

- [x] **Step 4: Append the three token cards**

Append these objects after the Agentflow card:

```ts
{
  label: "TotalInputToken",
  value: formatStat(stats?.usageInputTokenCount, hasData),
  color: "text-cyan-300",
},
{
  label: "TotalOutputToken",
  value: formatStat(stats?.usageOutputTokenCount, hasData),
  color: "text-orange-300",
},
{
  label: "TotalToken",
  value: formatStat(stats?.usageTotalTokenCount, hasData),
  color: "text-emerald-300",
},
```

- [x] **Step 5: Run focused and frontend verification**

Run from `src/clients/web`:

```bash
node --test 'src/app/(app)/(overview)/dashboard/page.test.ts'
pnpm lint
pnpm format:check
pnpm build
```

Expected: the focused tests pass, lint and formatting checks report no errors, and the production build exits successfully.
