import assert from "node:assert/strict";
import test, { after, afterEach, before, beforeEach } from "node:test";

import type { FileViewerFile } from "@agw/extension-sdk";
import { createScratchDirectory } from "@desktop/main/scratch-directory.test-helper";
import type { ExtensionHost } from "./extension-host";
import { fileViewerInstanceKey, tabInstanceKey } from "./workspace-ids";
import {
  createMainProcess,
  type ExtensionPage,
  sampleExtension,
  startExtensionHost,
} from "./extension-host.test-helper";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
type ViewModule = typeof import("../components/workspace/extension-view");

const PROJECT = "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e";
const VIEWER_ID = "sample:viewer";
const OUTLINE_ID = "sample:outline";
const SCOPE = { serverId: "local", projectId: PROJECT };
let viewerTab: string;
let VIEWER: string;
let OUTLINE: string;
const SAMPLE_ORIGIN = {
  kind: "extension",
  extensionId: "sample",
  extensionName: "Sample",
} as const;

const installed = sampleExtension([
  { id: "viewer", title: "Viewer", icon: "Eye", patterns: ["*.md"], entry: "viewer.html" },
  { id: "outline", title: "Outline", icon: "List", patterns: ["*.md"], entry: "outline.html" },
]);
installed.grantedPermissions = ["chat.composer"];
installed.manifest!.contributes!.mainViews = [
  { id: "main", title: "Main", icon: "File", entry: "main.html" },
];

// setupDomEnvironment 注册关闭 window 的 after 钩子；在 before 中调用时 node:test 在 before 结束后立即运行它，
// window 的事件监听随之移除。模块加载时调用，钩子属于整个文件。
// setupDomEnvironment registers an after hook that closes the window; called inside before, node:test runs it as
// soon as before ends and the window's listeners go with it. Called at module load, the hook belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);
const scratchReady = createScratchDirectory("extension-host-tabs");
after(async () => {
  await main.bridge.getWorkspaceTabs(SCOPE.serverId, SCOPE.projectId);
  await (await scratchReady).dispose();
});

let environment: DomEnvironment;
let views: ViewModule;
let main: ReturnType<typeof createMainProcess>;
let host: ExtensionHost;
let holdTabRead: Promise<void> | null = null;

