# Agentflow Handles and Desktop Fullscreen Dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enlarge Agentflow connection handles and give every shared fullscreen Dialog a platform-aware Desktop titlebar safety inset.

**Architecture:** Keep the Handle change inside the shared Agentflow `DagNode`, with handles outside the clipped node surface but inside its positioning wrapper. Add a `fullscreen` variant to the shared Dialog primitive, expose Desktop/platform state on the document root, and apply native-control insets to the standard header through component CSS; migrate Agentflow to that shared variant.

**Tech Stack:** Next.js 16, React 19, TypeScript, Tailwind CSS 4, Radix Dialog, React Flow, Node test runner.

## Global Constraints

- Work only in `/Users/ben/source/repos/agw/.worktrees/agw-desktop-v1` on `codex/agw-desktop-v1`.
- Preserve unrelated staged and unstaged changes.
- Do not create a Git commit unless the user explicitly asks.
- Handles are exactly `20×20px` with a `3px` contrasting border, enforced independently of React Flow stylesheet order.
- Handles remain on their existing React Flow anchors, outside the clipped `Card`, and above its border.
- macOS fullscreen Dialog headers reserve `76px` on the left.
- Windows and Linux fullscreen Dialog headers reserve `146px` on the right.
- Web and non-fullscreen Dialog spacing remains unchanged.
- The known pre-existing Agentflow summary source-test failure remains outside this plan.

---

### Task 1: Enlarge Agentflow connection handles

**Files:**
- Create: `src/clients/web/src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts`
- Modify: `src/clients/web/src/app/(app)/(agents)/agentflows/components/visual-agentflow-builder.tsx`

**Interfaces:**
- Consumes: React Flow `Handle` and Tailwind utility classes already used by `DagNode`.
- Produces: target and source handles with a `20×20px` visible and interactive area.

- [ ] **Step 1: Write the failing Handle size test**

```ts
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const BUILDER_URL = new URL("./visual-agentflow-builder.tsx", import.meta.url);

test("Agentflow node connection handles use the larger shared size", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(
    source,
    /type="target"[\s\S]*?className="h-5 w-5 border-\[3px\] border-background !bg-sky-600"/,
  );
  assert.match(
    source,
    /type="source"[\s\S]*?className="h-5 w-5 border-\[3px\] border-background !bg-emerald-600"/,
  );
});
```

- [ ] **Step 2: Run the test and verify RED**

Run from `src/clients/web`:

```bash
node --test --test-name-pattern="larger shared size" 'src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts'
```

Expected: FAIL because both handles still use `h-3 w-3 border-2`.

- [ ] **Step 3: Apply the minimal Handle change**

Change both Handle class strings in `DagNode`:

```tsx
className="h-5 w-5 border-[3px] border-background !bg-sky-600"
```

```tsx
className="h-5 w-5 border-[3px] border-background !bg-emerald-600"
```

- [ ] **Step 4: Run the test and verify GREEN**

Run the Step 2 command again.

Expected: 1 test passes, 0 failures.

---

### Task 2: Create the shared Desktop-safe fullscreen Dialog contract

**Files:**
- Create: `src/clients/web/src/components/ui/fullscreen-dialog.test.ts`
- Modify: `src/clients/web/src/components/ui/dialog.tsx`
- Modify: `src/clients/web/src/lib/desktop-runtime.tsx`
- Modify: `src/clients/web/src/app/globals.css`
- Modify: `src/clients/web/src/app/(app)/(agents)/agentflows/components/visual-agentflow-dialog.tsx`
- Modify: `src/clients/web/src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts`

**Interfaces:**
- Produces: `DialogContent size="fullscreen"`.
- Produces: document-root `data-agw-desktop` and `data-agw-platform` attributes.
- Consumes: `data-slot="dialog-content"`, `data-size="fullscreen"`, and `data-slot="dialog-header"` for shared CSS targeting.

- [ ] **Step 1: Add failing shared-contract tests**

