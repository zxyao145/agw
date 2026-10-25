import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { FileViewerOption } from "./file-content.tsx";

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
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode,
  onReloadSource?: () => void,
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
    renderFileViewerInfo,
    onReloadSource,
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

function renderInfo(viewerId: string) {
  return React.createElement("span", { "data-testid": "info" }, `info ${viewerId}`);
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

/** "More" 菜单顶部的查看器说明。The viewer info at the top of the "More" menu. */
async function moreMenuInfo(): Promise<string | null> {
  const menu = await openMoreMenu();
  const info = within(menu).getByTestId("info").textContent;
  await act(async () => {
    fireEvent.keyDown(menu, { key: "Escape" });
  });
  await menuClosed();
  return info;
}

test("FileContent_WithViewers_ShowsSwitchAndMoreMenuWithInfoAndReload", async () => {
  render(
    element(
      "README.md",
      VIEWERS,
      () => {},
      renderInfo,
      () => {},
    ),
  );

  // 文件标题行右侧依次是切换按钮与 "More" 菜单，切换到 Source 后不变。
  // The file title row's right side holds the switch and the "More" menu, also in Source.
  const actions = screen.getByRole("tablist").parentElement!;
  const controls = () => [
    screen.getByRole("tablist"),
    screen.getByRole("button", { name: "More" }),
  ];
  assert.deepEqual([...actions.children], controls());
  assert.ok(actions.parentElement!.textContent!.includes("README.md"));

  // 菜单顶部是查看器说明，分隔线之后是刷新。The menu starts with the viewer info, then reloading after a separator.
  const menu = await openMoreMenu();
  const info = within(menu).getByTestId("info");
  assert.equal(info.textContent, "info markdown-preview:preview");
  assert.equal(menu.firstElementChild, info.parentElement);
  assert.deepEqual(menuTexts(menu), ["Reload view"]);
  assert.equal(menu.querySelectorAll('[role="separator"]').length, 1);
  await act(async () => {
    fireEvent.keyDown(menu, { key: "Escape" });
  });
  await menuClosed();

  await selectTab("source");
  assert.deepEqual([...actions.children], controls());
  assert.deepEqual(menuTexts(await openMoreMenu()), ["Reload file"]);
});

test("FileContent_ReloadView_ReloadsSelectedViewer", async () => {
  const reloads: string[] = [];
  render(element("README.md", VIEWERS, (viewerId) => reloads.push(viewerId)));

  await chooseMoreItem("Reload view");
  await selectTab("other:outline");
  await chooseMoreItem("Reload view");

  assert.deepEqual(reloads, ["markdown-preview:preview", "other:outline"]);
});

test("FileContent_SourceSelected_ReloadRereadsFileInsteadOfViewer", async () => {
  const viewerReloads: string[] = [];
  let sourceReloads = 0;
  render(
    element(
      "README.md",
      VIEWERS,
      (viewerId) => viewerReloads.push(viewerId),
      undefined,
      () => (sourceReloads += 1),
    ),
  );

  await selectTab("source");
  await chooseMoreItem("Reload file");

  assert.equal(sourceReloads, 1);
  assert.deepEqual(viewerReloads, []);
  assert.deepEqual(menuTexts(await openMoreMenu()), ["Reload file"]);
});

test("FileContent_SourceSelected_InfoShowsViewerSelectedBeforeSource", async () => {
  render(element("README.md", VIEWERS, undefined, renderInfo));

  await selectTab("source");
  assert.equal(await moreMenuInfo(), "info markdown-preview:preview");

  await selectTab("other:outline");
  await selectTab("source");
  await selectTab("source");
  assert.equal(await moreMenuInfo(), "info other:outline");
});

test("FileContent_MoreMenu_TopItemsFollowReloadOnlyForSelectedViewer", async () => {
  const chosen: [string, string, string | null][] = [];
  render(
    React.createElement(FileContent, {
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
      fileViewers: [
        {
          ...VIEWERS[0],
          topItems: [
            {
              id: "encoding",
              label: "UTF-8",
              menu: [
                {
                  id: "reopen",
                  label: "Reopen with Encoding",
                  menu: [
                    { id: "reopen:utf-8", label: "UTF-8" },
                    { id: "reopen:gbk", label: "GBK" },
                  ],
                },
              ],
            },
            { id: "wrap", label: "Word wrap", checked: true },
            { id: "save", label: "Save", disabled: true },
          ],
          bottomItems: [{ id: "export", label: "Export" }],
        },
        VIEWERS[1],
      ],
      renderFileViewer: (viewerId: string) =>
        React.createElement("div", { "data-testid": "viewer" }, `viewer ${viewerId}`),
      onReloadFileViewer: () => {},
      onReloadSource: () => {},
      renderFileViewerInfo: renderInfo,
      onSetDefaultFileViewer: () => {},
      onSelectFileViewerItem: (viewerId: string, itemId: string, entryId: string | null) =>
        chosen.push([viewerId, itemId, entryId]),
    }),
  );

  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), [
    "Reload view",
    "UTF-8",
    "Word wrap",
    "Save",
    "Open with Preview by default for *.md",
    "Export",
  ]);
  assert.equal(menu.querySelectorAll('[role="separator"]').length, 3);
  // 勾选项显示勾选状态，不可用的项不能选择。A checkbox item shows its state; an unavailable entry cannot be chosen.
  const wordWrap = within(menu).getByRole("menuitemcheckbox", { name: "Word wrap" });
  assert.equal(wordWrap.getAttribute("aria-checked"), "true");
  const save = within(menu).getByRole("menuitem", { name: "Save" });
  assert.equal(save.getAttribute("aria-disabled"), "true");
  await act(async () => {
    fireEvent.click(save);
  });
  assert.deepEqual(chosen, []);
  await act(async () => {
    fireEvent.click(wordWrap);
  });
  assert.deepEqual(chosen, [["markdown-preview:preview", "wrap", null]]);
  await menuClosed();

  const reopened = await openMoreMenu();
  await act(async () => {
    fireEvent.click(within(reopened).getByText("UTF-8"));
  });
  await act(async () => {
    fireEvent.click(await screen.findByRole("menuitem", { name: "Reopen with Encoding" }));
  });
  await act(async () => {
    fireEvent.click(await screen.findByRole("menuitem", { name: "GBK" }));
  });
  assert.deepEqual(chosen.at(-1), ["markdown-preview:preview", "encoding", "reopen:gbk"]);
  await menuClosed();

  // 选中 Source 时说明仍为切换前的查看器，菜单中没有它的条目。With Source selected the info still describes the
  // viewer selected before, and the menu has none of its items.
  await selectTab("source");
  const sourceMenu = await openMoreMenu();
  assert.equal(within(sourceMenu).getByTestId("info").textContent, "info markdown-preview:preview");
  assert.deepEqual(menuTexts(sourceMenu), ["Reload file", "Open with Source by default for *.md"]);
});

