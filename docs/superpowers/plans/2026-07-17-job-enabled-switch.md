# Job Enabled Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Jobs-table Switch that persists enabled state through a dedicated Server endpoint and changes visually only after the Server succeeds.

**Architecture:** A body-based `PUT /api/jobs/enabled` endpoint delegates to `JobAppService.UpdateEnabledAsync`, which changes only the enabled and audit fields and wakes scheduler prefetch when enabling. The Web table uses one React Query mutation plus a Set of pending Job IDs, updating the Jobs cache only from the successful Server response.

**Tech Stack:** ASP.NET Core minimal APIs, EF Core, Bens.Results, xUnit, Next.js 16, React 19, TanStack Query, Radix Switch, Node test runner.

## Global Constraints

- Do not modify Job schedule, retry, status, name, prompt, project, or Agent assignment while toggling enabled state.
- The Switch remains at its existing value until the Server returns success.
- Only the Switch for the Job being updated is disabled; other Jobs remain interactive.
- All JSON API responses use Bens.Results.
- Do not create a Git commit or stage files unless the user explicitly asks.

---

### Task 1: Application-level enabled update

**Files:**
- Create: `src/server/Agw.Jobs/Application/Contracts/JobEnabledUpdateRequest.cs`
- Modify: `src/server/Agw.Jobs/Application/Services/JobAppService.cs`
- Modify: `src/server/Agw.Jobs/Scheduling/Coordination/JobSchedulerWakeSignal.cs`
- Test: `tests/Agw.Jobs.Tests/JobAppServiceTests.cs`
- Test: `tests/Agw.Jobs.Tests/JobSchedulerWakeSignalTests.cs`

**Interfaces:**
- Produces: `JobEnabledUpdateRequest { Guid JobId; bool IsEnabled; }`
- Produces: `Task<Job?> JobAppService.UpdateEnabledAsync(JobEnabledUpdateRequest request, string user)`
- Produces: `void JobSchedulerWakeSignal.NotifyChanged()`

- [ ] **Step 1: Write failing service and wake-signal tests**

Add tests asserting that disabling changes only `IsEnabled`, `UpdateBy`, and `UpdateTime`, a missing ID returns null, and `NotifyChanged` releases a waiter.

- [ ] **Step 2: Run tests and confirm RED**

Run: `dotnet test tests/Agw.Jobs.Tests --filter "FullyQualifiedName~JobAppServiceTests|FullyQualifiedName~JobSchedulerWakeSignalTests"`

Expected: build/test failure because `JobEnabledUpdateRequest`, `UpdateEnabledAsync`, and `NotifyChanged` do not exist.

- [ ] **Step 3: Implement the minimal service behavior**

Create the request contract and add:

```csharp
public async Task<Job?> UpdateEnabledAsync(JobEnabledUpdateRequest request, string user)
{
    var entity = await _jobTaskRepository.GetByIdAsync(request.JobId);
    if (entity == null)
    {
        return null;
    }

    entity.IsEnabled = request.IsEnabled;
    entity.UpdateBy = user;
    entity.UpdateTime = _timeProvider.GetUtcNow();
    _jobTaskRepository.Update(entity);
    await _unitOfWork.SaveChangesAsync();

    if (entity.IsEnabled)
    {
        _schedulerWakeSignal.NotifyChanged();
    }

    return entity;
}
```

`NotifyChanged` releases the existing scheduler signal without changing create-time filtering.

- [ ] **Step 4: Run the focused tests and confirm GREEN**

Run the Task 1 test command. Expected: all selected tests pass.

### Task 2: Dedicated Bens.Results API and generated types

**Files:**
- Modify: `src/server/Agw.Jobs/Api/EndpointRouteBuilderExtensions.cs`
- Test: `tests/Agw.Jobs.Tests/EndpointExtensionTests.cs`
- Test: `tests/Agw.Jobs.Tests/JobsApiTests.cs`
- Generated: `src/clients/web/src/api/openapi.d.ts`

