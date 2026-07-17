# Desktop Chat Compact Toolbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render the Desktop Chat Agent Select and Chat/Files tabs one control size smaller without changing Web Chat.

**Architecture:** Add an optional small-size prop through the existing `AgentSelector` and `SearchableSelect` boundary. Enable an explicit compact toolbar mode only from the Desktop Chat route and apply local utility classes to its tab controls instead of changing shared Tabs primitives globally.

**Tech Stack:** Next.js 16, React 19, TypeScript, Tailwind CSS 4, Radix UI, Node test runner, oxlint, oxfmt.

## Global Constraints

- Desktop Agent Select height is `h-8` instead of `h-9`.
- Desktop Chat/Files tabs use an `h-8` list with proportionally smaller trigger text and padding.
- The sidebar visibility button stays unchanged.
- Web `/chat` keeps the current default sizes.
- No unrelated refactoring and no automatic Git commit.

---

### Task 1: Small Select Size Propagation

**Files:**
- Modify: `src/clients/web/src/components/SearchableSelect/searchable-select.test.ts`
- Modify: `src/clients/web/src/components/SearchableSelect/searchable-select.tsx`
- Modify: `src/clients/web/src/components/agent-selector.tsx`
- Test: `src/clients/web/src/components/SearchableSelect/searchable-select.test.ts`

**Interfaces:**
- Produces: `SearchableSelectBaseProps.size?: "default" | "sm"`.
- Produces: `AgentSelectorProps.size?: "default" | "sm"`.
- Defaults: both components retain `"default"` when the prop is omitted.

- [x] **Step 1: Write the failing source contract test**

Add assertions that `SearchableSelect` accepts `size?: "default" | "sm"`, forwards `size={size}` to its Button, and that `AgentSelector` accepts and forwards the same prop.

- [x] **Step 2: Run the test and verify RED**

Run: `node --test src/components/SearchableSelect/searchable-select.test.ts`

Expected: FAIL because neither component exposes or forwards `size`.

- [x] **Step 3: Implement the minimal prop forwarding**

Add `size?: "default" | "sm"` to both prop types. Default it to `"default"` and pass it from `AgentSelector` to `SearchableSelect`, then from `SearchableSelect` to its Button trigger.

- [x] **Step 4: Run the test and verify GREEN**

Run: `node --test src/components/SearchableSelect/searchable-select.test.ts`

Expected: all SearchableSelect contract tests pass.

### Task 2: Desktop-Only Compact Chat Toolbar

**Files:**
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/page.test.ts`
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/chat-workspace.tsx`
- Modify: `src/clients/web/src/app/(app)/desktop/chat/page.tsx`
- Test: `src/clients/web/src/app/(app)/(interface)/chat/page.test.ts`

**Interfaces:**
- Consumes: `AgentSelectorProps.size?: "default" | "sm"` from Task 1.
- Produces: `ChatWorkspaceProps.compactToolbar?: boolean`.
- Desktop route passes `compactToolbar`; Web route omits it.

- [x] **Step 1: Write the failing Desktop scope test**

Add assertions that the Desktop route renders `<ChatWorkspace ... compactToolbar />`, the Web route does not, and `ChatWorkspace` uses compact mode to pass `size="sm"` to `AgentSelector` and compact classes to `TabsList` and `TabsTrigger`.

- [x] **Step 2: Run the test and verify RED**

Run: `node --test 'src/app/(app)/(interface)/chat/page.test.ts'`

Expected: FAIL because `compactToolbar` does not exist.

- [x] **Step 3: Implement Desktop compact mode**

Add the optional prop to `ChatWorkspace`. When true, pass `size="sm"` to `AgentSelector`, apply `h-8 p-0.5` to `TabsList`, and apply compact trigger classes such as `px-2.5 py-0.5 text-xs` to both `TabsTrigger` controls. Pass the prop only from `/desktop/chat/page.tsx`.

- [x] **Step 4: Run focused tests and verify GREEN**

Run: `node --test src/components/SearchableSelect/searchable-select.test.ts 'src/app/(app)/(interface)/chat/page.test.ts'`

Expected: all tests pass.

- [x] **Step 5: Verify formatting, lint, and renderer build**

Run: `pnpm exec oxfmt --check src/components/SearchableSelect/searchable-select.tsx src/components/SearchableSelect/searchable-select.test.ts src/components/agent-selector.tsx 'src/app/(app)/(interface)/chat/chat-workspace.tsx' 'src/app/(app)/(interface)/chat/page.test.ts' 'src/app/(app)/desktop/chat/page.tsx'`

Run: `pnpm lint`

Run from `src/clients/desktop`: `pnpm prepare:renderer`

Expected: formatting passes, lint has zero errors, and the static renderer build succeeds.

- [x] **Step 6: Verify in Electron**

Reload the running Electron window. Confirm the Agent Select and Chat/Files tabs render at the same compact `h-8` height, the sidebar button remains unchanged, and the toolbar behavior still works.
