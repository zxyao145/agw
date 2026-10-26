import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { FileViewerOption } from "./file-content.tsx";

// 这里只测试选中 extension 查看器时的行为：Source 使用 Monaco，jsdom 不能运行，渲染 Source 的用例在
// browser-tests/file-content.test.ts 中。
// Only behavior with an extension viewer selected is tested here: Source uses Monaco, which jsdom cannot run, so the
// cases that render Source are in browser-tests/file-content.test.ts.
const { React, act, fireEvent, render, screen, within } = await setupDomEnvironment();
const { default: FileContent } = await import("./file-content.tsx");

const VIEWERS: FileViewerOption[] = [
  {
    id: "markdown-preview:preview",
    title: "Preview",
    unsaved: false,
    saveStatus: "saved",
    topItems: [],
    bottomItems: [],
  },
  {
    id: "other:outline",
    title: "Outline",
    unsaved: true,
    saveStatus: "changed-on-disk",
    topItems: [],
    bottomItems: [],
  },
];

function element(
  selectedFile: string,
  fileViewers?: readonly FileViewerOption[],
  onReloadFileViewer?: (viewerId: string) => void,
  onReloadFileViewerDocument?: (viewerId: string) => void,
) {
  return React.createElement(FileContent, {
    projectId: "project-1",
    directoryId: "workspace",
    selectedFile,
    isLoadingContent: false,
    contentError: null,
    onlyDiff: false,
    diffContentData: null,
    comments: [],
    setComments() {},
    fileContent: "# Title",
    fileViewers,
    renderFileViewer: (viewerId: string) =>
      React.createElement("div", { "data-testid": "viewer" }, `viewer ${viewerId}`),
    onReloadFileViewer,
    onReloadFileViewerDocument,
  });
}

/** 文件名后显示的保存状态。The save status shown after the file name. */
function saveStatus(): { status: string | null; text: string } | null {
  const element = document.querySelector("[data-file-save-status]");
  return element
    ? { status: element.getAttribute("data-file-save-status"), text: element.textContent ?? "" }
    : null;
}

function tabs(): { id: string | null; selected: boolean; text: string }[] {
  return screen.getAllByRole("tab").map((tab) => ({
    id: tab.getAttribute("data-file-viewer-tab"),
    selected: tab.getAttribute("aria-selected") === "true",
    text: (tab.textContent ?? "").trim(),
  }));
}

async function selectTab(id: string) {
  await act(async () => {
    fireEvent.click(document.querySelector(`[data-file-viewer-tab="${id}"]`)!);
  });
}

test("FileContent_WithViewers_SelectsFirstViewerAndListsSource", async () => {
  render(element("README.md", VIEWERS));

  assert.deepEqual(tabs(), [
    { id: "markdown-preview:preview", selected: true, text: "Preview" },
    { id: "other:outline", selected: false, text: "●Outline" },
    { id: "source", selected: false, text: "Source" },
  ]);
  assert.equal(screen.getByLabelText("Unsaved changes").textContent, "●");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer markdown-preview:preview");
  assert.equal(screen.queryByText("# Title"), null);
});

/** Radix 关闭菜单后的清理在下一个宏任务中完成。Radix finishes closing a menu in the next macrotask. */
async function menuClosed() {
  await act(() => new Promise((resolve) => setTimeout(resolve, 0)));
}

/** 打开 "More" 菜单并选择一项。Opens the "More" menu and chooses an entry. */
async function chooseMoreItem(text: string) {
  const menu = await openMoreMenu();
  await act(async () => {
    fireEvent.click(within(menu).getByText(text));
  });
  await menuClosed();
}

test("FileContent_ReloadView_ReloadsSelectedViewer", async () => {
  const reloads: string[] = [];
  render(element("README.md", VIEWERS, (viewerId) => reloads.push(viewerId)));

  await chooseMoreItem("Reload view");
  await selectTab("other:outline");
  await chooseMoreItem("Reload view");

  assert.deepEqual(reloads, ["markdown-preview:preview", "other:outline"]);
});

test("FileContent_ChangedOnDiskReload_AsksSelectedViewerToReadFileAgain", async () => {
  const reloads: string[] = [];
  render(element("README.md", VIEWERS, undefined, (viewerId) => reloads.push(viewerId)));
  await selectTab("other:outline");

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload" }));
  });

  assert.deepEqual(reloads, ["other:outline"]);
});

test("FileContent_WithoutReloadDocumentHandler_ShowsChangedOnDiskWithoutReload", async () => {
  render(element("README.md", VIEWERS));
  await selectTab("other:outline");

  assert.deepEqual(saveStatus(), { status: "changed-on-disk", text: "Changed on disk" });
  assert.equal(screen.queryByRole("button", { name: "Reload" }), null);
});

test("FileContent_FileChanges_ReturnsToDefaultViewer", async () => {
  const view = render(element("README.md", VIEWERS));
  await selectTab("other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");

  view.rerender(element("docs/notes.md", VIEWERS));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "markdown-preview:preview");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer markdown-preview:preview");
});

function elementWithDefaults(
  selectedFile: string,
  defaultFileViewerId: string | null | undefined,
  onSetDefaultFileViewer?: (viewerId: string | null) => void,
) {
  return React.createElement(FileContent, {
    projectId: "project-1",
    directoryId: "workspace",
    selectedFile,
    isLoadingContent: false,
    contentError: null,
    onlyDiff: false,
    diffContentData: null,
    comments: [],
    setComments() {},
    fileContent: "# Title",
    fileViewers: VIEWERS,
    renderFileViewer: (viewerId: string) =>
      React.createElement("div", { "data-testid": "viewer" }, `viewer ${viewerId}`),
    defaultFileViewerId,
    onSetDefaultFileViewer,
  });
}

