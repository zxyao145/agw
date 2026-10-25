import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { FileViewerOption } from "./file-content.tsx";

const { React, act, fireEvent, render, screen, within } = await setupDomEnvironment();
const { default: FileContent } = await import("./file-content.tsx");

const VIEWERS: FileViewerOption[] = [
  { id: "markdown-preview:preview", title: "Preview", unsaved: false, saveStatus: "saved" },
  { id: "other:outline", title: "Outline", unsaved: true, saveStatus: "changed-on-disk" },
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

test("FileContent_WithViewers_ShowsSwitchReloadAndInfoNextToFileName", async () => {
  render(
    element(
      "README.md",
      VIEWERS,
      () => {},
      renderInfo,
      () => {},
    ),
  );

  // 文件标题行右侧依次是切换按钮、刷新按钮与查看器说明，切换到 Source 后位置不变。
  // The file title row's right side holds the switch, the reload button and the viewer info, also in Source.
  const actions = screen.getByRole("tablist").parentElement!;
  assert.deepEqual(
    [...actions.children],
    [
      screen.getByRole("tablist"),
      screen.getByRole("button", { name: "Reload view" }),
      screen.getByTestId("info"),
    ],
  );
  assert.equal(screen.getByTestId("info").textContent, "info markdown-preview:preview");
  assert.ok(actions.parentElement!.textContent!.includes("README.md"));

  await selectTab("source");
  assert.deepEqual(
    [...actions.children],
    [
      screen.getByRole("tablist"),
      screen.getByRole("button", { name: "Reload file" }),
      screen.getByTestId("info"),
    ],
  );
});

test("FileContent_ReloadView_ReloadsSelectedViewer", async () => {
  const reloads: string[] = [];
  render(element("README.md", VIEWERS, (viewerId) => reloads.push(viewerId)));

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload view" }));
  });
  await selectTab("other:outline");
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload view" }));
  });

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
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload file" }));
  });

  assert.equal(sourceReloads, 1);
  assert.deepEqual(viewerReloads, []);
  assert.equal(screen.queryByRole("button", { name: "Reload view" }), null);
});

test("FileContent_SourceSelected_InfoShowsViewerSelectedBeforeSource", async () => {
  render(element("README.md", VIEWERS, undefined, renderInfo));

  await selectTab("source");
  assert.equal(screen.getByTestId("info").textContent, "info markdown-preview:preview");

  await selectTab("other:outline");
  await selectTab("source");
  await selectTab("source");
  assert.equal(screen.getByTestId("info").textContent, "info other:outline");
});

test("FileContent_WithoutReloadHandlers_HasNoReloadButton", async () => {
  render(element("README.md", VIEWERS));

  assert.equal(screen.queryByRole("button", { name: "Reload view" }), null);
  await selectTab("source");
  assert.equal(screen.queryByRole("button", { name: "Reload file" }), null);
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

function menuTexts(menu: HTMLElement): string[] {
  return Array.from(menu.querySelectorAll('[role="menuitem"]'), (item) =>
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