test("FileContent_WithoutReloadHandlers_MoreMenuHasNoReloadEntry", async () => {
  render(element("README.md", VIEWERS, undefined, renderInfo));

  assert.deepEqual(menuTexts(await openMoreMenu()), []);
  await act(async () => {
    fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });
  });
  await menuClosed();
  await selectTab("source");
  assert.deepEqual(menuTexts(await openMoreMenu()), []);
});

test("FileContent_SelectedViewer_ShowsItsSaveStatusAfterFileName", async () => {
  render(element("README.md", VIEWERS, undefined, undefined, undefined, () => {}));

  // 状态与文件名在同一行，紧跟文件名。The status sits in the file name's row, right after the name.
  const status = document.querySelector("[data-file-save-status]")!;
  assert.equal(status.previousElementSibling!.textContent, "README.md");
  assert.deepEqual(saveStatus(), { status: "saved", text: "Saved" });

  await selectTab("other:outline");
  assert.deepEqual(saveStatus(), { status: "changed-on-disk", text: "Changed on diskReload" });

  await selectTab("source");
  assert.equal(saveStatus(), null);
});

test("FileContent_ChangedOnDiskReload_AsksSelectedViewerToReadFileAgain", async () => {
  const reloads: string[] = [];
  render(
    element("README.md", VIEWERS, undefined, undefined, undefined, (viewerId) =>
      reloads.push(viewerId),
    ),
  );
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

test("FileContent_SelectSource_ShowsFileText", async () => {
  render(element("README.md", VIEWERS));

  await selectTab("source");

  assert.equal(tabs().find((tab) => tab.selected)?.id, "source");
  assert.equal(screen.queryByTestId("viewer"), null);
  assert.ok(screen.getByText("# Title"));
});

test("FileContent_FileChanges_ReturnsToDefaultViewer", async () => {
  const view = render(element("README.md", VIEWERS));
  await selectTab("other:outline");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer other:outline");

  view.rerender(element("docs/notes.md", VIEWERS));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "markdown-preview:preview");
  assert.equal(screen.getByTestId("viewer").textContent, "viewer markdown-preview:preview");
});

test("FileContent_WithoutViewers_ShowsTextWithoutTabs", async () => {
  render(element("README.md"));

  assert.equal(screen.queryAllByRole("tab").length, 0);
  assert.equal(screen.queryByTestId("viewer"), null);
  assert.ok(screen.getByText("# Title"));
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

test("FileContent_DefaultViewerIdIsSource_SelectsSourceInitially", () => {
  render(elementWithDefaults("README.md", "source"));

  assert.equal(tabs().find((tab) => tab.selected)?.id, "source");
  assert.equal(screen.queryByTestId("viewer"), null);
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

test("FileContent_MoreMenu_SourceSelectedCanBecomeDefault", async () => {
  const calls: (string | null)[] = [];
  render(elementWithDefaults("Dockerfile", null, (viewerId) => calls.push(viewerId)));

  await selectTab("source");
  const menu = await openMoreMenu();
  await act(async () => {
    fireEvent.click(within(menu).getByText("Open with Source by default for dockerfile"));
  });

  assert.deepEqual(calls, ["source"]);
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

test("FileContent_MoreMenu_OtherViewerOrSourceSelected_HasNoItemsOfFirstViewer", async () => {
  const view = render(
    elementWithItems(
      () => undefined,
      () => undefined,
    ),
  );
  await selectTab("other:outline");
  const menu = await openMoreMenu();
  assert.deepEqual(menuTexts(menu), ["Open with Outline by default for *.md"]);
  view.unmount();

  // 没有默认查看器设置时，选中 Source 没有 More 菜单。Without the default setting, Source has no More menu.
  render(elementWithItems(() => undefined));
  await selectTab("source");
  assert.equal(screen.queryByRole("button", { name: "More" }), null);
});
