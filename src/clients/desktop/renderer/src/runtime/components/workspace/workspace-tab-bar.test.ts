import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { FileTab, TabGroup, WorkspaceTab } from "../../extensions/workspace-tab";
import type { TabDescribeContext } from "./workspace-tab-bar";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let tabBar: typeof import("./workspace-tab-bar");

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  Object.defineProperty(globalThis, "DOMRect", {
    configurable: true,
    value: environment.window.DOMRect,
  });
  tabBar = await import("./workspace-tab-bar");
});
afterEach(() => environment.cleanup());

const draft = {
  id: "draft",
  kind: "chat",
  preview: false,
  conversationId: null,
  draftId: "draft",
  draftSubmitted: false,
  title: null,
} as const;
const file = (
  id: string,
  path: string,
  directoryId: string | null = null,
  preview = false,
): FileTab => ({
  id,
  kind: "file",
  path,
  directoryId,
  preview,
  diffScope: null,
  diffFileView: false,
});
const group = (tabs: readonly WorkspaceTab[]): TabGroup => ({
  tabs,
  activeTabId: tabs[0]?.id ?? null,
  recentTabIds: tabs.map((tab) => tab.id),
  floatingChat: { conversationId: null, draftId: "floating", draftSubmitted: false },
});
const context: TabDescribeContext = {
  directories: [
    { id: null, name: "project", path: "/project", isPrimary: true },
    { id: "second", name: "shared", path: "/shared", isPrimary: false },
  ],
  conversationStatuses: new Map([
    ["busy", "waiting-approval"],
    ["running", "running"],
  ]),
  unsavedTabIds: new Set(["a"]),
  view: () => undefined,
};

test("an empty workspace or its sole new conversation has no tab entries", () => {
  assert.equal(tabBar.describeTabs(group([]), context).length, 0);
  assert.equal(tabBar.describeTabs(group([draft]), context).length, 0);
  const submitted = { ...draft, draftSubmitted: true };
  assert.equal(tabBar.describeTabs(group([submitted]), context).length, 0);
});

test("accepting the sole draft reveals the conversation with the same tab id", () => {
  const accepted = { ...draft, conversationId: draft.draftId, title: "Hello" };
  const tabs = tabBar.describeTabs(group([accepted]), context);
  assert.deepEqual(
    tabs.map(({ tab, title }) => [tab.id, title]),
    [[draft.id, "Hello"]],
  );
});

test("opening a file alongside the draft shows both tab entries", () => {
  const tabs = tabBar.describeTabs(group([draft, file("notes", "notes.md")]), context);
  assert.deepEqual(
    tabs.map(({ title }) => title),
    ["New conversation", "notes.md"],
  );
});

test("titles follow each tab kind and unknown extension views keep their title", () => {
  const { describeTabs } = tabBar;
  const tabs = describeTabs(
    group([
      draft,
      { ...draft, id: "busy", conversationId: "busy" },
      { ...draft, id: "running", conversationId: "running", title: "Build" },
      file("a", "docs/a.md"),
      { id: "jobs", kind: "jobs", preview: false },
      {
        id: "app",
        kind: "extension",
        preview: false,
        viewId: "e:app",
        key: null,
        title: "Repository",
        sequence: 0,
        params: null,
      },
    ]),
    context,
  );
  assert.deepEqual(
    tabs.map((tab) => tab.title),
    ["New conversation", "Conversation", "Build", "a.md", "Jobs", "Repository"],
  );
  assert.equal(tabs[1].status, "waiting-approval");
  assert.equal(tabs[2].status, "running");
  assert.equal(tabs[3].unsaved, true);
  assert.deepEqual(tabs[5].icon, { kind: "lucide", name: "Puzzle" });
});

test("conversation dots represent only active, waiting, or detached executions", () => {
  const statuses = [
    "running",
    "waiting-approval",
    "detached",
    "completed-unread",
    "failed-unread",
    "failed",
    "interrupted",
    "idle",
  ] as const;
  const tabs = tabBar.describeTabs(
    group(statuses.map((status) => ({ ...draft, id: status, conversationId: status }))),
    { ...context, conversationStatuses: new Map(statuses.map((status) => [status, status])) },
  );
  assert.deepEqual(
    tabs.map((tab) => tab.status),
    ["running", "waiting-approval", "detached", null, null, null, null, null],
  );
});

test("same-named files show distinguishing paths or directory names", () => {
  const { describeTabs } = tabBar;
  const tabs = describeTabs(
    group([
      file("a", "src/a/index.ts"),
      file("b", "src/b/index.ts"),
      file("c", "x/src/a/readme.md"),
      file("d", "y/src/a/readme.md"),
      file("e", "docs/license.md"),
      file("f", "docs/license.md", "second"),
      file("g", "docs/settings.json"),
    ]),
    context,
  );
  assert.deepEqual(
    tabs.map((tab) => tab.description),
    ["src/a", "src/b", "x/src/a", "y/src/a", "project", "shared", null],
  );
  assert.equal(tabs[0].tooltip, "project/src/a/index.ts");
  assert.equal(tabs[5].tooltip, "shared/docs/license.md");
  assert.equal(
    describeTabs(group([file("a", "src/index.ts")]), {
      ...context,
      directories: context.directories.slice(0, 1),
    })[0].tooltip,
    "src/index.ts",
  );
});

