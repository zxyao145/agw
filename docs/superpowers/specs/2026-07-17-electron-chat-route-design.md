# Electron Chat Route Design

## Goal

Give Electron a dedicated Chat route and composition while preserving the existing Web Chat page. Project selection moves into the Electron title bar: clicking the existing `agw-titlebar-button` opens a searchable Project picker, while the Project selector inside the Chat content remains visible only on the Web route.

## Route Boundary

- Web Chat remains `/chat/`.
- Electron Chat uses `/desktop/chat/`.
- Electron startup, tray actions, title-bar links, Project tabs, background-task links, and Settings “Back to chat” navigate to `/desktop/chat/`.
- If Electron reaches the legacy `/chat/` route, it replaces that URL with `/desktop/chat/` while preserving `projectId`, `contextId`, and other query parameters.
- If a normal browser reaches `/desktop/chat/`, it replaces that URL with `/chat/` while preserving query parameters.
- Redirect boundaries render a neutral loading state during replacement so the wrong Chat composition does not flash.

The route remains part of the Next.js static export. Electron continues to load the renderer through `agw://app`; no second `BrowserWindow` or independent frontend bundle is introduced.

## Shared Chat Workspace

Extract the current Chat page implementation into a shared `ChatWorkspace` component. The two route pages become thin composition boundaries:

```ts
type ChatWorkspaceProps = {
  routeBasePath: "/chat" | "/desktop/chat";
  showProjectSelect: boolean;
};
```

`ChatWorkspace` keeps the existing ownership of:

- Project, Agent, and Agentflow queries.
- Selected Project, target, context, and tab state.
- Conversation history and context hydration.
- Chat execution and background-task continuity.
- Files, file preview, and file-explorer state.
- Project-specific settings and environment variables.

All URL construction and route synchronization use `routeBasePath`; the extracted implementation must not retain hard-coded `/chat` navigation.

The Web page renders:

```tsx
<ChatWorkspace routeBasePath="/chat" showProjectSelect />
```

The Electron page renders:

```tsx
<ChatWorkspace routeBasePath="/desktop/chat" showProjectSelect={false} />
```

The `showProjectSelect` flag controls React composition around `#chat-project-select`. The Electron implementation must not hide it through XPath, CSS selectors, or DOM manipulation. Agent selection, Chat/Files tabs, and the sidebar toggle remain in the Chat content toolbar.

## Electron Project Picker

Create a Desktop-specific `DesktopProjectPicker` component. It receives Project options, the active Project ID, loading/error state, and an `onSelect(projectId)` callback. It owns only popover visibility, search text, option filtering, and keyboard-accessible selection.

The existing `agw-titlebar-button` with the plus icon becomes the picker trigger. Selecting a Project performs this flow in `ChatShell`:

1. Normalize the current open Project tabs.
2. If the Project is not open, append it and persist the tab list under the active Server ID.
3. If it is already open, keep the existing tab order.
4. Navigate to `/desktop/chat/?projectId=<id>`.
5. Close the picker.

`default-built-in` remains the mandatory first/default tab. Selecting it activates the existing tab rather than creating a duplicate.

The picker must show Project name and optional workspace, support search, expose an accessible “Open project” label, and display explicit loading, empty, and query-error states. It must not navigate to Project management; Project management remains available in Settings.

## State and Data Flow

The `projectId` query parameter is the synchronization boundary between the title bar and `ChatWorkspace`:

```text
DesktopProjectPicker
  -> persist/open Project tab
  -> update /desktop/chat/?projectId=...
  -> ChatWorkspace observes projectId
  -> reset or hydrate the selected Project session
```

React Query continues to cache the Project query. `ChatShell` and `ChatWorkspace` may subscribe to the same query key without creating separate data ownership or duplicate steady-state requests.

Project execution state remains keyed by Server, Project, and conversation. Switching Project tabs changes only the visible workspace; existing background executions continue and retain their title-bar status indicators.

## Web Isolation

The normal Web `/chat/` route retains:

- Its existing application sidebar behavior.
- The inline `#chat-project-select`.
- Existing `/chat` URL and context synchronization.
- Existing Project-management and Job-log links.

Desktop-only route selection must depend on `window.agwDesktop` through `DesktopRuntimeProvider`. No Electron title bar or Desktop Project picker is rendered in a normal browser.

## Error Handling

- A failed Project query leaves existing open tabs visible and shows the error inside the Project picker.
- An unknown `projectId` falls back through the existing Project normalization logic to `default-built-in` when available.
- Route replacement preserves the query string and does not create a browser-history loop.
- Selecting the current Project is idempotent.
- Closing a running Project tab retains the existing confirmation and background-execution behavior.

## Verification

- Add a failing route-contract test before extracting `ChatWorkspace`.
- Verify Web `/chat/` renders `#chat-project-select` and does not render the Desktop Project picker.
- Verify Electron `/desktop/chat/` renders the Desktop Project picker and does not render `#chat-project-select`.
- Verify the title-bar plus button opens the searchable picker.
- Verify selecting an unopened Project appends and persists one tab, then navigates to the matching Desktop Chat URL.
- Verify selecting an open Project activates it without duplication.
- Verify Settings “Back to chat”, tray Open Chat, and initial Electron startup use `/desktop/chat/` without a 404.
- Verify legacy Electron `/chat/` and browser `/desktop/chat/` redirects preserve query parameters.
- Run focused tests, Web lint and production static export, Desktop tests/build/lint, and a real Electron CDP navigation smoke test.

## Non-goals

- Creating another Electron `BrowserWindow`.
- Duplicating the Chat execution or file-management implementation.
- Removing the Web Project selector.
- Moving Project management out of Settings.
- Changing Project tab background-execution semantics.
