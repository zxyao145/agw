import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { FileItem } from "../../../services/files";
import type { ExplorerContextMenuArgs, ExtensionMenuItem } from "../../../lib/extension-menu";
import type { GitChangeGroup } from "./types";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, within } = environment;
// Radix ContextMenu 用 DOMRect 描述指针位置；测试环境没有把 jsdom 的 DOMRect 放到全局。
// Radix ContextMenu describes the pointer position with DOMRect, which the environment does not expose globally.
Object.defineProperty(globalThis, "DOMRect", {
  configurable: true,
  value: environment.window.DOMRect,
});
const { ExplorerFileTree, ExplorerGitChangeTree } = await import("./explorer-file-tree.tsx");
const { ProjectFileScopeContext } = await import("./project-file-scope.tsx");

const ROOT_ITEMS: FileItem[] = [
  { name: "src", path: "src", type: "directory" },
  { name: "README.md", path: "README.md", type: "file", size: 12 },
];

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

function renderTree(contextMenuItems?: ExtensionMenuItem<ExplorerContextMenuArgs>[]) {
  return render(
    React.createElement(
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
        contextMenuItems,
      }),
    ),
  );
}

async function openContextMenu(name: string) {
  await act(async () => {
    fireEvent.contextMenu(screen.getByText(name));
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
