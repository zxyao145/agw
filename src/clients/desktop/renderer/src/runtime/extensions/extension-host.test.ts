import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { FileViewerFile } from "@agw/extension-sdk";
import type { ExtensionHost, HostConfirmation } from "./extension-host";
import {
  createMainProcess,
  type ExtensionPage,
  sampleExtension,
  startExtensionHost,
} from "./extension-host.test-helper";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
type ViewModule = typeof import("../components/workspace/extension-view");

const PROJECT = "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e";
const VIEWER = "sample:viewer";
const OUTLINE = "sample:outline";

const installed = sampleExtension([
  { id: "viewer", title: "Viewer", icon: "Eye", patterns: ["*.md"], entry: "viewer.html" },
  { id: "outline", title: "Outline", icon: "List", patterns: ["*.md"], entry: "outline.html" },
]);

// setupDomEnvironment 注册关闭 window 的 after 钩子；在 before 中调用时 node:test 在 before 结束后立即运行它，
// window 的事件监听随之移除。模块加载时调用，钩子属于整个文件。
// setupDomEnvironment registers an after hook that closes the window; called inside before, node:test runs it as
// soon as before ends and the window's listeners go with it. Called at module load, the hook belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let environment: DomEnvironment;
let views: ViewModule;
let main: ReturnType<typeof createMainProcess>;
let host: ExtensionHost;