Create `fullscreen-dialog.test.ts` with separate assertions for the shared variant, Desktop root markers, and platform CSS:

```ts
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const DIALOG_URL = new URL("./dialog.tsx", import.meta.url);
const RUNTIME_URL = new URL("../../lib/desktop-runtime.tsx", import.meta.url);
const GLOBALS_URL = new URL("../../app/globals.css", import.meta.url);

test("Dialog exposes a true fullscreen size", async () => {
  const source = await readFile(DIALOG_URL, "utf8");
  assert.match(source, /fullscreen:\s*"[^"]*top-0 left-0[^"]*h-screen[^"]*w-screen/);
});

test("Desktop runtime exposes renderer and platform markers on the document root", async () => {
  const source = await readFile(RUNTIME_URL, "utf8");
  assert.match(source, /document\.documentElement/);
  assert.match(source, /root\.dataset\.agwDesktop = String\(isDesktop\)/);
  assert.match(source, /root\.dataset\.agwPlatform = platform/);
  assert.match(source, /delete root\.dataset\.agwDesktop/);
  assert.match(source, /delete root\.dataset\.agwPlatform/);
});

test("Desktop fullscreen Dialog headers reserve native window-control space", async () => {
  const source = await readFile(GLOBALS_URL, "utf8");
  assert.match(source, /data-agw-desktop="true"/);
  assert.match(source, /data-agw-platform="darwin"/);
  assert.match(source, /data-size="fullscreen"/);
  assert.match(source, /padding-left: 76px/);
  assert.match(source, /data-agw-platform="win32"/);
  assert.match(source, /data-agw-platform="linux"/);
  assert.match(source, /padding-right: 146px/);
});
```

Append this test to `agentflow-editor-ui.test.ts`:

```ts
const DIALOG_URL = new URL("./visual-agentflow-dialog.tsx", import.meta.url);

test("Agentflow uses the shared fullscreen Dialog contract", async () => {
  const source = await readFile(DIALOG_URL, "utf8");
  assert.match(source, /<DialogContent\s+size="fullscreen"/);
  assert.doesNotMatch(source, /fixed inset-0 w-screen h-screen/);
});
```

- [ ] **Step 2: Run the tests and verify RED**

Run from `src/clients/web`:

```bash
node --test src/components/ui/fullscreen-dialog.test.ts 'src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts'
```

Expected: Handle test passes; four fullscreen contract tests fail because the shared variant, root markers, CSS, and Agentflow opt-in do not exist.

- [ ] **Step 3: Add the shared `fullscreen` Dialog size**

Add this `size` variant in `dialog.tsx`:

```ts
fullscreen:
  "top-0 left-0 flex h-screen max-h-none w-screen max-w-none translate-x-0 translate-y-0 flex-col rounded-none border-0 p-4 sm:max-w-none",
```

Keep the existing `data-size={size}` attribute so shared CSS can identify the variant.

- [ ] **Step 4: Expose Desktop platform attributes at the Portal root**

Add this effect in `DesktopRuntimeProvider` after the runtime state declarations:

```ts
const platform = runtimeState?.platform ?? "browser";

React.useEffect(() => {
  const root = document.documentElement;
  root.dataset.agwDesktop = String(isDesktop);
  root.dataset.agwPlatform = platform;

  return () => {
    delete root.dataset.agwDesktop;
    delete root.dataset.agwPlatform;
  };
}, [isDesktop, platform]);
```

- [ ] **Step 5: Add platform-specific fullscreen header insets**

Add these rules to the Desktop component layer in `globals.css`:

```css
:root[data-agw-desktop="true"][data-agw-platform="darwin"]
  [data-slot="dialog-content"][data-size="fullscreen"]
  [data-slot="dialog-header"] {
  padding-left: 76px;
}

:root[data-agw-desktop="true"][data-agw-platform="win32"]
    [data-slot="dialog-content"][data-size="fullscreen"]
    [data-slot="dialog-header"],
:root[data-agw-desktop="true"][data-agw-platform="linux"]
    [data-slot="dialog-content"][data-size="fullscreen"]
    [data-slot="dialog-header"] {
  padding-right: 146px;
}
```

