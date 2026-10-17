import assert from "node:assert/strict";
import test from "node:test";

import { BUILT_IN_VIEWS } from "./workspace-ids";
import { DEFAULT_WORKSPACE_LAYOUT, WorkspaceLayoutStore } from "./workspace-layout-store";
import { type WorkspaceView, WorkspaceRegistry } from "./workspace-registry";

function view(
  id: string,
  location: "sidebar" | "main",
  extensionId: string | null = null,
): WorkspaceView {
  return {
    id,
    location,
    title: id,
    icon: { kind: "lucide", name: "Box" },
    extensionId,
    extensionName: extensionId,
    entry: extensionId ? "index.html" : null,
    retainContextWhenHidden: false,
    failure: null,
  };
}

function createStore() {
  const registry = new WorkspaceRegistry({
    views: [
      view(BUILT_IN_VIEWS.conversations, "sidebar"),
      view(BUILT_IN_VIEWS.files, "sidebar"),
      view(BUILT_IN_VIEWS.chat, "main"),
      view(BUILT_IN_VIEWS.file, "main"),
    ],
    entries: {},
  });
  registry.registerExtension("sample", {
    views: [view("sample:list", "sidebar", "sample"), view("sample:app", "main", "sample")],
    entries: {},
  });
  return { registry, layout: new WorkspaceLayoutStore(registry) };
}

test("the default layout shows the conversation list and the chat", () => {
  const { layout } = createStore();
  assert.deepEqual(layout.getSnapshot(), DEFAULT_WORKSPACE_LAYOUT);
  assert.equal(layout.getServerSnapshot(), DEFAULT_WORKSPACE_LAYOUT);
});

test("toggling the shown sidebar view hides the sidebar; another view shows and switches", () => {
  const { layout } = createStore();
  let notifications = 0;
  layout.subscribe(() => (notifications += 1));

  layout.toggleSidebarView(BUILT_IN_VIEWS.conversations);
  assert.equal(layout.getSnapshot().sidebarVisible, false);

  layout.toggleSidebarView(BUILT_IN_VIEWS.files);
  assert.equal(layout.getSnapshot().sidebarVisible, true);
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.files);

  layout.toggleSidebarView(BUILT_IN_VIEWS.conversations);
  assert.equal(layout.getSnapshot().sidebarVisible, true);
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.conversations);

  layout.setSidebarVisible(false);
  layout.setSidebarVisible(false);
  assert.equal(notifications, 4);
});

test("opening a main view switches it, records the params and navigates to the chat route", () => {
  const { layout } = createStore();
  let navigations = 0;
  layout.setNavigateToChat(() => (navigations += 1));

  layout.openMainView("sample:app", { message: "hi" });
  const first = layout.getSnapshot().openRequests["sample:app"];
  assert.equal(layout.getSnapshot().activeMainViewId, "sample:app");
  assert.deepEqual(first.params, { message: "hi" });

  layout.openMainView("sample:app", { message: "again" });
  const second = layout.getSnapshot().openRequests["sample:app"];
  assert.ok(second.sequence > first.sequence);
  assert.deepEqual(second.params, { message: "again" });
  assert.equal(navigations, 2);
});

test("unknown views or views in the wrong area are rejected", () => {
  const { layout } = createStore();
  assert.throws(() => layout.openMainView("missing:app"), { code: "ERR_UNKNOWN_VIEW" });
  assert.throws(() => layout.openMainView("sample:list"), { code: "ERR_UNKNOWN_VIEW" });
  assert.throws(() => layout.toggleSidebarView("sample:app"), { code: "ERR_UNKNOWN_VIEW" });
});

test("an unregistered current view falls back to the default built-in view", () => {
  const { registry, layout } = createStore();
  layout.showSidebarView("sample:list");
  layout.openMainView("sample:app");
  registry.unregisterExtension("sample");
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.conversations);
  assert.equal(layout.getSnapshot().activeMainViewId, BUILT_IN_VIEWS.chat);
});