before(async () => {
  environment = await environmentReady;
  views = await import("../components/workspace/extension-view");
  const { ExtensionHost } = await import("./extension-host");
  main = createMainProcess(environment.window, { installed, storageDirectory: "/data/sample" });
  host = new ExtensionHost({
    bridge: main.bridge,
    app: { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    notify: () => undefined,
  });
  await startExtensionHost(host, environment.window);
  assert.equal(host.registry.view(VIEWER)?.location, "file");
});

/** 测试获取后尚未释放的视图。Views a test acquired and has not released yet. */
const acquired = new Set<() => void>();

function acquire(viewId: string): () => void {
  const release = host.acquireView(viewId);
  const releaseOnce = () => {
    if (acquired.delete(releaseOnce)) release();
  };
  acquired.add(releaseOnce);
  return releaseOnce;
}

afterEach(async () => {
  for (
    let confirmation = host.getSnapshot().confirmation;
    confirmation !== null;
    confirmation = host.getSnapshot().confirmation
  ) {
    host.resolveConfirmation(confirmation.kind === "message" ? confirmation.cancelId : false);
  }
  for (const release of acquired) release();
  host.retainFileViewers([]);
  await until(
    () => host.frameIdOfView(VIEWER) === null && host.frameIdOfView(OUTLINE) === null,
    "the file viewers to close",
  );
});

async function until<T>(read: () => T | undefined | false, what: string): Promise<T> {
  const deadline = Date.now() + 3_000;
  for (;;) {
    const value = read();
    if (value !== undefined && value !== false) return value;
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${what}.`);
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

function fileNamed(name: string): FileViewerFile {
  return { uri: `agw-cloud://local/${PROJECT}/workspace/${name}`, name, projectId: PROJECT };
}

function pageOf(viewId: string): ExtensionPage | undefined {
  const frameId = host.frameIdOfView(viewId);
  return frameId === null ? undefined : main.pages.get(frameId);
}

function hooks(page: ExtensionPage): string[] {
  return page.calls.map((call) => call.method);
}

/** 当前消息框的 id。The id of the current message box. */
function messageId(): number {
  const confirmation = host.getSnapshot().confirmation;
  assert.equal(confirmation?.kind, "message");
  return (confirmation as Extract<HostConfirmation, { kind: "message" }>).id;
}

/** 当前的确认对话框；`message` 的 id 每次不同，检查后去掉。The current confirmation, without the per-box id of `message`. */
function currentConfirmation() {
  const confirmation = host.getSnapshot().confirmation;
  if (confirmation?.kind !== "message") return confirmation;
  const { id, ...rest } = confirmation;
  assert.ok(Number.isInteger(id));
  return rest;
}

/**
 * 页面报告未保存状态并等到回复；回复到达时宿主已处理页面此前发来的全部消息。
 * The page reports its unsaved state and waits for the reply; by then the host has handled every earlier page message.
 */
async function reportUnsaved(page: ExtensionPage, unsaved: boolean): Promise<void> {
  await (await page.api).workspace.setUnsavedChanges(unsaved);
}

/** 显示文件查看器并等到 onLoad 完成。Shows a file viewer and waits until its onLoad finished. */
async function showLoaded(viewId: string, file: FileViewerFile) {
  host.setFileViewerFile(viewId, file);
  const release = acquire(viewId);
  const page = await until(() => pageOf(viewId), `the ${viewId} page`);
  await until(() => hooks(page).includes("view.onLoad"), `${viewId} onLoad`);
  await reportUnsaved(page, false);
  return { page, release };
}

test("a loaded file viewer gets onShow when it is shown again after being hidden, and only then", async () => {
  const file = fileNamed("notes.md");
  const { page, release } = await showLoaded(VIEWER, file);
  assert.deepEqual(hooks(page), ["view.onLoad"]);

  release();
  await until(() => hooks(page).includes("view.onHide"), "onHide");
  const shownAgain = acquire(VIEWER);
  await until(() => hooks(page).includes("view.onShow"), "onShow after showing again");
  assert.deepEqual(page.calls.at(-1), { method: "view.onShow", params: { params: { file } } });

  // 释放计时器被取消时视图一直显示，不再发送。The release timer is canceled while the view stays shown: nothing is sent.
  shownAgain();
  acquire(VIEWER);
  await reportUnsaved(page, false);
  assert.deepEqual(hooks(page), ["view.onLoad", "view.onHide", "view.onShow"]);
});

test("confirmDiscardUnsaved lists only the unsaved changes of the given viewers", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  await reportUnsaved(viewer.page, true);
  await reportUnsaved(outline.page, true);
  assert.deepEqual(host.getSnapshot().unsavedViewIds, [VIEWER, OUTLINE]);

  const onlyOutline = host.confirmDiscardUnsaved([OUTLINE]);
  assert.deepEqual(host.getSnapshot().confirmation, {
    kind: "discard-unsaved",
    items: [{ fileName: "notes.md", viewerTitle: "Outline", extensionName: "Sample" }],
  });
  host.resolveConfirmation(false);
  assert.equal(await onlyOutline, false);

  const every = host.confirmDiscardUnsaved();
  assert.deepEqual(host.getSnapshot().confirmation, {
    kind: "discard-unsaved",
    items: [
      { fileName: "notes.md", viewerTitle: "Viewer", extensionName: "Sample" },
      { fileName: "notes.md", viewerTitle: "Outline", extensionName: "Sample" },
    ],
  });
  host.resolveConfirmation(true);
  assert.equal(await every, true);

  assert.equal(await host.confirmDiscardUnsaved(["sample:other"]), true);
  assert.equal(host.getSnapshot().confirmation, null);
});

test("Reload view asks before discarding unsaved changes and reloads only after Discard", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  await reportUnsaved(viewer.page, true);
  await reportUnsaved(outline.page, true);
  const view = host.registry.view(VIEWER)!;
  const frameId = host.frameIdOfView(VIEWER)!;
  const created = main.frames.length;

  const canceled = views.reloadExtensionView(host, view);
  assert.deepEqual(host.getSnapshot().confirmation, {
    kind: "discard-unsaved",
    items: [{ fileName: "notes.md", viewerTitle: "Viewer", extensionName: "Sample" }],
  });
  host.resolveConfirmation(false);
  await canceled;
  assert.equal(host.frameIdOfView(VIEWER), frameId);
  assert.equal(main.frames.length, created);
  assert.equal(main.destroyed.includes(frameId), false);
  assert.deepEqual(host.getSnapshot().unsavedViewIds, [VIEWER, OUTLINE]);

  const discarded = views.reloadExtensionView(host, view);
  host.resolveConfirmation(true);
  await discarded;
  assert.equal(main.destroyed.includes(frameId), true);
  assert.equal(main.frames.length, created + 1);
  const reloadedFrameId = host.frameIdOfView(VIEWER)!;
  assert.notEqual(reloadedFrameId, frameId);
  assert.deepEqual(hooks(main.pages.get(reloadedFrameId)!), ["view.onLoad"]);
  assert.deepEqual(host.getSnapshot().unsavedViewIds, [OUTLINE]);

  // 没有未保存修改时直接重新加载。Without unsaved changes the view reloads at once.
  await views.reloadExtensionView(host, view);
  assert.equal(host.getSnapshot().confirmation, null);
  assert.equal(main.frames.length, created + 2);
});