- [ ] **Step 6: Migrate Agentflow to the shared size**

Replace its one-off full-viewport classes with:

```tsx
<DialogContent
  size="fullscreen"
  onInteractOutside={(event) => event.preventDefault()}
  onPointerDownOutside={(event) => event.preventDefault()}
  showCloseButton={false}
>
```

- [ ] **Step 7: Run the tests and verify GREEN**

Run the Step 2 command again.

Expected: 5 tests pass, 0 failures.

---

### Task 3: Verify both clients without expanding scope

**Files:**
- Review only: all files changed by Tasks 1 and 2.

**Interfaces:**
- Consumes: Web static export produced by `NEXT_OUTPUT_MODE=export`.
- Produces: Desktop renderer resources containing the updated shared Web components.

- [ ] **Step 1: Run focused feature tests**

```bash
node --test src/components/ui/fullscreen-dialog.test.ts 'src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts'
```

Expected: 5 tests pass, 0 failures.

- [ ] **Step 2: Run relevant unaffected Agentflow tests separately**

```bash
node --test 'src/app/(app)/(agents)/agentflows/components/agentflow-input-node.test.ts' 'src/app/(app)/(agents)/agentflows/components/block-membership.test.ts' 'src/app/(app)/(agents)/agentflows/components/mermaid-viewport.test.ts'
```

Expected: 16 tests pass, 0 failures. Do not include the known drifting summary source test in this assertion.

- [ ] **Step 3: Run Web lint and build**

```bash
pnpm lint
pnpm build
```

Expected: lint has no new errors; build exits 0.

- [ ] **Step 4: Build the Desktop renderer**

Run from `src/clients/desktop`:

```bash
pnpm build
pnpm prepare:renderer
```

Expected: TypeScript build exits 0 and the Next static export is copied into Desktop resources.

- [ ] **Step 5: Inspect the surgical diff**

```bash
git diff --check
git diff -- src/clients/web/src/components/ui/dialog.tsx src/clients/web/src/lib/desktop-runtime.tsx src/clients/web/src/app/globals.css 'src/clients/web/src/app/(app)/(agents)/agentflows/components/visual-agentflow-dialog.tsx' 'src/clients/web/src/app/(app)/(agents)/agentflows/components/visual-agentflow-builder.tsx'
```

Expected: no whitespace errors and every production line traces to one of the two requested fixes.

- [ ] **Step 6: Stop without committing**

Report the changed files, RED→GREEN evidence, verification results, and the known unrelated baseline failure. Do not stage, commit, push, or clean up the worktree.

---

### Task 4: Keep enlarged handles visible above the node border

**Files:**
- Modify: `src/clients/web/src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts`
- Modify: `src/clients/web/src/app/(app)/(agents)/agentflows/components/visual-agentflow-builder.tsx`

**Interfaces:**
- Consumes: the existing `20×20px` React Flow handles and clipped `Card` node surface.
- Produces: a `relative w-[220px]` node wrapper with both handles as explicitly sized and stacked siblings of the `overflow-hidden` Card.

- [ ] **Step 1: Add the failing clipping-boundary regression test**

Add this test after the Handle size test:

```ts
test("Agentflow node handles render outside the clipped card surface", async () => {
  const source = await readFile(BUILDER_URL, "utf8");
  const dagNodeSource = source.slice(source.indexOf("function DagNode"), source.indexOf("function BlockParticipantSummary"));
  const targetHandleIndex = dagNodeSource.indexOf('type="target"');
  const cardIndex = dagNodeSource.indexOf("<Card");
  const cardEndIndex = dagNodeSource.lastIndexOf("</Card>");
  const sourceHandleIndex = dagNodeSource.indexOf('type="source"');

  assert.match(dagNodeSource, /<div className="relative w-\[220px\]">/);
  assert.ok(targetHandleIndex < cardIndex);
  assert.ok(sourceHandleIndex > cardEndIndex);
  assert.match(dagNodeSource, /<Card[\s\S]*?overflow-hidden/);
});
```

