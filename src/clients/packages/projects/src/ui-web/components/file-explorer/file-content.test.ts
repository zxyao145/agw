import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { FileViewerOption } from "./file-content.tsx";

const { React, act, fireEvent, render, screen } = await setupDomEnvironment();
const { default: FileContent } = await import("./file-content.tsx");

const VIEWERS: FileViewerOption[] = [
  { id: "markdown-preview:preview", title: "Preview", unsaved: false },
  { id: "other:outline", title: "Outline", unsaved: true },
];

function element(
  selectedFile: string,
  fileViewers?: readonly FileViewerOption[],
  onReloadFileViewer?: (viewerId: string) => void,
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode,
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
  });
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
      (viewerId) => React.createElement("span", { "data-testid": "info" }, `info ${viewerId}`),
    ),
  );

  // 文件标题行右侧依次是切换按钮、"Reload view" 与查看器说明。
  // The file title row's right side holds the switch, "Reload view" and the viewer info, in that order.
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
  assert.equal(screen.queryByTestId("info"), null);
});

test("FileContent_ReloadView_ReloadsSelectedViewerAndHidesForSource", async () => {
  const reloads: string[] = [];
  render(element("README.md", VIEWERS, (viewerId) => reloads.push(viewerId)));

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload view" }));
  });
  await selectTab("other:outline");
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Reload view" }));
  });
  await selectTab("source");

  assert.deepEqual(reloads, ["markdown-preview:preview", "other:outline"]);
  assert.equal(screen.queryByRole("button", { name: "Reload view" }), null);
});

test("FileContent_WithoutReloadHandler_HasNoReloadButton", async () => {
  render(element("README.md", VIEWERS));

  assert.equal(screen.queryByRole("button", { name: "Reload view" }), null);
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
