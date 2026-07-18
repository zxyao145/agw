# Electron Chat Route Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Electron a dedicated `/desktop/chat/` workspace with a title-bar Project picker while preserving the Web `/chat/` composition.

**Architecture:** Keep one Next.js renderer and one shared `ChatWorkspace`. Thin route pages select Web or Desktop composition through a runtime-aware route boundary, while `AppShell` owns Desktop tabs and Project opening.

**Tech Stack:** Next.js 16 App Router, React 19, TypeScript, Radix/Shadcn primitives, Tailwind CSS 4, React Query, Electron 43, Node test runner.

## Global Constraints

- Web Chat remains `/chat/`; Electron Chat uses `/desktop/chat/`.
- `ChatWorkspace` receives `routeBasePath: "/chat" | "/desktop/chat"` and `showProjectSelect: boolean`.
- `#chat-project-select` is omitted by React composition in Electron, never hidden by CSS or DOM manipulation.
- `default-built-in` remains the mandatory first tab and Project execution continues in the background across tab switches.
- Electron startup, tray, tabs, task links, and Settings return navigation use `/desktop/chat/`.
- Legacy Electron `/chat/` and browser `/desktop/chat/` redirects preserve the complete query string.
- Do not create another `BrowserWindow`, duplicate Chat logic, change Web navigation, or create a Git commit automatically.

---

### Task 1: Shared Chat route contract

**Files:**
- Create: `src/clients/web/src/lib/chat-route.ts`
- Create: `src/clients/web/src/lib/chat-route.test.ts`
- Create: `src/clients/web/src/components/chat/chat-route-boundary.tsx`
- Create: `src/clients/web/src/app/(app)/desktop/chat/page.tsx`
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/page.tsx`

**Interfaces:**
- Produces: `type ChatRouteBasePath = "/chat" | "/desktop/chat"`.
- Produces: `buildChatHref(basePath, { projectId, contextId }): string` with a static-export-safe trailing slash.
- Produces: `getChatRouteRedirect({ isDesktop, pathname, search }): string | null`.
- Produces: `ChatRouteBoundary` that calls `router.replace` and renders a neutral status while redirecting.

- [ ] **Step 1: Write failing route tests**

```ts
test("builds Web and Desktop chat links with project and context", () => {
  assert.equal(buildChatHref("/chat", { projectId: "p", contextId: "c" }), "/chat/?projectId=p&contextId=c");
  assert.equal(buildChatHref("/desktop/chat", { projectId: "p", contextId: null }), "/desktop/chat/?projectId=p");
});

test("redirects only across the mismatched runtime boundary", () => {
  assert.equal(getChatRouteRedirect({ isDesktop: true, pathname: "/chat/", search: "?projectId=p&contextId=c" }), "/desktop/chat/?projectId=p&contextId=c");
  assert.equal(getChatRouteRedirect({ isDesktop: false, pathname: "/desktop/chat/", search: "?projectId=p" }), "/chat/?projectId=p");
  assert.equal(getChatRouteRedirect({ isDesktop: true, pathname: "/desktop/chat/", search: "" }), null);
});
```

- [ ] **Step 2: Run the focused test and observe the missing-module failure**

Run: `cd src/clients/web && node --test src/lib/chat-route.test.ts`

Expected: FAIL because `chat-route.ts` does not exist.

- [ ] **Step 3: Implement the pure route helpers and boundary**

```ts
export function buildChatHref(basePath: ChatRouteBasePath, params: ChatRouteParams): string {
  const search = new URLSearchParams();
  if (params.projectId) search.set("projectId", params.projectId);
  if (params.projectId && params.contextId) search.set("contextId", params.contextId);
  const query = search.toString();
  return `${basePath}/${query ? `?${query}` : ""}`;
}
```

The redirect helper normalizes trailing slashes, preserves the supplied search string, and returns `null` for a matching runtime. `ChatRouteBoundary` derives the current search string from `useSearchParams`, replaces only when the helper returns a target, and otherwise renders its children.

- [ ] **Step 4: Add thin Web and Desktop route pages**

```tsx
export default function ChatPage() {
  return (
    <ChatRouteBoundary>
      <ChatWorkspace routeBasePath="/chat" showProjectSelect />
    </ChatRouteBoundary>
  );
}
```

```tsx
export default function DesktopChatPage() {
  return (
    <ChatRouteBoundary>
      <ChatWorkspace routeBasePath="/desktop/chat" showProjectSelect={false} />
    </ChatRouteBoundary>
  );
}
```

- [ ] **Step 5: Run the route test**

Run: `cd src/clients/web && node --test src/lib/chat-route.test.ts`

Expected: PASS.

### Task 2: Extract the shared Chat workspace

**Files:**
- Create by moving existing implementation: `src/clients/web/src/app/(app)/(interface)/chat/chat-workspace.tsx`
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/page.tsx`
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/page.test.ts`
- Modify: `src/clients/web/src/app/(app)/(interface)/chat/agent-selector-usage.test.ts`

**Interfaces:**
- Consumes: `buildChatHref` and `ChatRouteBasePath` from Task 1.
- Produces: `ChatWorkspace({ routeBasePath, showProjectSelect })` with all existing state and query ownership.

- [ ] **Step 1: Change the source-contract test to require two thin route pages and one shared workspace**

The test reads all three source files and requires Web props, Desktop props, `showProjectSelect` composition around `#chat-project-select`, and route construction through `routeBasePath`.

- [ ] **Step 2: Run the source-contract test and observe failure**

Run: `cd src/clients/web && node --test 'src/app/(app)/(interface)/chat/page.test.ts'`

Expected: FAIL because `chat-workspace.tsx` and the Desktop route are not yet complete.

- [ ] **Step 3: Move the existing Chat implementation and parameterize it**