test("message boxes from views join the confirmation queue in order and resolve with the chosen button", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  const viewerApi = await viewer.page.api;
  const outlineApi = await outline.page.api;

  const conflict = viewerApi.dialog.showMessageBox({
    type: "warning",
    message: "notes.md was changed on disk after it was opened.",
    detail: "Overwrite it with your version, or reload it and discard your changes.",
    buttons: ["Overwrite", "Reload", "Cancel"],
    defaultId: 2,
    cancelId: 2,
  });
  await reportUnsaved(viewer.page, true);
  // 参数取上限时同样可以显示。Options at their upper limits are shown too.
  const longest = outlineApi.dialog.showMessageBox({
    message: "m".repeat(500),
    detail: "d".repeat(2000),
    buttons: ["1".repeat(40), "2", "3", "4"],
  });
  await reportUnsaved(outline.page, false);
  const discard = host.confirmDiscardUnsaved([VIEWER]);

  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    extensionName: "Sample",
    type: "warning",
    message: "notes.md was changed on disk after it was opened.",
    detail: "Overwrite it with your version, or reload it and discard your changes.",
    buttons: ["Overwrite", "Reload", "Cancel"],
    defaultId: 2,
    cancelId: 2,
  });
  // 回答的类型与下标必须符合当前对话框。The answer must fit the current dialog.
  assert.throws(() => host.resolveConfirmation(true));
  assert.throws(() => host.resolveConfirmation(3));
  const conflictId = messageId();
  host.resolveConfirmation(1);
  assert.equal(await conflict, 1);
  assert.notEqual(messageId(), conflictId);

  // 缺省的 cancelId 为最后一个按钮。cancelId defaults to the last button.
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    extensionName: "Sample",
    type: null,
    message: "m".repeat(500),
    detail: "d".repeat(2000),
    buttons: ["1".repeat(40), "2", "3", "4"],
    defaultId: null,
    cancelId: 3,
  });
  host.resolveConfirmation(3);
  assert.equal(await longest, 3);

  // 布尔确认排在后面，照常以 true/false 回答。The boolean confirmation queued after them still answers true or false.
  assert.deepEqual(host.getSnapshot().confirmation, {
    kind: "discard-unsaved",
    items: [{ fileName: "notes.md", viewerTitle: "Viewer", extensionName: "Sample" }],
  });
  assert.throws(() => host.resolveConfirmation(0));
  host.resolveConfirmation(true);
  assert.equal(await discard, true);
  assert.equal(host.getSnapshot().confirmation, null);
});

test("closing a frame removes its queued and shown message boxes and the calls fail with ERR_FRAME_GONE", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  const viewerApi = await viewer.page.api;
  const outlineApi = await outline.page.api;

  const shown = viewerApi.dialog.showMessageBox({ message: "First", buttons: ["OK"] });
  const shownFailure = assert.rejects(shown, { code: "ERR_FRAME_GONE" });
  await reportUnsaved(viewer.page, false);
  const other = outlineApi.dialog.showMessageBox({ message: "Second", buttons: ["OK", "No"] });
  await reportUnsaved(outline.page, false);
  const queued = viewerApi.dialog.showMessageBox({ message: "Third", buttons: ["OK"] });
  const queuedFailure = assert.rejects(queued, { code: "ERR_FRAME_GONE" });
  await reportUnsaved(viewer.page, false);
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    extensionName: "Sample",
    type: null,
    message: "First",
    detail: null,
    buttons: ["OK"],
    defaultId: null,
    cancelId: 0,
  });

  host.retainFileViewers([OUTLINE]);
  await shownFailure;
  await queuedFailure;
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    extensionName: "Sample",
    type: null,
    message: "Second",
    detail: null,
    buttons: ["OK", "No"],
    defaultId: null,
    cancelId: 1,
  });
  host.resolveConfirmation(0);
  assert.equal(await other, 0);
});

test("a replaced page's message boxes leave the queue while the frame stays", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  const replaced = (await viewer.page.api).dialog.showMessageBox({
    message: "First",
    buttons: ["OK"],
  });
  await reportUnsaved(viewer.page, false);
  const other = (await outline.page.api).dialog.showMessageBox({
    message: "Second",
    buttons: ["OK", "No"],
  });
  await reportUnsaved(outline.page, false);
  const frameId = host.frameIdOfView(VIEWER)!;

  main.emit({
    type: "frame-page-replaced",
    extensionId: "sample",
    frameId,
    pageId: main.pageIds.get(frameId)!,
  });
  // 旧文档已经不存在，它一侧的端口随之关闭。The old document is gone and its side of the port closes with it.
  viewer.page.endpoint.close();
  await assert.rejects(replaced, { code: "ERR_CONNECTION_CLOSED" });
  assert.equal(host.frameIdOfView(VIEWER), frameId);
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    extensionName: "Sample",
    type: null,
    message: "Second",
    detail: null,
    buttons: ["OK", "No"],
    defaultId: null,
    cancelId: 1,
  });
  host.resolveConfirmation(0);
  assert.equal(await other, 0);
});