async function openMoreMenu() {
  const trigger = screen.getByRole("button", { name: "More" });
  await act(async () => {
    fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false, pointerType: "mouse" });
    fireEvent.click(trigger);
  });
  return screen.findByRole("menu");
}

/** 菜单中各项的文字，含勾选项。The texts of the menu's entries, checkbox items included. */
function menuTexts(menu: HTMLElement): string[] {
  return Array.from(menu.querySelectorAll('[role="menuitem"], [role="menuitemcheckbox"]'), (item) =>
    (item.textContent ?? "").trim(),
  );
}

test("FileContent_DefaultViewerIdMatchesViewer_SelectsItInitially", () => {
  render(elementWithDefaults("README.md", "other:outline"));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");
});

test("FileContent_DefaultViewerIdMatchesNothing_KeepsFirstViewer", () => {
  render(elementWithDefaults("README.md", "gone:viewer"));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "markdown-preview:preview");
});

test("FileContent_MoreMenu_SetsSelectedViewerAsDefaultForFileType", async () => {
  const calls: (string | null)[] = [];
  render(elementWithDefaults("docs/README.MD", null, (viewerId) => calls.push(viewerId)));

  await selectTab("other:outline");
  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), ["Open with Outline by default for *.md"]);
  await act(async () => {
    fireEvent.click(within(menu).getByText("Open with Outline by default for *.md"));
  });

  assert.deepEqual(calls, ["other:outline"]);
});

test("FileContent_MoreMenu_WithDefaultOffersReset", async () => {
  const calls: (string | null)[] = [];
  render(
    elementWithDefaults("README.md", "markdown-preview:preview", (viewerId) =>
      calls.push(viewerId),
    ),
  );

  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), [
    "Open with Preview by default for *.md",
    "Reset default for *.md",
  ]);
  await act(async () => {
    fireEvent.click(within(menu).getByText("Reset default for *.md"));
  });

  assert.deepEqual(calls, [null]);
});

test("FileContent_ResetDefaultWhileDefaultViewerShown_KeepsThatViewerSelected", async () => {
  const calls: (string | null)[] = [];
  const onSetDefaultFileViewer = (viewerId: string | null) => calls.push(viewerId);
  const view = render(elementWithDefaults("README.md", "other:outline", onSetDefaultFileViewer));
  assert.equal(tabs().find((tab) => tab.selected)?.id, "other:outline");

  const menu = await openMoreMenu();
  await act(async () => {
    fireEvent.click(within(menu).getByText("Reset default for *.md"));
  });
  view.rerender(elementWithDefaults("README.md", null, onSetDefaultFileViewer));

  assert.deepEqual(calls, [null]);
  assert.equal(tabs().find((tab) => tab.selected)?.id, "other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");
});

test("FileContent_WithoutOnSetDefaultFileViewer_HasNoMoreMenu", () => {
  render(elementWithDefaults("README.md", "markdown-preview:preview"));

  assert.equal(screen.queryByRole("button", { name: "More" }), null);
});

/**
 * 第一个查看器在 "More" 菜单最后有两个条目，第二项有子菜单。The first viewer has two items at the end of the "More"
 * menu; the second has a submenu.
 */
const VIEWERS_WITH_ITEMS: FileViewerOption[] = [
  {
    ...VIEWERS[0],
    bottomItems: [
      { id: "export", label: "Export" },
      {
        id: "indent",
        label: "Indentation",
        menu: [
          { id: "tabs", label: "Tabs" },
          { id: "spaces", label: "Spaces" },
        ],
      },
    ],
  },
  VIEWERS[1],
];

function elementWithItems(
  onSelectFileViewerItem: (viewerId: string, itemId: string, entryId: string | null) => void,
  onSetDefaultFileViewer?: (viewerId: string | null) => void,
) {
  return React.createElement(FileContent, {
    projectId: "project-1",
    directoryId: "workspace",
    selectedFile: "README.md",
    isLoadingContent: false,
    contentError: null,
    onlyDiff: false,
    diffContentData: null,
    comments: [],
    setComments() {},
    fileContent: "# Title",
    fileViewers: VIEWERS_WITH_ITEMS,
    renderFileViewer: (viewerId: string) =>
      React.createElement("div", { "data-testid": "viewer" }, `viewer ${viewerId}`),
    onSelectFileViewerItem,
    onSetDefaultFileViewer,
  });
}

test("FileContent_MoreMenu_ListsSelectedViewerItemsAfterDefaultEntries", async () => {
  const chosen: [string, string, string | null][] = [];
  render(
    elementWithItems(
      (viewerId, itemId, entryId) => chosen.push([viewerId, itemId, entryId]),
      () => undefined,
    ),
  );

  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), [
    "Open with Preview by default for *.md",
    "Export",
    "Indentation",
  ]);
  assert.equal(menu.querySelectorAll('[role="separator"]').length, 1);
  await act(async () => {
    fireEvent.click(within(menu).getByText("Export"));
  });
  assert.deepEqual(chosen, [["markdown-preview:preview", "export", null]]);

  await menuClosed();
  const reopened = await openMoreMenu();
  await act(async () => {
    fireEvent.click(within(reopened).getByText("Indentation"));
  });
  await act(async () => {
    fireEvent.click(await screen.findByRole("menuitem", { name: "Spaces" }));
  });
  assert.deepEqual(chosen.at(-1), ["markdown-preview:preview", "indent", "spaces"]);
});

test("FileContent_MoreMenu_ViewerItemsWithoutDefaultHandler_ListsOnlyItems", async () => {
  render(elementWithItems(() => undefined));

  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), ["Export", "Indentation"]);
  assert.equal(menu.querySelectorAll('[role="separator"]').length, 0);
});
