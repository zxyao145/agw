import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";

import { BUILT_IN_VIEWS } from "./workspace-ids";
import { DEFAULT_WORKSPACE_LAYOUT, WorkspaceLayoutStore } from "./workspace-layout-store";
import { type WorkspaceView, WorkspaceRegistry } from "./workspace-registry";
import { activeTabOf } from "./workspace-tabs-store";
import { createTestTabs } from "./workspace-tabs-store.test-helper";

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
    priority: "default",
  };
}

async function createStore(t: TestContext) {
  const { conversations, files, chat, file, jobs, events, notifications } = BUILT_IN_VIEWS;
  const registry = new WorkspaceRegistry({
    views: [
      view(conversations, "sidebar"),
      view(files, "sidebar"),
      ...[chat, file, jobs, events, notifications].map((id) => view(id, "main")),
    ],
    entries: {},
  });
  registry.registerExtension("sample", {
    views: [view("sample:list", "sidebar", "sample"), view("sample:app", "main", "sample")],
    entries: {},
  });
  const tabs = await createTestTabs(t, () => layout.leaveFullWorkspaceView());
  const layout = new WorkspaceLayoutStore(registry, tabs);
  return { registry, layout, tabs };
}

test("the default layout shows the conversation sidebar and the tabs", async (t) => {
  const { layout } = await createStore(t);
  assert.deepEqual(layout.getSnapshot(), DEFAULT_WORKSPACE_LAYOUT);
  assert.equal(layout.getServerSnapshot(), DEFAULT_WORKSPACE_LAYOUT);
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, null);
});

test("toggling the current sidebar hides it and another view shows it", async (t) => {
  const { layout, tabs } = await createStore(t);
  const original = tabs.getSnapshot().group;
  let notifications = 0;
  layout.subscribe(() => (notifications += 1));
  layout.toggleSidebarView(BUILT_IN_VIEWS.conversations);
  assert.equal(layout.getSnapshot().sidebarVisible, false);
  layout.toggleSidebarView(BUILT_IN_VIEWS.files);
  assert.equal(layout.getSnapshot().sidebarVisible, true);
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.files);
  layout.setSidebarVisible(true);
  assert.equal(notifications, 2);
  assert.equal(tabs.getSnapshot().group, original);
});

test("Events hides the sidebar and switching to Files returns to the same tabs", async (t) => {
  const { layout, tabs } = await createStore(t);
  const original = tabs.getSnapshot().group;
  layout.openMainView(BUILT_IN_VIEWS.events);
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, BUILT_IN_VIEWS.events);
  assert.equal(layout.getSnapshot().sidebarVisible, false);
  layout.showSidebarView(BUILT_IN_VIEWS.files);
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, null);
  assert.equal(layout.getSnapshot().sidebarVisible, true);
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.files);
  assert.equal(tabs.getSnapshot().group, original);
});

test("leaving full views restores the original sidebar visibility", async (t) => {
  const { layout, tabs } = await createStore(t);
  layout.setSidebarVisible(false);
  layout.openMainView(BUILT_IN_VIEWS.events);
  layout.openMainView(BUILT_IN_VIEWS.notifications, { notificationId: "n1" });
  assert.deepEqual(layout.getSnapshot().openRequests[BUILT_IN_VIEWS.notifications]?.params, {
    notificationId: "n1",
  });
  tabs.activate(tabs.getSnapshot().group!.activeTabId!);
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, null);
  assert.equal(layout.getSnapshot().sidebarVisible, false);
});

test("openMainView opens Jobs and extension views as tabs and leaves a full view", async (t) => {
  const { layout, tabs } = await createStore(t);
  layout.openMainView(BUILT_IN_VIEWS.events);
  layout.openMainView(BUILT_IN_VIEWS.jobs);
  assert.equal(layout.getSnapshot().sidebarVisible, true);
  layout.openMainView("sample:app", { a: 1 }, { key: "k", title: "K", preview: true });
  assert.equal(layout.getSnapshot().fullWorkspaceViewId, null);
  assert.deepEqual(
    tabs.getSnapshot().group!.tabs.map((tab) => tab.kind),
    ["chat", "jobs", "extension"],
  );
  assert.equal("sample:app" in layout.getSnapshot().openRequests, false);
  const tab = activeTabOf(tabs.getSnapshot().group);
  assert.ok(tab?.kind === "extension");
  assert.deepEqual(tab.params, { a: 1 });
});

test("unsupported views and built-in options are rejected", async (t) => {
  const { layout } = await createStore(t);
  assert.throws(() => layout.openMainView(BUILT_IN_VIEWS.chat), {
    code: "ERR_UNSUPPORTED_OPERATION",
  });
  assert.throws(() => layout.openMainView(BUILT_IN_VIEWS.file), {
    code: "ERR_UNSUPPORTED_OPERATION",
  });
  assert.throws(() => layout.openMainView(BUILT_IN_VIEWS.jobs, {}, { preview: true }), {
    code: "ERR_INVALID_ARGUMENT",
  });
  assert.throws(() => layout.openMainView("sample:list"), { code: "ERR_UNKNOWN_VIEW" });
  assert.throws(() => layout.toggleSidebarView("sample:app"), { code: "ERR_UNKNOWN_VIEW" });
});

test("an extension view requested from Settings opens when navigation loads the Project", async (t) => {
  const { layout, tabs } = await createStore(t);
  const scope = tabs.getSnapshot().scope!;
  await tabs.setScope(null);
  let navigating = false;
  layout.setNavigateToChat(() => {
    navigating = true;
  });
  const opening = layout.openMainView("sample:app", { name: "from settings" }, { key: "settings" });
  assert.equal(navigating, true);
  assert.equal(tabs.getSnapshot().group, null);
  await tabs.setScope(scope);
  await opening;
  const tab = activeTabOf(tabs.getSnapshot().group);
  assert.ok(tab?.kind === "extension");
  assert.equal(tab.key, "settings");
  assert.deepEqual(tab.params, { name: "from settings" });
});

test("an open requested during a group read belongs to that Project", async (t) => {
  const { layout, tabs } = await createStore(t);
  const a = { serverId: "local", projectId: "a" };
  const b = { serverId: "local", projectId: "b" };
  const loading = tabs.setScope(a);
  const opening = layout.openMainView(BUILT_IN_VIEWS.jobs);
  const rejected = assert.rejects(Promise.resolve(opening), { code: "ERR_CONNECTION_CHANGED" });
  await Promise.all([loading, tabs.setScope(b), rejected]);
  assert.deepEqual(
    tabs.getSnapshot().group?.tabs.map((tab) => tab.kind),
    ["chat"],
  );
});

test("unregistering an extension resets its sidebar and keeps its open tab", async (t) => {
  const { registry, layout, tabs } = await createStore(t);
  layout.showSidebarView("sample:list");
  layout.openMainView("sample:app");
  const id = tabs.getSnapshot().group!.activeTabId;
  registry.unregisterExtension("sample");
  assert.equal(layout.getSnapshot().activeSidebarViewId, BUILT_IN_VIEWS.conversations);
  assert.equal(tabs.getSnapshot().group!.activeTabId, id);
});
