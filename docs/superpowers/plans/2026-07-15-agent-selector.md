# AgentSelector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the Chat target selector into a reusable `AgentSelector` that returns `{ agentType, agentId }` for Agents and Agentflows.

**Architecture:** `AgentSelector` owns the Agent and Agentflow React Query reads, builds options with the existing chat-target helpers, and renders `SearchableSelect`. Chat remains responsible for session, URL, persistence, and execution state, and converts the structured callback back into its existing encoded target value.

**Tech Stack:** React 19, TypeScript, TanStack React Query, existing `apiGet`, `SearchableSelect`, and `chat-target-options` utilities.

## Global Constraints

- `agentType: 0` represents an Agent and `agentType: 1` represents an Agentflow.
- Preserve existing project restrictions, enabled-Agentflow filtering, sorting, labels, loading state, and errors.
- Keep Chat session and route behavior unchanged.
- Do not create a Git commit unless the user explicitly requests one.

---

### Task 1: Add the reusable AgentSelector

**Files:**
- Create: `src/clients/web/src/components/agent-selector.test.ts`
- Create: `src/clients/web/src/components/agent-selector.tsx`

**Interfaces:**
- Consumes: `apiGet`, `buildChatTargetOptions`, `getTargetValue`, `parseTargetValue`, `SearchableSelect`.
- Produces: `AgentSelection`, `AgentSelectorProps`, and `AgentSelector`.

- [ ] **Step 1: Write the failing component contract test**

```ts
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const COMPONENT_URL = new URL("./agent-selector.tsx", import.meta.url);

test("AgentSelector loads agents and agentflows and returns a structured selection", async () => {
  const source = await readFile(COMPONENT_URL, "utf8").catch(() => "");

  assert.match(source, /export type AgentSelection =/);
  assert.match(source, /agentType: 0 \| 1;/);
  assert.match(source, /agentId: string;/);
  assert.match(source, /queryKey: \["agents"\]/);
  assert.match(source, /queryKey: \["agentflows"\]/);
  assert.match(source, /buildChatTargetOptions/);
  assert.match(source, /onSelect\(\{[\s\S]*agentType:[\s\S]*agentId:/);
});
```

- [ ] **Step 2: Run the test and verify the red state**

Run:

```bash
cd src/clients/web
node --experimental-strip-types --test src/components/agent-selector.test.ts
```

Expected: one failed test because `agent-selector.tsx` is missing.

- [ ] **Step 3: Implement the component**

Create a client component with the public API:

```ts
export type AgentSelection = {
  agentType: 0 | 1;
  agentId: string;
};

export type AgentSelectorProps = {
  id: string;
  projectId?: string | null;
  value?: AgentSelection | null;
  onSelect: (selection: AgentSelection) => void;
};
```

The implementation must:

```ts
const agentsQuery = useQuery({
  queryKey: ["agents"],
  queryFn: async () => (await apiGet("/api/agents")) as AgentDto[],
});
const agentflowsQuery = useQuery({
  queryKey: ["agentflows"],
  queryFn: async () => (await apiGet("/api/agentflows")) as AgentflowDto[],
});
```

Build grouped `SearchableSelectOption` values with `buildChatTargetOptions`, encode the controlled value with `getTargetValue`, and decode changes with `parseTargetValue`. Invoke:

```ts
onSelect({
  agentType: target.type === "agent" ? 0 : 1,
  agentId: target.id,
});
```

Pass query loading and `getApiErrorMessage(agentsQuery.error ?? agentflowsQuery.error)` to `SearchableSelect`, and keep `clearable={false}`.

- [ ] **Step 4: Run the focused test and verify green**

Run:

```bash
cd src/clients/web
node --experimental-strip-types --test src/components/agent-selector.test.ts
```

Expected: one passed test and zero failures.

### Task 2: Replace the Chat target selector

**Files:**
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/page.tsx:29-62,1035-1060,1340-1420`
- Create: `src/clients/web/src/app/(app)/(interface)/chat/agent-selector-usage.test.ts`

**Interfaces:**
- Consumes: `AgentSelector`, `AgentSelection`, and the existing `handleTargetChange` callback.
- Produces: unchanged Chat selection, persistence, routing, and execution behavior using the reusable selector.

- [ ] **Step 1: Write the failing Chat integration test**

```ts
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const PAGE_URL = new URL("./page.tsx", import.meta.url);

test("Chat uses AgentSelector and maps its structured selection to the existing target value", async () => {
  const source = await readFile(PAGE_URL, "utf8");

  assert.match(source, /import \{ AgentSelector/);
  assert.match(source, /<AgentSelector/);
  assert.match(source, /projectId=\{selectedProjectId\}/);
  assert.match(source, /onSelect=\{handleAgentSelect\}/);
  assert.doesNotMatch(source, /id="chat-target-select"[\s\S]{0,120}onValueChange=/);
});
```

- [ ] **Step 2: Run the test and verify the red state**

Run:

```bash
cd src/clients/web
node --experimental-strip-types --test 'src/app/(app)/(interface)/chat/agent-selector-usage.test.ts'
```

Expected: one failed test because Chat still renders `SearchableSelect` for `chat-target-select`.

- [ ] **Step 3: Integrate AgentSelector**

Import `AgentSelector` and `AgentSelection`. Add a memoized handler:

```ts
const handleAgentSelect = React.useCallback(
  ({ agentType, agentId }: AgentSelection) => {
    handleTargetChange(
      getTargetValue({
        id: agentId,
        type: agentType === 0 ? "agent" : "agentflow",
      }),
    );
  },
  [handleTargetChange],
);
```

Replace the target selector with:

```tsx
<AgentSelector
  id="chat-target-select"
  projectId={selectedProjectId}
  value={
    selectedTarget
      ? {
          agentType: selectedTarget.type === "agent" ? 0 : 1,
          agentId: selectedTarget.id,
        }
      : null
  }
  onSelect={handleAgentSelect}
/>
```

Remove only the now-unused `targetSelectOptions` memo. Keep `SearchableSelect` and `SearchableSelectOption` for the project selector.

- [ ] **Step 4: Run both focused tests**

Run:

```bash
cd src/clients/web
node --experimental-strip-types --test \
  src/components/agent-selector.test.ts \
  'src/app/(app)/(interface)/chat/agent-selector-usage.test.ts'
```

Expected: two passed tests and zero failures.

### Task 3: Verify the focused change

**Files:**
- Verify: all files from Tasks 1 and 2.

**Interfaces:**
- Consumes: completed `AgentSelector` extraction.
- Produces: verification evidence without additional production changes.

- [ ] **Step 1: Run related Chat target tests**

```bash
cd src/clients/web
node --experimental-strip-types --test \
  src/lib/chat-target-options.test.ts \
  src/components/agent-selector.test.ts \
  'src/app/(app)/(interface)/chat/agent-selector-usage.test.ts'
```

Expected: all tests pass.

- [ ] **Step 2: Run lint and formatting checks**

```bash
cd src/clients/web
pnpm lint
pnpm format:check
```

Expected: zero lint errors and formatting succeeds. Existing unrelated lint warnings may remain.

- [ ] **Step 3: Run the production build**

```bash
cd src/clients/web
pnpm build
```

Expected: report the actual result. If the existing `src/components/message/renders/reasoning.tsx` nullability error remains, confirm the new files introduce no additional TypeScript errors.

- [ ] **Step 4: Review the final diff**

```bash
git diff --check
git status --short
```

Expected: no whitespace errors; existing Jobs changes remain untouched; only the approved AgentSelector files, Chat integration, and planning documents are newly changed.