before(async () => {
  environment = await environmentReady;
  views = await import("../components/workspace/extension-view");
  const { ExtensionHost } = await import("./extension-host");
  main = createMainProcess(environment.window, {
    installed,
    storageDirectory: "/data/sample",
    workspaceTabsDirectory: (await scratchReady).path,
    channels: {
      listExtensions: async () => [
        installed,
        { ...installed, source: "server", instanceId: "sample-server" },
      ],
    },
  });
  host = new ExtensionHost({
    bridge: {
      ...main.bridge,
      getWorkspaceTabs: async (serverId, projectId) => {
        await holdTabRead;
        return main.bridge.getWorkspaceTabs(serverId, projectId);
      },
    },
    app: { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    notify: () => undefined,
  });
  await startExtensionHost(host, environment.window);
  assert.equal(host.registry.view(VIEWER_ID)?.location, "file");
  await host.tabs.setScope(SCOPE);
  const initial = host.tabs.getSnapshot().group!.tabs[0];
  assert.equal(initial.kind, "chat");
  host.tabs.acceptConversation(initial.id, initial.draftId);
});

function fileTab(path: string, preview = false): string {
  host.tabs.openFile({ path, directoryId: null, diffScope: null }, { preview });
  return host.tabs.getSnapshot().group!.activeTabId!;
}

beforeEach(async () => {
  await host.tabs.setScope(SCOPE);
  viewerTab = fileTab("notes.md");
  VIEWER = fileViewerInstanceKey(viewerTab, VIEWER_ID);
  OUTLINE = fileViewerInstanceKey(fileTab("outline.md"), OUTLINE_ID);
});

/** 测试获取后尚未释放的视图。Views a test acquired and has not released yet. */
const acquired = new Set<() => void>();

function acquire(
  instanceKey: string,
  viewId = instanceKey === OUTLINE ? OUTLINE_ID : VIEWER_ID,
): () => void {
  const release = host.acquireView(instanceKey, viewId);
  const releaseOnce = () => {
    if (acquired.delete(releaseOnce)) release();
  };
  acquired.add(releaseOnce);
  return releaseOnce;
}

afterEach(async () => {
  for (
    let confirmation = main.confirmations.current();
    confirmation !== null;
    confirmation = main.confirmations.current()
  ) {
    await main.confirmations.answer(
      confirmation.kind === "message" ? confirmation.cancelId : false,
    );
  }
  for (const release of acquired) release();
  host.releaseScope(SCOPE);
  await host.tabs.setScope(SCOPE);
  await host.tabs.close(host.tabs.getSnapshot().group!.tabs.map((tab) => tab.id));
  await until(
    () => host.frameIdOf(VIEWER) === null && host.frameIdOf(OUTLINE) === null,
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

function pageOf(instanceKey: string): ExtensionPage | undefined {
  const frameId = host.frameIdOf(instanceKey);
  return frameId === null ? undefined : main.pages.get(frameId);
}

function hooks(page: ExtensionPage): string[] {
  return page.calls.map((call) => call.method);
}

/** 确认窗口中显示的内容。What the confirmation window shows. */
function currentConfirmation() {
  return main.confirmations.current();
}

/** 以确认窗口的身份回答。Answers as the confirmation window. */
function answer(value: unknown): Promise<void> {
  return main.confirmations.answer(value);
}

/**
 * 页面报告未保存状态并等到回复；回复到达时宿主已处理页面此前发来的全部消息。
 * The page reports its unsaved state and waits for the reply; by then the host has handled every earlier page message.
 */
async function reportUnsaved(page: ExtensionPage, unsaved: boolean): Promise<void> {
  await (await page.api).workspace.setUnsavedChanges(unsaved);
}

/** 显示文件查看器并等到 onLoad 完成。Shows a file viewer and waits until its onLoad finished. */
async function showLoaded(
  instanceKey: string,
  file: FileViewerFile,
  viewId = instanceKey === OUTLINE ? OUTLINE_ID : VIEWER_ID,
) {
  host.setFileViewerFile(instanceKey, viewId, file);
  const release = acquire(instanceKey, viewId);
  const page = await until(() => pageOf(instanceKey), `the ${viewId} page`);
  await until(() => hooks(page).includes("view.onLoad"), `${viewId} onLoad`);
  await reportUnsaved(page, false);
  return { page, release };
}

test("one file viewer has a frame per file tab and pins only the dirty preview", async () => {
  const first = await showLoaded(VIEWER, fileNamed("notes.md"));
  const id = fileTab("other.md", true);
  const otherKey = fileViewerInstanceKey(id, VIEWER_ID);
  const second = await showLoaded(otherKey, fileNamed("other.md"), VIEWER_ID);
  assert.notEqual(host.frameIdOf(VIEWER), host.frameIdOf(otherKey));
  assert.deepEqual(first.page.calls[0].params, { params: { file: fileNamed("notes.md") } });
  assert.deepEqual(second.page.calls[0].params, { params: { file: fileNamed("other.md") } });
  await reportUnsaved(second.page, true);
  host.setFileViewerFile(otherKey, VIEWER_ID, fileNamed("stale.md"));
  await (await second.page.api).workspace.setUnsavedChanges(true);
  assert.deepEqual(hooks(second.page), ["view.onLoad"]);
  assert.deepEqual(host.getSnapshot().unsavedInstanceKeys, [otherKey]);
  assert.equal(host.tabs.getSnapshot().group!.tabs.find((tab) => tab.id === id)?.preview, false);
  await reportUnsaved(first.page, true);
  const answerForOne = host.confirmDiscardUnsaved({ tabIds: [id] });
  const confirmation = currentConfirmation();
  assert.ok(confirmation?.kind === "discard-unsaved");
  assert.deepEqual(
    confirmation.items.map((item) => item.fileName),
    ["other.md"],
  );
  await answer(false);
  assert.equal(await answerForOne, false);
});

test("openMainView contribution validation rejects through the returned promise", async () => {
  const group = host.tabs.getSnapshot().group;
  const action = { type: "openMainView", view: "sample:main" } as const;
  for (const args of [{ invalid: undefined }, "a".repeat(65535)]) {
    await assert.rejects(host.runAction("sample", action, args), { code: "ERR_INVALID_ARGUMENT" });
    assert.equal(host.tabs.getSnapshot().group, group);
  }
});

test("a missing Server release identity publishes the view instance error", async () => {
  const viewId = "sample-server:main";
  await host.rescan();
  await host.layout.openMainView(viewId);
  const key = tabInstanceKey(host.tabs.getSnapshot().group!.activeTabId!);
  acquire(key, viewId);
  const failure = await until(
    () => host.getSnapshot().instanceErrors[key],
    "the frame creation error",
  );
  assert.equal(failure.code, "LOAD_FAILED");
  assert.match(failure.message, /source and release identity/u);
  assert.equal(host.frameIdOf(key), null);
});

test("leaving a Project keeps its dirty resources until that Project is released", async () => {
  const first = await showLoaded(VIEWER, fileNamed("notes.md"));
  await showLoaded(OUTLINE, fileNamed("outline.md"));
  await reportUnsaved(first.page, true);
  host.setSourceUnsaved(viewerTab, "source.md");
  const retained = host.frameIdOf(VIEWER);
  const otherScope = { ...SCOPE, projectId: "project-b" };
  await host.tabs.setScope(otherScope);
  await until(() => host.frameIdOf(OUTLINE) === null, "the clean frame to close");
  assert.equal(host.frameIdOf(VIEWER), retained);
  assert.deepEqual(host.getSnapshot().unsavedSourceTabIds, [viewerTab]);
  assert.equal(await host.confirmDiscardScope(otherScope), true);
  const decision = host.confirmDiscardScope(SCOPE);
  const confirmation = currentConfirmation();
  assert.ok(confirmation?.kind === "discard-unsaved");
  assert.deepEqual(
    confirmation.items.map((item) => item.fileName),
    ["notes.md", "source.md"],
  );
  await answer(true);
  assert.equal(await decision, true);
  host.releaseScope(SCOPE);
  await until(() => host.frameIdOf(VIEWER) === null, "the discarded frame to close");
  assert.equal(host.hasUnsavedChanges(), false);
});

test("an extension frame loads with its tab parameters and receives a reopen", async () => {
  host.layout.openMainView("sample:main", { a: 1 });
  const id = host.tabs.getSnapshot().group!.activeTabId!;
  const key = tabInstanceKey(id);
  acquire(key, "sample:main");
  const page = await until(() => pageOf(key), "the main frame");
  await until(() => hooks(page).includes("view.onShow"), "the first show");
  assert.deepEqual(page.calls[0], { method: "view.onLoad", params: { params: { a: 1 } } });
  host.layout.openMainView("sample:main", { a: 2 });
  const tab = host.tabs.getSnapshot().group!.tabs.find((item) => item.id === id);
  assert.ok(tab?.kind === "extension");
  host.reopenView(key, tab.params);
  await until(() => page.calls.length === 3, "the second show");
  assert.deepEqual(page.calls.at(-1), { method: "view.onShow", params: { params: { a: 2 } } });
});

test("the workspace context reports the active view and null for an empty group", async () => {
  host.tabs.openJobs();
  assert.equal(host.context.snapshot().workspace.activeMainViewId, "agw.jobs");
  host.layout.openMainView("agw.events");
  assert.equal(host.context.snapshot().workspace.activeMainViewId, "agw.events");
  host.layout.leaveFullWorkspaceView();
  await host.tabs.close(host.tabs.getSnapshot().group!.tabs.map((tab) => tab.id));
  assert.equal(host.context.snapshot().workspace.activeMainViewId, null);
});

test("composer insertion follows the latest visible registration", async () => {
  const { page } = await showLoaded(VIEWER, fileNamed("notes.md"));
  const { composer } = (await page.api).chat;
  const messages: string[] = [];
  const absent = { code: "ERR_UNAVAILABLE", message: "No chat composer is visible." };
  await assert.rejects(composer.insertText("missing"), absent);
  const first = host.registerComposer({ insertText: (text) => messages.push(`first ${text}`) });
  const last = host.registerComposer({ insertText: (text) => messages.push(`last ${text}`) });
  await composer.insertText("two");
  last();
  await composer.insertText("one");
  first();
  assert.deepEqual(messages, ["last two", "first one"]);
  await assert.rejects(composer.insertText("missing"), absent);
});

test("openFile waits for its Project group and opens the tab with dirty files retained", async () => {
  const { page } = await showLoaded(VIEWER, fileNamed("notes.md"));
  await reportUnsaved(page, true);
  const scope = { serverId: "local", projectId: "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d60" };
  const identity = { profileId: "local", baseUrl: "http://localhost", token: null };
  host.context.setConnection(identity, {
    id: "local",
    name: "Local",
    kind: "local",
    serverId: null,
  });
  host.context.setSelection(host.context.connectionKey!, {
    project: { id: scope.projectId, name: "B", description: null, directories: [] },
    conversation: null,
    target: null,
  });
  let release!: () => void;
  holdTabRead = new Promise<void>((resolve) => {
    release = resolve;
  });
  const loading = host.tabs.setScope(scope);
  const api = await page.api;
  const opening = api.workspace.openFile(`agw-cloud://local/${scope.projectId}/workspace/next.md`);
  await api.workspace.setUnsavedChanges(true);
  assert.equal(host.tabs.getSnapshot().group, null);
  holdTabRead = null;
  release();
  await Promise.all([loading, opening]);
  const group = host.tabs.getSnapshot().group!;
  const tab = group.tabs.find((item) => item.id === group.activeTabId);
  assert.ok(tab?.kind === "file");
  assert.equal(tab.path, "next.md");
  assert.equal(tab.preview, false);
  assert.equal(currentConfirmation(), null);

  await host.tabs.setScope(null);
  holdTabRead = new Promise<void>((resolve) => {
    release = resolve;
  });
  const secondLoad = host.tabs.setScope(scope);
  const stale = assert.rejects(
    api.workspace.openFile(`agw-cloud://local/${scope.projectId}/workspace/stale.md`),
    { code: "ERR_UNAVAILABLE" },
  );
  await api.workspace.setUnsavedChanges(true);
  const switchBack = host.tabs.setScope(SCOPE);
  holdTabRead = null;
  release();
  await Promise.all([secondLoad, switchBack, stale]);
  assert.equal(
    host.tabs
      .getSnapshot()
      .group!.tabs.some((item) => item.kind === "file" && item.path === "stale.md"),
    false,
  );
});

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

test("Source registers the window Chat button with the current file context", () => {
  host.setSourceFloatingChatContext({ projectRelativePath: "first.md" });
  assert.deepEqual(host.getSnapshot().floatingChatButton, {
    owner: "source",
    obscured: false,
  });
  host.setObscuredFrames(new Set(), true);
  assert.equal(host.getSnapshot().floatingChatButton?.obscured, true);
  host.setObscuredFrames(new Set(), false);
  assert.equal(host.getSnapshot().floatingChatButton?.obscured, false);
  host.setSourceFloatingChatContext({ projectRelativePath: "second.md" });
  assert.deepEqual(host.getSnapshot().floatingChat?.context, {
    projectRelativePath: "second.md",
  });
  host.closeSourceFloatingChat();
  assert.equal(host.getSnapshot().floatingChatButton, null);
});

test("Events and Notifications keep the Source chat after its component unmounts", async () => {
  host.tabs.activate(viewerTab);
  const context = { projectRelativePath: "notes.md" };
  host.setSourceFloatingChatContext(context);
  host.openFloatingChatButton("source");
  await host.layout.openMainView("agw.events");
  host.closeSourceFloatingChat();
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  assert.deepEqual(host.getSnapshot().floatingChat?.context, context);
  host.closeFloatingChat();
  const button = host.getSnapshot().floatingChatButton!;
  assert.equal(host.getSnapshot().floatingChat?.open, false);
  await host.layout.openMainView("agw.notifications");
  host.openFloatingChatButton(button.owner);
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  host.layout.leaveFullWorkspaceView();
  host.setSourceFloatingChatContext(context);
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  await host.layout.openMainView("agw.events");
  host.closeSourceFloatingChat();
  host.tabs.openJobs();
  host.openFloatingChatButton(button.owner);
  assert.equal(host.getSnapshot().floatingChatButton, null);
  assert.notEqual(host.getSnapshot().floatingChat?.open, true);
});

test("full views retain a viewer's offered chat after hide and clear it on a Project switch", async () => {
  host.tabs.activate(viewerTab);
  const viewer = await showLoaded(VIEWER, fileNamed("notes.md"));
  const context = { projectRelativePath: "notes.md" };
  const handle = await (await viewer.page.api).chat.createFloatingChat({ context });
  await host.layout.openMainView("agw.notifications");
  viewer.release();
  await until(() => hooks(viewer.page).includes("view.onHide"), "the viewer to hide");
  const owner = host.getSnapshot().floatingChatButton?.owner;
  assert.ok(owner);
  await assert.rejects(handle.open(), { code: "ERR_UNAVAILABLE" });
  host.openFloatingChatButton(owner);
  assert.deepEqual(host.getSnapshot().floatingChat?.context, context);
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  await host.tabs.setScope({ ...SCOPE, projectId: "project-b" });
  assert.equal(host.getSnapshot().floatingChatButton, null);
  assert.notEqual(host.getSnapshot().floatingChat?.open, true);
  host.openFloatingChatButton(owner);
  assert.notEqual(host.getSnapshot().floatingChat?.open, true);
  host.layout.leaveFullWorkspaceView();
});

test("a full view keeps the open chat of a destroyed main view and honors Close on return", async () => {
  const viewId = "sample:main";
  await host.layout.openMainView(viewId);
  const key = tabInstanceKey(host.tabs.getSnapshot().group!.activeTabId!);
  const release = acquire(key, viewId);
  const page = await until(() => pageOf(key), "the main view");
  const context = { repository: "demo" };
  const handle = await (await page.api).chat.createFloatingChat({ context });
  await handle.open();
  await host.layout.openMainView("agw.events");
  release();
  await until(() => host.frameIdOf(key) === null, "the main frame to close");
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  assert.deepEqual(host.getSnapshot().floatingChat?.context, context);
  const owner = host.getSnapshot().floatingChatButton!.owner;
  host.closeFloatingChat();
  host.openFloatingChatButton(owner);
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  host.closeFloatingChat();
  host.layout.leaveFullWorkspaceView();
  acquire(key, viewId);
  const replacement = await until(() => pageOf(key), "the new main frame");
  await (await replacement.api).chat.createFloatingChat({ context: { repository: "current" } });
  host.openFloatingChatButton(owner);
  assert.notEqual(host.getSnapshot().floatingChat?.open, true);
  host.openFloatingChatButton(host.getSnapshot().floatingChatButton!.owner);
  assert.deepEqual(host.getSnapshot().floatingChat?.context, { repository: "current" });
});

test("floating Chat handles survive retained-view hide and keep registrations per view", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  const viewerChat = await (
    await viewer.page.api
  ).chat.createFloatingChat({
    context: { projectRelativePath: "viewer.md" },
  });
  const viewerOwner = host.getSnapshot().floatingChatButton?.owner;
  assert.ok(viewerOwner);
  const outlineChat = await (
    await outline.page.api
  ).chat.createFloatingChat({
    context: { projectRelativePath: "outline.md" },
  });

  viewer.release();
  await until(() => hooks(viewer.page).includes("view.onHide"), "the viewer to hide");
  assert.notEqual(host.getSnapshot().floatingChatButton?.owner, viewerOwner);
  await viewerChat.setContext({ projectRelativePath: "viewer-after-hide.md" });
  const showAgain = acquire(VIEWER);
  await until(() => hooks(viewer.page).includes("view.onShow"), "the viewer to show again");
  assert.equal(host.getSnapshot().floatingChatButton?.owner, viewerOwner);

  await viewerChat.open();
  assert.equal(host.getSnapshot().floatingChat?.open, true);
  await viewerChat.close();
  await viewerChat.dispose();
  await outlineChat.setContext({ projectRelativePath: "outline-still-available.md" });
  await outlineChat.dispose();
  assert.equal(host.getSnapshot().floatingChatButton, null);
  showAgain();
  outline.release();
});

test("confirmDiscardUnsaved lists only the unsaved changes of the given viewers", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  await reportUnsaved(viewer.page, true);
  await reportUnsaved(outline.page, true);
  assert.deepEqual(host.getSnapshot().unsavedInstanceKeys, [VIEWER, OUTLINE]);

  const onlyOutline = host.confirmDiscardUnsaved({ instanceKeys: [OUTLINE] });
  assert.deepEqual(currentConfirmation(), {
    kind: "discard-unsaved",
    items: [
      {
        fileName: "notes.md",
        viewerTitle: "Outline",
        origin: SAMPLE_ORIGIN,
      },
    ],
  });
  await answer(false);
  assert.equal(await onlyOutline, false);

  const every = host.confirmDiscardUnsaved();
  assert.deepEqual(currentConfirmation(), {
    kind: "discard-unsaved",
    items: [
      {
        fileName: "notes.md",
        viewerTitle: "Viewer",
        origin: SAMPLE_ORIGIN,
      },
      {
        fileName: "notes.md",
        viewerTitle: "Outline",
        origin: SAMPLE_ORIGIN,
      },
    ],
  });
  await answer(true);
  assert.equal(await every, true);

  assert.equal(await host.confirmDiscardUnsaved({ instanceKeys: ["sample:other"] }), true);
  assert.equal(currentConfirmation(), null);
});

test("the host Source's unsaved file joins every confirmation except a viewer-only one", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  await reportUnsaved(viewer.page, true);
  assert.equal(host.hasUnsavedChanges(), true);
  await reportUnsaved(viewer.page, false);
  assert.equal(host.hasUnsavedChanges(), false);

  host.setSourceUnsaved(viewerTab, "app.ts");
  assert.equal(host.hasUnsavedChanges(), true);
  await reportUnsaved(viewer.page, true);
  const every = host.confirmDiscardUnsaved();
  assert.deepEqual(currentConfirmation(), {
    kind: "discard-unsaved",
    items: [
      { fileName: "notes.md", viewerTitle: "Viewer", origin: SAMPLE_ORIGIN },
      { fileName: "app.ts", viewerTitle: "Source", origin: { kind: "host" } },
    ],
  });
  await answer(false);
  assert.equal(await every, false);

  // 只针对某个 extension 视图的确认不包括 Source。A confirmation for one extension view leaves Source out.
  const onlyViewer = host.confirmDiscardUnsaved({ instanceKeys: [VIEWER] });
  assert.deepEqual(currentConfirmation(), {
    kind: "discard-unsaved",
    items: [{ fileName: "notes.md", viewerTitle: "Viewer", origin: SAMPLE_ORIGIN }],
  });
  await answer(true);
  assert.equal(await onlyViewer, true);

  await reportUnsaved(viewer.page, false);
  host.setSourceUnsaved(viewerTab, null);
  assert.equal(host.hasUnsavedChanges(), false);
  assert.equal(await host.confirmDiscardUnsaved(), true);
  assert.equal(currentConfirmation(), null);
});

test("a host message box shows with the host origin and an abort withdraws it with ERR_CANCELED", async () => {
  const request = {
    type: "warning" as const,
    message: "app.ts was changed on disk after it was opened.",
    detail: "Overwrite it with your version, or reload it and discard your changes.",
    buttons: ["Overwrite", "Reload", "Cancel"],
    defaultId: 2,
    cancelId: 2,
  };
  const answered = host.showHostMessageBox(request, new AbortController().signal);
  await until(() => currentConfirmation() !== null, "the host message box");
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    origin: { kind: "host" },
    ...request,
  });
  await answer(0);
  assert.equal(await answered, 0);

  const closing = new AbortController();
  const withdrawn = host.showHostMessageBox(request, closing.signal);
  await until(() => currentConfirmation() !== null, "the second host message box");
  closing.abort();
  await assert.rejects(withdrawn, { code: "ERR_CANCELED" });
  await until(() => currentConfirmation() === null, "the message box to be withdrawn");
  await assert.rejects(host.showHostMessageBox(request, closing.signal), {
    code: "ERR_CANCELED",
  });
});

