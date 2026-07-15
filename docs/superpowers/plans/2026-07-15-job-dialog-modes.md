# Job Dialog Modes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Jobs Create/Edit dialogs default to a compact scheduling form, expose Name/Max Retry/Enabled through an Advanced toggle, use `AgentSelector`, and generate blank job names on the server.

**Architecture:** Extend the existing reusable `AgentSelector` with optional clear behavior, then keep all Job dialog form state in the existing `JobFormState` while controlling only field visibility locally inside `JobDialog`. Centralize default-name generation in `JobAppService`, using the repository count and injected `TimeProvider` so every API client gets the same behavior.

**Tech Stack:** React 19, TypeScript, TanStack React Query, Shadcn UI, ASP.NET Core, EF Core, xUnit, SQLite test provider.

## Global Constraints

- `agentType: 0` represents Agent and `agentType: 1` represents Agentflow.
- Default Job dialog fields are Project ID, Agent ID, Trigger fields, and Prompt.
- Advanced fields are Job Name, Max Retry Count, and Enabled.
- Footer order is Cancel, primary submit, then Advanced/Basic.
- Edit Status is preserved in the request but is not editable in the dialog.
- Blank Create or Update names become `job-{job count + 1}-{UTC yyyyMMdd}`.
- Existing staged and unrelated changes remain untouched.
- Do not create a Git commit unless the user explicitly requests one.

---

### Task 1: Extend AgentSelector for optional assignments

**Files:**
- Modify: `src/clients/web/src/components/agent-selector.test.ts`
- Modify: `src/clients/web/src/components/agent-selector.tsx`

**Interfaces:**
- Consumes: existing `AgentSelection` and `SearchableSelect`.
- Produces: optional `clearable?: boolean`, `placeholder?: string`, and `onClear?: () => void` props while preserving the existing Chat API.

- [ ] **Step 1: Add a failing clearable-contract test**

Add a test that reads `agent-selector.tsx` and asserts the three optional props, forwards `clearable`, uses the supplied placeholder, and calls `onClear` when `parseTargetValue` rejects the empty value.

```ts
test("AgentSelector supports an optional unassigned state", async () => {
  const source = await readFile(COMPONENT_URL, "utf8");

  assert.match(source, /clearable\?: boolean;/);
  assert.match(source, /placeholder\?: string;/);
  assert.match(source, /onClear\?: \(\) => void;/);
  assert.match(source, /if \(!target\) \{[\s\S]*onClear\?\.\(\)/);
  assert.match(source, /clearable=\{clearable\}/);
  assert.match(source, /placeholder=\{placeholder\}/);
});
```

- [ ] **Step 2: Verify RED**

Run:

```bash
cd src/clients/web
node --experimental-strip-types --test src/components/agent-selector.test.ts
```

Expected: the new test fails because the optional props do not exist.

- [ ] **Step 3: Implement the optional props**

Extend `AgentSelectorProps`, default `clearable` to `false` and `placeholder` to `Select agent or agentflow`, call `onClear?.()` for an empty/invalid selected value, and pass both props to `SearchableSelect`. Do not change `onSelect`.

- [ ] **Step 4: Verify GREEN**

Run the same command and expect both AgentSelector tests to pass.

### Task 2: Implement compact and advanced Job dialog modes

**Files:**
- Modify: `src/clients/web/src/app/(app)/(jobs)/jobs/page.test.ts`
- Modify: `src/clients/web/src/app/(app)/(jobs)/jobs/page.tsx`

**Interfaces:**
- Consumes: `AgentSelector`, `AgentSelection`, `JobFormState`, and existing request builders.
- Produces: compact Create/Edit dialogs and unchanged request payload fields.

- [ ] **Step 1: Add failing Job dialog contract tests**

Add source contract tests asserting:

```ts
assert.match(source, /import \{ AgentSelector/);
assert.match(source, /const \[isAdvanced, setIsAdvanced\] = React\.useState\(false\)/);
assert.match(source, /<AgentSelector[\s\S]*placeholder="Not assigned"[\s\S]*clearable/);
assert.match(source, /isAdvanced \? \([\s\S]*Job Name[\s\S]*Max Retry Count[\s\S]*Enabled/);
assert.match(source, /setIsAdvanced\(\(current\) => !current\)/);
assert.doesNotMatch(source, /throw new Error\("Job name is required\."\)/);
assert.doesNotMatch(source, /<Label htmlFor=\{`\$\{mode\}-status`\}>Status<\/Label>/);
```

- [ ] **Step 2: Verify RED**

Run only the new Job dialog tests with `node --experimental-strip-types --test --test-name-pattern` and confirm they fail on the missing compact/advanced structure.

- [ ] **Step 3: Replace Agent Type/ID with AgentSelector**