Update the Handle size test so it requires a shared runtime style unaffected by React Flow's later stylesheet:

```ts
assert.match(
  source,
  /const HANDLE_STYLE: React\.CSSProperties = \{[\s\S]*?width: 20,[\s\S]*?height: 20,[\s\S]*?zIndex: 10,[\s\S]*?borderWidth: 3,[\s\S]*?borderColor: "var\(--background\)",[\s\S]*?\};/,
);
assert.match(
  source,
  /type="target"[\s\S]*?className="!bg-sky-600"[\s\S]*?style=\{HANDLE_STYLE\}/,
);
assert.match(
  source,
  /type="source"[\s\S]*?className="!bg-emerald-600"[\s\S]*?style=\{HANDLE_STYLE\}/,
);
```

- [ ] **Step 2: Run the focused test and verify RED**

Run from `src/clients/web`:

```bash
node --test --test-name-pattern="outside the clipped card surface" 'src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts'
```

Expected: FAIL because both handles are currently descendants of the `overflow-hidden` Card and do not have `z-10`.

- [ ] **Step 3: Move the handles outside the clipped Card**

Define the shared runtime style before `DagNode`:

```tsx
const HANDLE_STYLE: React.CSSProperties = {
  width: 20,
  height: 20,
  zIndex: 10,
  borderWidth: 3,
  borderColor: "var(--background)",
};
```

Replace the start of the `DagNode` return value with a positioning wrapper, a target Handle, and a full-width clipped Card:

```tsx
<div className="relative w-[220px]">
  {!isInput && showHandles ? (
    <Handle
      type="target"
      position={Position.Left}
      className="!bg-sky-600"
      style={HANDLE_STYLE}
    />
  ) : null}
  <Card
    className={`relative w-full gap-0 overflow-hidden rounded-md border-2 p-0 shadow-sm transition-shadow ${
      selected
        ? "border-primary shadow-md"
        : hasBlockWarning
          ? "border-amber-400 shadow-amber-100"
          : "border-border"
    }`}
  >
```

Delete the old target Handle from inside the Card. Replace the existing source Handle and Card close with:

```tsx
  </Card>
  {showHandles ? (
    <Handle
      type="source"
      position={Position.Right}
      className="!bg-emerald-600"
      style={HANDLE_STYLE}
    />
  ) : null}
</div>
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

```bash
node --test 'src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts'
```

Expected: 3 tests pass, 0 failures.

- [ ] **Step 5: Run full relevant verification**

Run from `src/clients/web`:

```bash
node --test src/components/ui/fullscreen-dialog.test.ts 'src/app/(app)/(agents)/agentflows/components/agentflow-editor-ui.test.ts'
node --test 'src/app/(app)/(agents)/agentflows/components/agentflow-input-node.test.ts' 'src/app/(app)/(agents)/agentflows/components/block-membership.test.ts' 'src/app/(app)/(agents)/agentflows/components/mermaid-viewport.test.ts'
pnpm lint
pnpm build
```

Run from `src/clients/desktop`:

```bash
pnpm build
pnpm prepare:renderer
```

Run from the repository worktree root:

```bash
git diff --check
git diff -- src/clients/web/src/app/\(app\)/\(agents\)/agentflows/components/agentflow-editor-ui.test.ts src/clients/web/src/app/\(app\)/\(agents\)/agentflows/components/visual-agentflow-builder.tsx
```

Expected: all focused tests and builds exit 0, lint reports no new errors, and the diff contains only the Handle clipping correction. Reload the open Electron Agentflow editor and confirm both circles render fully above the node border at normal zoom.
