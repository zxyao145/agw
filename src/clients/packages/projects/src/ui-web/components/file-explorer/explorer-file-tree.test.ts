import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment, startApiServer } from "@agw/test-harness";

import type { FileItem } from "../../../services/files";
import type { ExplorerContextMenuArgs, ExtensionMenuItem } from "../../../lib/extension-menu";
import type { GitChangeGroup } from "./types";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, waitFor, within } = environment;
// Radix ContextMenu 用 DOMRect 描述指针位置；测试环境没有把 jsdom 的 DOMRect 放到全局。
// Radix ContextMenu describes the pointer position with DOMRect, which the environment does not expose globally.
Object.defineProperty(globalThis, "DOMRect", {
  configurable: true,
  value: environment.window.DOMRect,
});
const { ExplorerFileTree, ExplorerGitChangeTree } = await import("./explorer-file-tree.tsx");
const { buildGitChangeGroups } = await import("./git-change-tree.ts");
const { ProjectFileScopeContext } = await import("./project-file-scope.tsx");
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");

const api = await startApiServer({
  "GET /api/files/list": (request: { query: URLSearchParams }) => ({
    items:
      request.query.get("path") === "docs"
        ? [{ name: "sub", path: "docs/sub", type: "directory" }]
        : [{ name: "a.md", path: "docs/sub/a.md", type: "file" }],
  }),
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

const ROOT_ITEMS: FileItem[] = [
  { name: "src", path: "src", type: "directory" },
  { name: "README.md", path: "README.md", type: "file", size: 12 },
];

const DOCS: FileItem[] = [{ name: "docs", path: "docs", type: "directory" }];

function menuItems(
  selections: ExplorerContextMenuArgs[],
): ExtensionMenuItem<ExplorerContextMenuArgs>[] {
  return [
    {
      id: "extension:open",
      title: "Open in extension",
      icon: React.createElement("svg", { "data-testid": "open icon" }),
      onSelect: (args) => selections.push(args),
    },
  ];
}

function tree(props: Partial<Parameters<typeof ExplorerFileTree>[0]> = {}) {
  return React.createElement(
    ProjectFileScopeContext.Provider,
    { value: { projectId: "project-1", directoryId: "extra" } },
    React.createElement(ExplorerFileTree, {
      projectId: "project-1",
      rootItems: ROOT_ITEMS,
      onlyDiff: false,
      recursiveMode: false,
      onFileSelect() {},
      onFileDeleted() {},
      onFileReset() {},
      ...props,
    }),
  );
}

function renderTree(contextMenuItems?: ExtensionMenuItem<ExplorerContextMenuArgs>[]) {
  return render(tree({ contextMenuItems }));
}

function recordScroll(testContext: { after(callback: () => void): void }) {
  const calls: Array<{ element: Element; options: ScrollIntoViewOptions }> = [];
  const { Element, HTMLElement } = environment.window;
  const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "scrollIntoView")!;
  Reflect.deleteProperty(HTMLElement.prototype, "scrollIntoView");
  Object.defineProperty(Element.prototype, "scrollIntoView", {
    configurable: true,
    value(this: Element, options: ScrollIntoViewOptions) {
      calls.push({ element: this, options });
    },
  });
  testContext.after(() => {
    Reflect.deleteProperty(Element.prototype, "scrollIntoView");
    Object.defineProperty(HTMLElement.prototype, "scrollIntoView", original);
  });
  return calls;
}

test("a double click on a file selects it pinned", () => {
  const selections: unknown[][] = [];
  render(tree({ onFileSelect: (...args) => selections.push(args) }));

  const file = screen.getByText("README.md");
  fireEvent.click(file);
  fireEvent.click(file, { detail: 2 });
  fireEvent.doubleClick(file);

  assert.deepEqual(selections, [
    ["README.md", undefined, { pinned: false }],
    ["README.md", undefined, { pinned: false }],
    ["README.md", undefined, { pinned: true }],
  ]);
});

test("selectedPath highlights the file and expands its folders", async (context) => {
  const scrolls = recordScroll(context);
  const view = render(tree({ rootItems: DOCS, selectedPath: null }));
  assert.equal(screen.queryByText("a.md"), null);

  const requestStart = api.requests.length;
  view.rerender(tree({ rootItems: DOCS, selectedPath: "docs/sub/a.md" }));

  const file = await screen.findByRole("treeitem", { name: "a.md", selected: true });
  assert.ok(file.firstElementChild?.classList.contains("bg-accent"));
  assert.deepEqual(
    api.requests.slice(requestStart).map((request) => request.query.get("path")),
    ["docs", "docs/sub"],
  );
  await waitFor(() => assert.equal(scrolls.length, 1));
  assert.ok(scrolls[0].element === file);
  assert.deepEqual(scrolls[0].options, { block: "nearest" });
});

test("a click on a file only highlights it and keeps the expansion", async (context) => {
  const scrolls = recordScroll(context);
  function ControlledTree() {
    const [selectedPath, setSelectedPath] = React.useState<string | null>(null);
    return tree({ rootItems: DOCS, selectedPath, onFileSelect: setSelectedPath });
  }
  render(React.createElement(ControlledTree));
  fireEvent.click(screen.getByText("docs"));
  fireEvent.click(await screen.findByText("sub"));
  const file = await screen.findByText("a.md");
  const requestCount = api.requests.length;

  fireEvent.click(file);

  assert.ok(screen.getByRole("treeitem", { name: "a.md", selected: true }));
  assert.equal(scrolls.length, 0);
  assert.equal(api.requests.length, requestCount);
});

async function openContextMenu(target: string | HTMLElement) {
  await act(async () => {
    fireEvent.contextMenu(typeof target === "string" ? screen.getByText(target) : target);
  });
  return screen.findByRole("menu");
}

/** 右键菜单的菜单项与分隔线，按显示顺序。A context menu's items and separators, in display order. */
function menuEntries(menu: HTMLElement): string[] {
  return Array.from(menu.querySelectorAll('[role="menuitem"], [role="separator"]'), (entry) =>
    entry.getAttribute("role") === "separator" ? "---" : (entry.textContent ?? "").trim(),
  );
}

// 关闭的菜单在下一个任务中移动焦点；之前打开的下一个菜单会被这次焦点移动关闭。
// A closed menu moves focus in the next task; a menu opened earlier would be dismissed by that focus move.
async function waitForFocusReturn() {
  await act(() => new Promise((resolve) => setTimeout(resolve, 0)));
}

async function selectMenuItem(menu: HTMLElement, title: string) {
  await act(async () => {
    fireEvent.click(within(menu).getByText(title));
  });
  await waitForFocusReturn();
}

async function closeMenu(menu: HTMLElement) {
  await act(async () => {
    fireEvent.keyDown(menu, { key: "Escape" });
  });
  await waitForFocusReturn();
}

test("extension items follow the built-in file actions after a separator and receive the file", async () => {
  const selections: ExplorerContextMenuArgs[] = [];
  renderTree(menuItems(selections));

  const menu = await openContextMenu("README.md");
  assert.deepEqual(menuEntries(menu), [
    "Delete",
    "---",
    "Reset to HEAD",
    "---",
    "Open in extension",
  ]);
  assert.equal(within(menu).queryByTestId("open icon") !== null, true);
  await selectMenuItem(menu, "Open in extension");

  assert.deepEqual(selections, [
    { path: "README.md", name: "README.md", type: "file", directoryId: "extra" },
  ]);
});

test("a directory offers extension items after Delete and passes its type", async () => {
  const selections: ExplorerContextMenuArgs[] = [];
  renderTree(menuItems(selections));

  const menu = await openContextMenu("src");
  assert.deepEqual(menuEntries(menu), ["Delete", "---", "Open in extension"]);
  await selectMenuItem(menu, "Open in extension");

  assert.deepEqual(selections, [
    { path: "src", name: "src", type: "directory", directoryId: "extra" },
  ]);
});

test("without extension items the context menu keeps only the built-in actions", async () => {
  renderTree();

  const fileMenu = await openContextMenu("README.md");
  assert.deepEqual(menuEntries(fileMenu), ["Delete", "---", "Reset to HEAD"]);
  await closeMenu(fileMenu);
  const directoryMenu = await openContextMenu("src");
  assert.deepEqual(menuEntries(directoryMenu), ["Delete"]);
  await closeMenu(directoryMenu);
});

test("the git change tree offers the same extension items outside a directory scope", async () => {
  const selections: ExplorerContextMenuArgs[] = [];
  const groups: GitChangeGroup[] = [
    {
      scope: "unstaged",
      label: "Changes",
      fileCount: 1,
      items: [
        {
          name: "app.ts",
          path: "src/app.ts",
          type: "file",
          gitStatus: "modified",
          gitScope: "unstaged",
        },
      ],
    },
  ];
  render(
    React.createElement(ExplorerGitChangeTree, {
      projectId: "project-1",
      groups,
      onFileSelect() {},
      onFileDeleted() {},
      onFileReset() {},
      onGitScopeChanged() {},
      contextMenuItems: menuItems(selections),
    }),
  );

  const menu = await openContextMenu("app.ts");
  await selectMenuItem(menu, "Open in extension");

  assert.deepEqual(selections, [
    { path: "src/app.ts", name: "app.ts", type: "file", directoryId: null },
  ]);
});

test("the git change tree offers Reset to HEAD only for files that exist in HEAD", async () => {
  const groups = buildGitChangeGroups(
    [
      {
        name: "changed.ts",
        path: "changed.ts",
        type: "file",
        gitStatus: "modified",
        gitStagedStatus: "modified",
        gitUnstagedStatus: "modified",
      },
      {
        name: "untracked.ts",
        path: "untracked.ts",
        type: "file",
        gitStatus: "untracked",
        gitStagedStatus: null,
        gitUnstagedStatus: "untracked",
      },
      {
        name: "added.ts",
        path: "added.ts",
        type: "file",
        gitStatus: "modified",
        gitStagedStatus: "added",
        gitUnstagedStatus: "modified",
      },
    ],
    "",
  );
  render(
    React.createElement(ExplorerGitChangeTree, {
      projectId: "project-1",
      groups,
      onFileSelect() {},
      onFileDeleted() {},
      onFileReset() {},
      onGitScopeChanged() {},
    }),
  );

  const menus: Record<string, string[][]> = {};
  for (const name of ["changed.ts", "untracked.ts", "added.ts"]) {
    menus[name] = [];
    for (const row of screen.getAllByText(name)) {
      const menu = await openContextMenu(row);
      menus[name].push(menuEntries(menu));
      await closeMenu(menu);
    }
  }

  assert.deepEqual(menus, {
    "changed.ts": [
      ["Delete", "---", "Reset to HEAD"],
      ["Delete", "---", "Reset to HEAD"],
    ],
    "untracked.ts": [["Delete"]],
    "added.ts": [["Delete"], ["Delete"]],
  });
});