**Interfaces:**
- Produces: `PUT /api/jobs/enabled`
- Consumes: `JobEnabledUpdateRequest`
- Returns: `ApiResult<Job>`

- [ ] **Step 1: Write failing route and API tests**

Extend the route list with `("api/jobs/enabled", "PUT")`. Add an integration test that seeds one Job, sends `{ jobId, isEnabled: false }`, and asserts a successful Bens.Results envelope containing `isEnabled: false` while persisted schedule fields remain unchanged.

- [ ] **Step 2: Run tests and confirm RED**

Run: `dotnet test tests/Agw.Jobs.Tests --filter "FullyQualifiedName~EndpointExtensionTests|FullyQualifiedName~JobsApiTests"`

Expected: route/API assertions fail because the endpoint is not mapped.

- [ ] **Step 3: Map the endpoint**

Map `routeGroup.MapPut("jobs/enabled", UpdateEnabledAsync).Produces<ApiResult<Job>>()`. The handler obtains the user name, calls the application service, returns `ApiResult.Ok(job)`, and maps null through `ErrorCodes.ResourceNotFound.ToApiResult()`.

- [ ] **Step 4: Run focused API tests and confirm GREEN**

Run the Task 2 test command. Expected: all selected tests pass.

- [ ] **Step 5: Regenerate the Web API types**

Start or reuse the local Agw Host OpenAPI endpoint, refresh `src/clients/web/openapi.json` using the repository's existing generation workflow, then run `pnpm gen:api` from `src/clients/web`. Verify `openapi.d.ts` contains `/api/jobs/enabled` and `JobEnabledUpdateRequest`.

### Task 3: Jobs table Enabled Switch

**Files:**
- Modify: `src/clients/web/src/app/(app)/(jobs)/jobs/page.tsx`
- Test: `src/clients/web/src/app/(app)/(jobs)/jobs/page.test.ts`

**Interfaces:**
- Consumes: `PUT /api/jobs/enabled`
- Maintains: `Set<string>` of pending Job IDs

- [ ] **Step 1: Write a failing source-contract test**

Assert that the table has an `Enabled` header before `Status`, renders `Switch checked={job.isEnabled}`, disables it with `pendingEnabledJobIds.has(job.id)`, calls the dedicated API, updates `queryClient.setQueryData<JobDto[]>(["jobs"], ...)` only in `onSuccess`, and shows an error toast in `onError`.

- [ ] **Step 2: Run the test and confirm RED**

Run: `node --test 'src/app/(app)/(jobs)/jobs/page.test.ts'` from `src/clients/web`.

Expected: the new Enabled-column assertions fail.

- [ ] **Step 3: Implement the mutation and table column**

Add one mutation for `{ jobId, isEnabled }`, a pending-ID Set, and a toggle handler. Do not optimistically mutate the row. On success replace only the returned Job in the `jobs` cache; on error show `Update failed: ...`; on settled remove that Job ID from the pending Set. Remove the duplicate Enabled/Disabled text from the Status cell while retaining retry and error details.

- [ ] **Step 4: Run Web tests and confirm GREEN**

Run the Task 3 test command, then run the related Jobs and API client tests. Expected: all pass.

### Task 4: Final verification

**Files:**
- Verify only; no new files.

- [ ] **Step 1: Backend verification**

Run: `dotnet test tests/Agw.Jobs.Tests`

Expected: zero failures.

- [ ] **Step 2: Web verification**

Run from `src/clients/web`:

```bash
node --test 'src/app/(app)/(jobs)/jobs/page.test.ts'
pnpm lint
pnpm build
```

Expected: Jobs tests and production build pass; lint has no new errors or warnings from changed files.

- [ ] **Step 3: Visual behavior check**

Open the Jobs page, toggle one row, and verify that only that Switch is disabled while waiting, the checked state changes after success, and a failed request leaves the old value visible with a toast.

- [ ] **Step 4: Diff integrity check**

Run: `git diff --check`

Expected: no whitespace errors. Do not stage or commit.