test("closing the confirmation window answers as canceled", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  await reportUnsaved(viewer.page, true);

  const closed = host.confirmDiscardUnsaved({ instanceKeys: [VIEWER] });
  assert.equal(currentConfirmation()?.kind, "discard-unsaved");
  main.confirmations.close();
  assert.equal(await closed, false);
  assert.equal(currentConfirmation(), null);
});

test("Reload view asks before discarding unsaved changes and reloads only after Discard", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);
  await reportUnsaved(viewer.page, true);
  await reportUnsaved(outline.page, true);
  const view = host.registry.view(VIEWER_ID)!;
  const frameId = host.frameIdOf(VIEWER)!;
  const created = main.frames.length;

  const canceled = views.reloadExtensionView(host, view, VIEWER);
  assert.deepEqual(currentConfirmation(), {
    kind: "discard-unsaved",
    items: [
      {
        fileName: "notes.md",
        viewerTitle: "Viewer",
        origin: SAMPLE_ORIGIN,
      },
    ],
  });
  await answer(false);
  await canceled;
  assert.equal(host.frameIdOf(VIEWER), frameId);
  assert.equal(main.frames.length, created);
  assert.equal(main.destroyed.includes(frameId), false);
  assert.deepEqual(host.getSnapshot().unsavedInstanceKeys, [VIEWER, OUTLINE]);

  const discarded = views.reloadExtensionView(host, view, VIEWER);
  await answer(true);
  await discarded;
  assert.equal(main.destroyed.includes(frameId), true);
  assert.equal(main.frames.length, created + 1);
  const reloadedFrameId = host.frameIdOf(VIEWER)!;
  assert.notEqual(reloadedFrameId, frameId);
  assert.deepEqual(hooks(main.pages.get(reloadedFrameId)!), ["view.onLoad"]);
  assert.deepEqual(host.getSnapshot().unsavedInstanceKeys, [OUTLINE]);

  // 没有未保存修改时直接重新加载。Without unsaved changes the view reloads at once.
  await views.reloadExtensionView(host, view, VIEWER);
  assert.equal(currentConfirmation(), null);
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
  const discard = host.confirmDiscardUnsaved({ instanceKeys: [VIEWER] });

  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    origin: SAMPLE_ORIGIN,
    type: "warning",
    message: "notes.md was changed on disk after it was opened.",
    detail: "Overwrite it with your version, or reload it and discard your changes.",
    buttons: ["Overwrite", "Reload", "Cancel"],
    defaultId: 2,
    cancelId: 2,
  });
  // 回答的类型与下标必须符合当前确认。The answer must fit the current confirmation.
  await assert.rejects(answer(true));
  await assert.rejects(answer(3));
  const conflictId = main.confirmations.requestId();
  await answer(1);
  assert.equal(await conflict, 1);
  assert.notEqual(main.confirmations.requestId(), conflictId);

  // 缺省的 cancelId 为最后一个按钮。cancelId defaults to the last button.
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    origin: SAMPLE_ORIGIN,
    type: null,
    message: "m".repeat(500),
    detail: "d".repeat(2000),
    buttons: ["1".repeat(40), "2", "3", "4"],
    defaultId: null,
    cancelId: 3,
  });
  await answer(3);
  assert.equal(await longest, 3);

  // 布尔确认排在后面，照常以 true/false 回答。The boolean confirmation queued after them still answers true or false.
  assert.deepEqual(currentConfirmation(), {
    kind: "discard-unsaved",
    items: [
      {
        fileName: "notes.md",
        viewerTitle: "Viewer",
        origin: SAMPLE_ORIGIN,
      },
    ],
  });
  await assert.rejects(answer(0));
  await answer(true);
  assert.equal(await discard, true);
  assert.equal(currentConfirmation(), null);
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
    origin: SAMPLE_ORIGIN,
    type: null,
    message: "First",
    detail: null,
    buttons: ["OK"],
    defaultId: null,
    cancelId: 0,
  });

  await host.tabs.close([viewerTab]);
  await shownFailure;
  await queuedFailure;
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    origin: SAMPLE_ORIGIN,
    type: null,
    message: "Second",
    detail: null,
    buttons: ["OK", "No"],
    defaultId: null,
    cancelId: 1,
  });
  await answer(0);
  assert.equal(await other, 0);
});

