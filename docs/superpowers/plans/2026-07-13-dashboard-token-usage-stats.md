# Dashboard Token Usage Stats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add global input, output, and total token usage sums to `GET /api/dashboard/stats`.

**Architecture:** Keep aggregation in the existing dashboard controller and query the existing `ProjectContext.Usage` columns through `IRepository<ProjectContext>`. Extend only the response record and cover the behavior with SQLite-backed controller tests.

**Tech Stack:** .NET 10, ASP.NET Core, Entity Framework Core, SQLite, xUnit v3

## Global Constraints

- Preserve the Bens.Results response envelope.
- Aggregate every project context without filters.
- Return zero for an empty project-context table.
- Do not add migrations or frontend changes.
- Do not create a Git commit without explicit user authorization.

---

### Task 1: Dashboard token usage aggregation

**Files:**
- Create: `tests/Agw.Host.Tests/DashboardControllerTests.cs`
- Modify: `src/server/Agw.Host/Controllers/DashboardController.cs`

**Interfaces:**
- Consumes: `IRepository<ProjectContext>.Queryable` and `ProjectContext.Usage`
- Produces: `DashboardStatsResponse.UsageInputTokenCount`, `UsageOutputTokenCount`, and `UsageTotalTokenCount` as `long`

- [x] **Step 1: Write failing controller tests**

Add SQLite-backed tests that insert two project contexts with distinct usage values, call `GetStats`, and assert each global sum. Add an empty-table test asserting all three values are zero.

- [x] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --filter FullyQualifiedName~DashboardControllerTests`

Expected: FAIL because the three response properties do not exist.

- [x] **Step 3: Implement the minimal aggregation**

Append three `SumAsync` expressions to the existing `DashboardStatsResponse` construction and add the matching `long` positional properties to the response record.

- [x] **Step 4: Run focused tests and verify GREEN**

Run: `dotnet test tests/Agw.Host.Tests/Agw.Host.Tests.csproj --filter FullyQualifiedName~DashboardControllerTests`

Expected: PASS.

- [x] **Step 5: Verify the backend build**

Run: `dotnet build Agw.slnx --no-restore`

Expected: Build succeeds with zero errors.
