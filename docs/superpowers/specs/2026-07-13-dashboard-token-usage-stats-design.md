# Dashboard Token Usage Stats Design

## Goal

Extend `GET /api/dashboard/stats` with global input, output, and total token usage.

## Design

`DashboardController` will continue using the existing `IRepository<ProjectContext>` dependency. It will sum `ProjectContext.Usage.InputTokenCount`, `OutputTokenCount`, and `TotalTokenCount` across every project context and append the results to `DashboardStatsResponse` as `long` values named `UsageInputTokenCount`, `UsageOutputTokenCount`, and `UsageTotalTokenCount`.

No filters, migrations, new services, or frontend changes are required. Empty data sets return zero. The endpoint continues returning its existing Bens.Results envelope.

## Verification

An SQLite-backed controller test will verify aggregation across multiple project contexts and the empty-database result. The focused host test project and backend build must pass.