/** 预览图片的 SVG 文字。The SVG text of a preview image. */
function svgText(label: string): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><text>${label}</text></svg>`;
}

function svgImage(label: string, name = `${label}.svg`) {
  return { data: new TextEncoder().encode(svgText(label)), type: "image/svg+xml" as const, name };
}

/** 显示中的图片预览；没有时为 undefined，供 `until` 等待。The image preview showing, or undefined for `until`. */
function currentPreview() {
  return host.getSnapshot().imagePreview ?? undefined;
}

/** Blob URL 指向的文字；URL 已释放时为 null。The text a Blob URL points at, or null once revoked. */
async function blobText(src: string): Promise<string | null> {
  return (await environment.objectUrls.get(src)?.text()) ?? null;
}

test("image previews show one at a time, settle when closed, and release their Blob URLs", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const outline = await showLoaded(OUTLINE, file);

  const first = (await viewer.page.api).ui.previewImage(svgImage("first"));
  const shown = await until(currentPreview, "the first preview");
  assert.equal(shown.name, "first.svg");
  assert.match(shown.src, /^blob:/);
  assert.equal(await blobText(shown.src), svgText("first"));

  // 新的预览替换当前预览，被替换的调用完成。A new preview replaces the current one, whose call settles.
  const second = (await outline.page.api).ui.previewImage(svgImage("second"));
  assert.equal(await first, undefined);
  const replaced = await until(
    () => currentPreview()?.name === "second.svg" && currentPreview(),
    "the second preview",
  );
  assert.notEqual(replaced.id, shown.id);
  assert.equal(await blobText(shown.src), null);

  // 关闭旧的预览不影响当前预览。Closing an earlier preview leaves the current one.
  host.closeImagePreview(shown.id);
  assert.equal(host.getSnapshot().imagePreview, replaced);
  host.closeImagePreview(replaced.id);
  assert.equal(await second, undefined);
  assert.equal(host.getSnapshot().imagePreview, null);
  assert.equal(await blobText(replaced.src), null);
});

test("closing a frame closes its image preview and the call fails with ERR_FRAME_GONE", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  await showLoaded(OUTLINE, file);
  const pending = (await viewer.page.api).ui.previewImage(svgImage("closing"));
  const failure = assert.rejects(pending, { code: "ERR_FRAME_GONE" });
  const shown = await until(currentPreview, "the preview");

  await host.tabs.close([viewerTab]);
  await failure;
  assert.equal(host.getSnapshot().imagePreview, null);
  assert.equal(await blobText(shown.src), null);
});

const HEADER_ITEMS = [
  {
    id: "encoding",
    placement: "top" as const,
    label: "UTF-8",
    menu: [
      { id: "reopen", label: "Reopen with Encoding", menu: [{ id: "reopen:gbk", label: "GBK" }] },
    ],
  },
  { id: "export", placement: "bottom" as const, label: "Export" },
];

test("a file viewer's header items show under its view id, stay across files and reach the page when chosen", async () => {
  const viewer = await showLoaded(VIEWER, fileNamed("notes.md"));
  const api = await viewer.page.api;
  const chosen: unknown[] = [];
  api.workspace.onDidSelectFileHeaderItem((selection) => chosen.push(selection));
  await api.workspace.setFileHeaderItems(HEADER_ITEMS);
  assert.deepEqual(host.getSnapshot().fileHeaderItems, { [VIEWER]: HEADER_ITEMS });

  host.selectFileHeaderItem(VIEWER, "encoding", "reopen:gbk");
  host.selectFileHeaderItem(VIEWER, "export", null);
  await until(() => chosen.length === 2, "the choices to reach the page");
  assert.deepEqual(chosen, [
    { itemId: "encoding", entryId: "reopen:gbk" },
    { itemId: "export", entryId: null },
  ]);

  // 条目由页面按新文件更新，切换文件时宿主保留。The page updates its items for the new file; the host keeps them.
  host.setFileViewerFile(VIEWER, VIEWER_ID, fileNamed("other.md"));
  assert.deepEqual(host.getSnapshot().fileHeaderItems[VIEWER], HEADER_ITEMS);

  await api.workspace.setFileHeaderItems([]);
  assert.deepEqual(host.getSnapshot().fileHeaderItems, {});

  // Reload view 关闭 frame，条目随之清除。Reload view closes the frame and its items go with it.
  await api.workspace.setFileHeaderItems(HEADER_ITEMS);
  await host.reloadView(VIEWER);
  assert.deepEqual(host.getSnapshot().fileHeaderItems, {});
});

test("a replaced page's header items are cleared while the frame stays", async () => {
  const viewer = await showLoaded(VIEWER, fileNamed("notes.md"));
  await (await viewer.page.api).workspace.setFileHeaderItems(HEADER_ITEMS);
  const frameId = host.frameIdOf(VIEWER)!;

  main.emit({
    type: "frame-page-replaced",
    extensionId: "sample",
    frameId,
    pageId: main.pageIds.get(frameId)!,
  });
  viewer.page.endpoint.close();
  await until(
    () => host.getSnapshot().fileHeaderItems[VIEWER] === undefined,
    "the header items to clear",
  );
  assert.equal(host.frameIdOf(VIEWER), frameId);
});

test("a replaced page's image preview closes while the frame stays", async () => {
  const file = fileNamed("notes.md");
  const viewer = await showLoaded(VIEWER, file);
  const pending = (await viewer.page.api).ui.previewImage(svgImage("replaced"));
  await until(currentPreview, "the preview");
  const frameId = host.frameIdOf(VIEWER)!;

  main.emit({
    type: "frame-page-replaced",
    extensionId: "sample",
    frameId,
    pageId: main.pageIds.get(frameId)!,
  });
  // 旧文档已经不存在，它一侧的端口随之关闭。The old document is gone and its side of the port closes with it.
  viewer.page.endpoint.close();
  await assert.rejects(pending, { code: "ERR_CONNECTION_CLOSED" });
  await until(() => host.getSnapshot().imagePreview === null, "the preview to close");
  assert.equal(host.frameIdOf(VIEWER), frameId);
});

test("the host shows a view's image preview over the window and settles the call when the user closes it", async () => {
  const { ExtensionImagePreview } = await import("./extension-image-preview");
  const rendered = environment.render(
    environment.React.createElement(ExtensionImagePreview, { host }) as Parameters<
      DomEnvironment["render"]
    >[0],
  );
  const viewer = await showLoaded(VIEWER, fileNamed("notes.md"));
  const pending = (await viewer.page.api).ui.previewImage(svgImage("shown", "diagram.svg"));
  const shown = await until(currentPreview, "the preview");

  const root = await until(
    () => document.querySelector<HTMLElement>('[data-slot="image-preview"]') ?? undefined,
    "the preview root",
  );
  assert.equal(root.getAttribute("role"), "dialog");
  const image = await until(() => root.querySelector("img") ?? undefined, "the preview image");
  assert.equal(image.getAttribute("src"), shown.src);
  assert.equal(image.getAttribute("alt"), "diagram.svg");

  await environment.act(async () => {
    environment.fireEvent.click(environment.screen.getByRole("button", { name: "Close" }));
  });
  assert.equal(await pending, undefined);
  await until(
    () => document.querySelector('[data-slot="image-preview"]') === null,
    "the preview to close",
  );
  rendered.unmount();
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
  const frameId = host.frameIdOf(VIEWER)!;

  main.emit({
    type: "frame-page-replaced",
    extensionId: "sample",
    frameId,
    pageId: main.pageIds.get(frameId)!,
  });
  // 旧文档已经不存在，它一侧的端口随之关闭。The old document is gone and its side of the port closes with it.
  viewer.page.endpoint.close();
  await assert.rejects(replaced, { code: "ERR_CONNECTION_CLOSED" });
  assert.equal(host.frameIdOf(VIEWER), frameId);
  assert.deepEqual(currentConfirmation(), {
    kind: "message",
    origin: SAMPLE_ORIGIN,
    type: null,
    message: "Second",
    detail: null,
    buttons: ["OK", "No"],
    defaultId: null,
    cancelId: 1,
  });
  await answer(0);
  assert.equal(await other, 0);
});
