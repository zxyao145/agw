import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { FileViewerOption } from "./file-content.tsx";

// jsdom 覆盖 extension 查看器、二进制 diff 和读取错误。Chromium 覆盖使用 Monaco 的场景。
// jsdom covers extension viewers, binary diffs, and read errors. Chromium covers the cases that use Monaco.
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

const BINARY_DIFF = {
  diff: "Binary files a/logo.png and b/logo.png differ\n",
  unchanged: false,
  isBinary: true,
  originalContent: null,
  modifiedContent: null,
};

function element(props: Partial<Parameters<typeof FileContent>[0]> = {}) {
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
    fileViewers: VIEWERS,
    renderFileViewer: (viewerId: string) =>
      React.createElement("div", { "data-testid": "viewer" }, `viewer ${viewerId}`),
    ...props,
  });
}

/** 标题行中的按钮名称。The names of the buttons in the title row. */
const buttonNames = () =>
  screen.getAllByRole("button").map((button) => button.getAttribute("aria-label"));

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
  render(element());

  assert.deepEqual(tabs(), [
    { id: "markdown-preview:preview", selected: true, text: "Preview" },
    { id: "other:outline", selected: false, text: "●Outline" },
    { id: "source", selected: false, text: "Source" },
  ]);
  assert.equal(screen.getByLabelText("Unsaved changes").textContent, "●");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer markdown-preview:preview");
  assert.equal(screen.queryByText("# Title"), null);
});

test("FileContent_FileViewInDiffMode_ShowsViewerTabsAndActions", async () => {
  const calls: boolean[] = [];
  render(
    element({
      onlyDiff: true,
      diffFileView: true,
      onDiffFileViewChange: (value) => calls.push(value),
      onReloadFileViewer() {},
      onSetDefaultFileViewer() {},
    }),
  );

  assert.deepEqual(tabs(), [
    { id: "markdown-preview:preview", selected: true, text: "Preview" },
    { id: "other:outline", selected: false, text: "●Outline" },
    { id: "source", selected: false, text: "Source" },
  ]);
  assert.equal(screen.getByTestId("viewer").textContent, "viewer markdown-preview:preview");
  assert.deepEqual(menuTexts(await openMoreMenu()), [
    "Reload view",
    "Open with Preview by default for *.md",
  ]);
  await act(async () => fireEvent.keyDown(document, { key: "Escape" }));
  await menuClosed();
  assert.deepEqual(buttonNames(), ["More", "View changes"]);
  const toggle = screen.getByRole("button", { name: "View changes" });
  assert.equal(toggle.getAttribute("title"), "View changes");
  await act(async () => fireEvent.click(toggle));
  assert.deepEqual(calls, [false]);
});

test("FileContent_DiffModeOff_HasNoViewToggle", () => {
  render(element({ onDiffFileViewChange() {} }));

  assert.equal(screen.queryByRole("button", { name: "View file" }), null);
  assert.equal(screen.queryByRole("button", { name: "View changes" }), null);
});

test("FileContent_DiffModeWithUnsavedViewer_MarksViewFile", async () => {
  const calls: boolean[] = [];
  render(
    element({
      onlyDiff: true,
      diffContentData: BINARY_DIFF,
      onDiffFileViewChange: (value) => calls.push(value),
    }),
  );

  assert.deepEqual(screen.queryAllByRole("tab"), []);
  assert.ok(screen.getByText("Binary file; its contents are not shown."));
  assert.deepEqual(buttonNames(), ["View file, unsaved changes"]);
  const toggle = screen.getByRole("button", { name: "View file, unsaved changes" });
  assert.equal(toggle.getAttribute("title"), "View file, unsaved changes");
  await act(async () => fireEvent.click(toggle));
  assert.deepEqual(calls, [true]);
});

test("FileContent_FileViewReadErrorInDiffMode_KeepsViewChangesToggle", () => {
  render(
    element({
      onlyDiff: true,
      diffFileView: true,
      fileViewers: [],
      contentError: "File not found",
      onDiffFileViewChange() {},
    }),
  );

  assert.ok(screen.getByText("Error loading file: File not found"));
  assert.deepEqual(buttonNames(), ["More", "View changes"]);
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
  render(element({ onReloadFileViewer: (viewerId) => reloads.push(viewerId) }));

  await chooseMoreItem("Reload view");
  await selectTab("other:outline");
  await chooseMoreItem("Reload view");

  assert.deepEqual(reloads, ["markdown-preview:preview", "other:outline"]);
});

test("FileContent_ChangedOnDiskReload_AsksSelectedViewerToReadFileAgain", async () => {
  const reloads: string[] = [];
  render(element({ onReloadFileViewerDocument: (viewerId) => reloads.push(viewerId) }));
  await selectTab("other:outline");

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload" }));
  });

  assert.deepEqual(reloads, ["other:outline"]);
});

test("FileContent_WithoutReloadDocumentHandler_ShowsChangedOnDiskWithoutReload", async () => {
  render(element());
  await selectTab("other:outline");

  assert.deepEqual(saveStatus(), { status: "changed-on-disk", text: "Changed on disk" });
  assert.equal(screen.queryByRole("button", { name: "Reload" }), null);
});

test("FileContent_FileChanges_ReturnsToDefaultViewer", async () => {
  const view = render(element());
  await selectTab("other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");

  view.rerender(element({ selectedFile: "docs/notes.md" }));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "markdown-preview:preview");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer markdown-preview:preview");
});

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
  render(element({ defaultFileViewerId: "other:outline" }));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");
});

test("FileContent_DefaultViewerIdMatchesNothing_KeepsFirstViewer", () => {
  render(element({ defaultFileViewerId: "gone:viewer" }));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "markdown-preview:preview");
});

test("FileContent_MoreMenu_SetsSelectedViewerAsDefaultForFileType", async () => {
  const calls: (string | null)[] = [];
  render(
    element({
      selectedFile: "docs/README.MD",
      onSetDefaultFileViewer: (viewerId) => calls.push(viewerId),
    }),
  );

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
    element({
      defaultFileViewerId: "markdown-preview:preview",
      onSetDefaultFileViewer: (viewerId) => calls.push(viewerId),
    }),
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
  const view = render(element({ defaultFileViewerId: "other:outline", onSetDefaultFileViewer }));
  assert.equal(tabs().find((tab) => tab.selected)?.id, "other:outline");

  const menu = await openMoreMenu();
  await act(async () => {
    fireEvent.click(within(menu).getByText("Reset default for *.md"));
  });
  view.rerender(element({ defaultFileViewerId: null, onSetDefaultFileViewer }));

  assert.deepEqual(calls, [null]);
  assert.equal(tabs().find((tab) => tab.selected)?.id, "other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");
});

test("FileContent_WithoutOnSetDefaultFileViewer_HasNoMoreMenu", () => {
  render(element({ defaultFileViewerId: "markdown-preview:preview" }));

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

test("FileContent_MoreMenu_ListsSelectedViewerItemsAfterDefaultEntries", async () => {
  const chosen: [string, string, string | null][] = [];
  render(
    element({
      fileViewers: VIEWERS_WITH_ITEMS,
      onSelectFileViewerItem: (viewerId, itemId, entryId) =>
        chosen.push([viewerId, itemId, entryId]),
      onSetDefaultFileViewer: () => undefined,
    }),
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
  render(element({ fileViewers: VIEWERS_WITH_ITEMS, onSelectFileViewerItem: () => undefined }));

  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), ["Export", "Indentation"]);
  assert.equal(menu.querySelectorAll('[role="separator"]').length, 0);
});
