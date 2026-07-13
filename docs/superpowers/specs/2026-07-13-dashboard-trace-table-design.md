# Dashboard Trace Table Design

## Goal

Add a self-contained `TraceTable` to the dashboard that queries `GET /api/traces`, exposes every supported filter, displays trace data, and provides server-backed pagination.

## Scope

- Add `TraceTable` under the dashboard route's local `components` directory.
- Render it below the existing dashboard summary cards and statistics error state.
- Support the API filters `projectId`, `contextId`, `agentflowId`, `fromUtc`, and `toUtc`.
- Support `pageIndex` and `pageSize` pagination.
- Reuse the generated OpenAPI types and the shared `apiGet` client.
- Preserve the dashboard's existing styling and behavior.

The standalone `/traces` page, backend contracts, and automatic polling are outside this change.

## Component Design

`TraceTable` owns its filter form, applied query, current page, page size, and React Query request. The component keeps draft form values separate from applied filter values so editing a field does not issue a request until the user submits the form.

Submitting or resetting filters returns to page 1. Changing page size also returns to page 1. Moving between pages reuses the last applied filters. The React Query key includes the applied filters, page index, and page size.

The filter form contains text inputs for Project ID, Context ID, and Agentflow ID, plus `datetime-local` inputs for the UTC range. Non-empty datetime values are converted to ISO strings before they are sent to the API. Empty values are omitted from the query string.

## Table Design

The table shows the following trace information:

- Start time
- Status
- Node name or node identifier
- Agent name or identifier
- Duration
- Project ID
- Context ID
- Agentflow ID
- Input summary
- Error summary

Long identifiers and text values remain readable through monospace styling, truncation, and native title text. Status uses a restrained badge treatment consistent with the existing dashboard rather than introducing a new visual system.

The Input column parses the persisted JSON message array and reads only non-empty string values at `contents[*].text`. When more than one text value exists, the component joins them with newline characters in message and content order. Invalid JSON, an unexpected structure, or input without text content displays `—`; the raw JSON is never used as a fallback.

The Start time column parses `startTimeUtc` as an instant and formats it in the browser's local time zone as `yyyy-MM-dd HH:mm:ss`. A value without `Z` or a numeric time-zone offset is treated as UTC; a value with `Z` or an explicit offset keeps that supplied time-zone meaning. Formatting uses local `Date` getters with explicit zero-padding so output does not vary with browser locale. Invalid timestamps display `—`.

The Error column keeps a truncated summary in the table. When an error exists, its cell uses the shadcn Tooltip installed with `pnpm dlx shadcn@latest add tooltip`; mouse hover or keyboard focus reveals the complete error. The root layout wraps the application in the generated `TooltipProvider`, as required by the current shadcn component. Tooltip content preserves line breaks, breaks long tokens, stays within the viewport, and allows scrolling when the error is taller than its maximum height. A trace without an error displays `—` without a tooltip.

The component renders dedicated loading, request-error, and empty-result states. Pagination displays the current item range and total count, with previous and next buttons disabled at the boundaries.

## Data Contract

The component consumes the generated OpenAPI response type for `/api/traces` and calls:

```ts
apiGet("/api/traces", {
  params: {
    query: {
      projectId,
      contextId,
      agentflowId,
      fromUtc,
      toUtc,
      pageIndex,
      pageSize,
    },
  },
});
```

No generated API artifact is changed because the endpoint and types already exist.

## Error Handling

Request errors are converted to a user-facing message using the same `ApiError` envelope handling already used by the dashboard. Filter inputs are passed to the backend without duplicating GUID or date-range validation; backend validation remains authoritative and its returned message is shown in the component.

## Testing and Verification

- Add tests first for query construction, applying/resetting filters, and pagination boundaries using the repository's existing frontend test conventions.
- Add a focused parser test for one text value, ordered multi-value joining, ignored non-text content, malformed JSON, and missing text content.
- Add a focused date-format test that fixes the runtime to a non-UTC time zone and verifies exact local `yyyy-MM-dd HH:mm:ss` output plus the invalid-timestamp fallback.
- Add a focused source integration test that verifies the Error cell uses shadcn `Tooltip`, `TooltipTrigger`, and `TooltipContent`, keeps the full error in Tooltip content, and does not rely on the native `title` attribute.
- Run the focused tests and confirm the expected red-green cycle.
- Run the frontend lint, formatting check, and production build.
- If the local app can be started with its dependencies, inspect the dashboard in the browser to verify layout, filtering, loading, empty/error states, and pagination behavior.
- Run `git diff --check` and verify that unrelated working-tree files remain untouched.

## Non-Goals

- No backend changes.
- No OpenAPI regeneration.
- No automatic polling or manual refresh control.
- No URL query-string synchronization.
- No changes to the standalone traces page.
- No Git commit unless explicitly requested.
