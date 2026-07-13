# Dashboard Token Usage Cards Design

## Goal

Display the dashboard API's global token usage totals in `SummaryCards`.

## Design

Extend the local `DashboardStatsResponse` type with `usageInputTokenCount`, `usageOutputTokenCount`, and `usageTotalTokenCount`. Append three cards after the existing Agentflow card so the current responsive grid becomes three rows of three cards on large screens.

The card mappings are fixed:

- `usageInputTokenCount` → `TotalInputToken`
- `usageOutputTokenCount` → `TotalOutputToken`
- `usageTotalTokenCount` → `TotalToken`

Token values use locale-aware thousands separators. Loading continues to render one skeleton per card, and unavailable data continues to render `—`. Existing dashboard statistics, trace content, error handling, and API polling remain unchanged.

## Verification

Extend the existing source-level dashboard test to verify the three response fields, card labels, field mappings, and locale formatting. Run the focused Node test, frontend lint, formatting check, and production build.