```tsx
export type ChatWorkspaceProps = {
  routeBasePath: ChatRouteBasePath;
  showProjectSelect: boolean;
};

export function ChatWorkspace({ routeBasePath, showProjectSelect }: ChatWorkspaceProps) {
  // Existing Chat page state, queries, execution, history, files, and settings remain here.
}
```

Replace the hard-coded Chat URL helper with `buildChatHref(routeBasePath, ...)`, include `routeBasePath` in `syncRoute` dependencies, and wrap only the Project selector block with `{showProjectSelect ? (...) : null}`. Keep Agent selection, Chat/Files tabs, and sidebar controls unchanged.

- [ ] **Step 4: Point existing source tests at the shared workspace**

Tests that validate Chat implementation details read `chat-workspace.tsx`; route composition assertions continue to read `page.tsx` and `desktop/chat/page.tsx`.

- [ ] **Step 5: Run focused Chat tests**

Run: `cd src/clients/web && node --test 'src/app/(app)/(interface)/chat/page.test.ts' 'src/app/(app)/(interface)/chat/agent-selector-usage.test.ts'`

Expected: PASS.

### Task 3: Desktop title-bar Project picker

**Files:**
- Create: `src/clients/web/src/components/desktop/desktop-project-picker.tsx`
- Create: `src/clients/web/src/components/desktop/desktop-project-picker.test.ts`
- Modify: `src/clients/web/src/components/desktop/app-shell.tsx`
- Modify: `src/clients/web/src/components/desktop/app-shell.test.ts`
- Modify: `src/clients/web/src/app/globals.css`

**Interfaces:**
- Produces: `DesktopProjectPicker({ projects, activeProjectId, isLoading, errorMessage, onSelect })`.
- Consumes: `normalizeProjectTabs(tabs, projectIds, selectedProjectId)` and `buildChatHref("/desktop/chat", ...)`.

- [ ] **Step 1: Write failing picker and shell tests**

Require the picker trigger to be a `.agw-titlebar-button` with `aria-label="Open project"`, a search input, loading/error/empty states, Project name plus optional workspace, and `onSelect`. Require all Desktop Chat links and Settings return links in `AppShell` to use `/desktop/chat/`.

- [ ] **Step 2: Run focused tests and observe failure**

Run: `cd src/clients/web && node --test src/components/desktop/desktop-project-picker.test.ts src/components/desktop/app-shell.test.ts`

Expected: FAIL because the picker does not exist and the shell still links to `/chat/`.

- [ ] **Step 3: Implement the searchable picker**

Use the existing Popover and Input primitives. Keep local `open` and `search` state, focus the input on open, match against name/workspace/id, close after selection, and render explicit `Loading projects…`, query error, `No projects available`, and `No matching projects` states. Use restrained HeroUI-style rounded surfaces, neutral borders, compact type, and a single primary selected-state accent.

- [ ] **Step 4: Wire Project selection into `ChatShell`**

```ts
const openProject = (projectId: string) => {
  persistTabs(normalizeProjectTabs(tabs, projectIds, projectId));
  router.push(buildChatHref("/desktop/chat", { projectId, contextId: null }));
};
```

Replace the plus-link with `DesktopProjectPicker`, and update brand, tab, close fallback, task, and Settings return destinations to Desktop Chat. Treat both `/chat` and `/desktop/chat` as Chat shell paths so legacy redirect never flashes Settings.

- [ ] **Step 5: Run focused tests**

Run: `cd src/clients/web && node --test src/components/desktop/desktop-project-picker.test.ts src/components/desktop/app-shell.test.ts src/lib/project-tabs.test.ts`

Expected: PASS.

### Task 4: Electron entry points and full verification

**Files:**
- Create: `src/clients/desktop/src/chat-route.test.ts`
- Modify: `src/clients/desktop/src/main.ts`

**Interfaces:**
- Consumes: static-export route `/desktop/chat/`.
- Produces: default renderer load and tray Open Chat action targeting `/desktop/chat/`.

- [ ] **Step 1: Write a failing Electron source-contract test**

```ts
test("Desktop starts and reopens on the dedicated chat route", async () => {
  const source = await readFile(MAIN_URL, "utf8");
  assert.match(source, /loadRenderer\(pathname = "\/desktop\/chat\/"\)/);
  assert.match(source, /showWindow\("\/desktop\/chat\/"\)/);
});
```

- [ ] **Step 2: Run the Desktop test and observe failure**

Run: `cd src/clients/desktop && pnpm test -- src/chat-route.test.ts`

Expected: FAIL because Electron still targets `/chat/`.

- [ ] **Step 3: Update Electron entry points**

Change the `loadRenderer` default and tray “Open Agw Chat” action to `/desktop/chat/`. Keep Settings and all daemon behavior unchanged.

- [ ] **Step 4: Run focused and full automated verification**

Run:

```bash
cd src/clients/web
node --test src/lib/chat-route.test.ts src/components/desktop/desktop-project-picker.test.ts src/components/desktop/app-shell.test.ts 'src/app/(app)/(interface)/chat/page.test.ts' 'src/app/(app)/(interface)/chat/agent-selector-usage.test.ts'
pnpm lint
pnpm build

cd ../desktop
pnpm test
pnpm lint
pnpm build
```

Expected: all tests, lint, and builds pass without new warnings.

- [ ] **Step 5: Smoke-test both exported routes**

Confirm the Web export contains `out/chat/index.html` and `out/desktop/chat/index.html`. Launch `pnpm dev` from `src/clients/desktop`, verify the title-bar picker opens, selecting a Project activates exactly one tab, the inline Project selector is absent, Settings returns to Desktop Chat, and the normal Web `/chat/` retains its inline Project selector and sidebar.