Remove `AgentOption`, `assignableAgents`, `areAssignableAgentsReady`, the Jobs-owned Agent/Agentflow queries, their option memo, and the validation effect. Render:

```tsx
<AgentSelector
  id={`${mode}-agent-id`}
  projectId={form.projectId}
  value={
    form.agentType !== null && form.agentId
      ? { agentType: form.agentType as 0 | 1, agentId: form.agentId }
      : null
  }
  onSelect={({ agentType, agentId }) =>
    setForm((current) => ({ ...current, agentType, agentId }))
  }
  onClear={() =>
    setForm((current) => ({ ...current, agentType: null, agentId: "" }))
  }
  placeholder="Not assigned"
  clearable
/>
```

- [ ] **Step 4: Add Advanced visibility and footer toggle**

Add local `isAdvanced` state, reset it when the dialog closes, move Name/Max Retry/Enabled into one `isAdvanced` conditional block, remove the Edit Status control, and render the outline Advanced/Basic button after the primary submit button. Keep Project, AgentSelector, Trigger controls, and Prompt visible.

- [ ] **Step 5: Allow an empty name payload**

Remove only the `Job name is required.` client check. Continue sending `name: form.name.trim()` and preserve all other validation.

- [ ] **Step 6: Verify GREEN**

Run the focused Jobs tests plus AgentSelector tests and expect the new contract tests to pass.

### Task 3: Generate blank names in JobAppService

**Files:**
- Create: `tests/Agw.Jobs.Tests/JobAppServiceTests.cs`
- Modify: `src/server/Agw.Jobs/Application/Services/JobAppService.cs`

**Interfaces:**
- Consumes: `IRepository<Job>.Queryable`, `TimeProvider.GetUtcNow()`, and existing `CreateAsync`/`UpdateAsync` requests.
- Produces: trimmed supplied names or `job-{count + 1}-{yyyyMMdd}` for blank names.

- [ ] **Step 1: Add failing service tests**

Use an in-memory SQLite `AgwDbContext`, `JobRepo`, `EfRepository<JobLog>`, `EfRepository<TaskRecord>`, `EfRepository<ProjectContext>`, `UnitOfWork`, `JobTimeCalculator`, `JobDomainEventDispatcher`, and `TestTimeProvider`.

Create tests named:

```csharp
CreateAsync_BlankName_GeneratesCountBasedUtcName
CreateAsync_ProvidedName_TrimsAndPreservesName
UpdateAsync_BlankName_GeneratesCountBasedUtcName
```

Seed two valid Job records, set UTC time to `2026-07-15T23:30:00Z`, and assert the blank generated name equals `job-3-20260715`. For the supplied-name test, assert `"  Nightly Job  "` becomes `"Nightly Job"`.

- [ ] **Step 2: Verify RED**

Run:

```bash
dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj --filter "FullyQualifiedName~JobAppServiceTests"
```

Expected: failures because blank names remain blank and supplied names are not trimmed.

- [ ] **Step 3: Implement server-side name resolution**

Add a private async method:

```csharp
private async Task<string> ResolveNameAsync(string? requestedName, DateTimeOffset now)
{
    if (!string.IsNullOrWhiteSpace(requestedName))
    {
        return requestedName.Trim();
    }

    var count = await _jobTaskRepository.Queryable.CountAsync();
    return $"job-{count + 1}-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
}
```

Use it in Create and Update before persisting. Keep all other entity assignments unchanged.

- [ ] **Step 4: Verify GREEN**

Run the filtered tests and then the full `Agw.Jobs.Tests` project; expect all tests to pass.

### Task 4: Verify the complete change

**Files:**
- Verify all files from Tasks 1–3.

**Interfaces:**
- Consumes: completed frontend and backend behavior.
- Produces: verification evidence without additional scope changes.

- [ ] **Step 1: Run focused frontend tests**

Run AgentSelector, Jobs page date/time, and new dialog-mode tests. Expect all selected tests to pass.

- [ ] **Step 2: Run frontend lint and format checks**

Run changed-file `oxlint`, `pnpm lint`, and `pnpm format:check`. Require zero errors; report unrelated warnings.

- [ ] **Step 3: Run backend tests and builds**

Run `dotnet test tests/Agw.Jobs.Tests/Agw.Jobs.Tests.csproj` and `dotnet build src/server/Agw.Jobs/Agw.Jobs.csproj`. Expect both to pass.

- [ ] **Step 4: Run frontend production build**

Run `pnpm build`. Report the actual result and distinguish existing unrelated TypeScript failures from changed-file diagnostics.

- [ ] **Step 5: Review the diff and browser behavior**

Run `git diff --check`, inspect staged and unstaged changes separately, and verify the dialog interactively against the supplied screenshots when authentication permits.