function renderBar() {
  const activated: string[] = [];
  const pinned: string[] = [];
  const closed: (readonly string[])[] = [];
  const moved: [string, number][] = [];
  const tabs = tabBar.describeTabs(
    group([file("a", "a.md"), file("b", "b.md", null, true), file("c", "c.md")]),
    context,
  );
  const { createElement } = environment.React;
  environment.render(
    createElement(tabBar.WorkspaceTabBar, {
      tabs,
      activeTabId: "a",
      onActivate: (id) => activated.push(id),
      onPin: (id) => pinned.push(id),
      onClose: (ids) => closed.push(ids),
      onMove: (id, index) => moved.push([id, index]),
    }),
  );
  const a = environment.screen.getByRole("tab", { name: "a.md" });
  const b = environment.screen.getByRole("tab", { name: "b.md" });
  const c = environment.screen.getByRole("tab", { name: "c.md" });
  return { activated, pinned, closed, moved, a, b, c };
}

test("the tab bar describes the selected tab, preview, dirty state and close button", () => {
  const { a, b } = renderBar();
  assert.ok(environment.screen.getByRole("tablist", { name: "Open tabs" }));
  assert.equal(a.getAttribute("aria-selected"), "true");
  assert.equal(a.getAttribute("aria-controls"), "workspace-tab-panel-a");
  assert.equal(a.getAttribute("aria-description"), "Unsaved changes");
  assert.equal(b.getAttribute("aria-description"), "Preview");
  assert.ok(b.closest(".is-preview"));
  assert.ok(environment.screen.getByRole("button", { name: "Close a.md" }));
});

test("click activates, double click pins, and middle click and the close button close", () => {
  const { a, b, c, activated, pinned, closed } = renderBar();
  environment.fireEvent.click(b);
  environment.fireEvent.doubleClick(b);
  environment.fireEvent(c, new window.MouseEvent("auxclick", { button: 1, bubbles: true }));
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Close a.md" }));
  assert.deepEqual(activated, ["b"]);
  assert.deepEqual(pinned, ["b"]);
  assert.deepEqual(closed, [["c"], [a.dataset.tabId]]);
});

test("the context menu closes tab groups or keeps the preview open", async () => {
  const { b, closed, pinned } = renderBar();
  async function select(label: string) {
    environment.fireEvent.contextMenu(b);
    environment.fireEvent.click(await environment.screen.findByRole("menuitem", { name: label }));
    await environment.waitFor(() => assert.equal(environment.screen.queryByRole("menu"), null));
  }
  await select("Close Others");
  await select("Close to the Right");
  await select("Close All");
  await select("Keep Open");
  assert.deepEqual(closed, [["a", "c"], ["c"], ["a", "b", "c"]]);
  assert.deepEqual(pinned, ["b"]);
});

test("arrow keys, Home and End focus tabs; Enter activates and Delete closes", () => {
  const { a, b, c, activated, closed } = renderBar();
  a.focus();
  environment.fireEvent.keyDown(a, { key: "ArrowRight" });
  assert.equal(document.activeElement, b);
  environment.fireEvent.keyDown(b, { key: "End" });
  assert.equal(document.activeElement, c);
  environment.fireEvent.keyDown(c, { key: "Home" });
  assert.equal(document.activeElement, a);
  environment.fireEvent.keyDown(a, { key: "ArrowLeft" });
  assert.equal(document.activeElement, c);
  environment.fireEvent.keyDown(c, { key: "Enter" });
  environment.fireEvent.keyDown(c, { key: "Delete" });
  assert.deepEqual(activated, ["c"]);
  assert.deepEqual(closed, [["c"]]);
});

test("dragging the preview pins it and dropping before another tab moves it", () => {
  const { a, b, pinned, moved } = renderBar();
  const data = new Map<string, string>();
  const dataTransfer = { setData: data.set.bind(data), getData: data.get.bind(data) };
  environment.fireEvent.dragStart(b, { dataTransfer });
  environment.fireEvent.dragOver(a, { dataTransfer, clientX: 0 });
  environment.fireEvent.drop(a, { dataTransfer, clientX: 0 });
  assert.deepEqual(pinned, ["b"]);
  assert.deepEqual(moved, [["b", 0]]);
});

test("dragging through another tab keeps the insertion line and leaving the bar clears it", () => {
  const { a, b } = renderBar();
  const list = environment.screen.getByRole("tablist");
  const dataTransfer = new Map<string, string>();
  environment.fireEvent.dragStart(b, {
    dataTransfer: { setData: dataTransfer.set.bind(dataTransfer) },
  });
  assert.ok(b.closest(".insert-before"));
  environment.fireEvent(b, new window.MouseEvent("dragleave", { bubbles: true, relatedTarget: a }));
  assert.ok(b.closest(".insert-before"));
  environment.fireEvent(
    a,
    new window.MouseEvent("dragleave", { bubbles: true, relatedTarget: document.body }),
  );
  assert.equal(list.querySelector(".insert-before, .insert-after"), null);
});

test("dropping into the empty space after the tabs moves the tab to the end", () => {
  const { a, c, moved } = renderBar();
  const list = environment.screen.getByRole("tablist");
  const data = new Map<string, string>();
  const dataTransfer = { setData: data.set.bind(data) };
  environment.fireEvent.dragStart(a, { dataTransfer });
  environment.fireEvent.dragOver(list, { dataTransfer });
  assert.ok(c.closest(".insert-after"));
  environment.fireEvent.drop(list, { dataTransfer });
  assert.deepEqual(moved, [["a", 2]]);
  assert.equal(list.querySelector(".insert-before, .insert-after"), null);
});
