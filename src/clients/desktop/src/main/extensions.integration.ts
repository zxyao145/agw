// extension 完整流程的集成测试：运行生产主进程与 renderer，通过 DOM 操作宿主页面，并读取主进程的 frame 登记。
// Full-flow extension integration test: runs the production main process and renderer, drives the host page through
// the DOM, and reads the main-process frame registry.
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import { connect } from "node:net";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import {
  app,
  BrowserWindow,
  dialog,
  session,
  type WebContents,
  type WebContentsView,
} from "electron";

import { config } from "./extensions-integration-setup";
import { extensionPartition } from "./extensions/extension-session";

// 运行 dist/main 中的生产主进程，使 preload 与资源路径与正式运行相同。
// Runs the production main bundle in dist/main so preload and resource paths match a normal launch.
const { integrationTestHooks } = require(resolve("dist/main/index.js")) as typeof import("./index");
const hooks = integrationTestHooks();
const runtime = () => hooks.extensionRuntime();
const host = (): WebContents => {
  const window = hooks.mainWindow();
  if (!window || window.isDestroyed()) throw new Error("The main window is not available.");
  return window.webContents;
};
const storage = (extensionId: string) => join(config.userData, "extension-data", extensionId);

/* ---------- 通用辅助 Helpers ---------- */

function pass(message: string): void {
  console.log(`PASS ${message}`);
}

async function until<T>(
  check: () => T | Promise<T>,
  label: string,
  timeoutMs = 20_000,
  describe?: () => Promise<string>,
): Promise<NonNullable<T>> {
  const deadline = Date.now() + timeoutMs;
  let last: unknown;
  while (Date.now() < deadline) {
    try {
      const value = await check();
      if (value) return value as NonNullable<T>;
    } catch (error) {
      last = error;
    }
    await delay(100);
  }
  const details = describe ? ` (${await describe()})` : "";
  throw new Error(`Timed out waiting for ${label}${last ? `: ${String(last)}` : ""}${details}`);
}

/** 弹出层与视图状态，供超时信息使用。Overlay and view state for timeout messages. */
async function overlayState(frame: FrameHandle): Promise<string> {
  const overlays = await evalHost<string>(
    `JSON.stringify([...document.querySelectorAll('[data-slot$="-content"], [data-slot$="-overlay"], [data-sonner-toast]')].map((element) => ({ slot: element.dataset.slot ?? "toast", state: element.dataset.state, rect: element.getBoundingClientRect(), visibility: getComputedStyle(element).visibility })))`,
  );
  const slot = await evalHost<string>(
    `JSON.stringify(document.querySelector('[data-frame-id="${frame.frameId}"]')?.getBoundingClientRect() ?? null)`,
  );
  const visibility = runtime().views.get(frame.frameId)?.visibility;
  const documentState = await evalHost<string>("document.visibilityState");
  return `overlays ${overlays}; slot ${slot}; visible ${frame.view.getVisible()}; visibility ${JSON.stringify(visibility)}; document ${documentState}`;
}

/** 占位组件中的截图已加载。The snapshot in the frame's slot has loaded. */
function snapshotLoaded(frameId: string): string {
  return `(() => {
    const image = document.querySelector('[data-frame-id="${frameId}"] img');
    return Boolean(image?.complete && image.naturalWidth > 0);
  })()`;
}

function evalHost<T>(code: string): Promise<T> {
  return host().executeJavaScript(code, true) as Promise<T>;
}

function untilHost(code: string, label: string, timeoutMs?: number) {
  return until(() => evalHost<boolean>(code), label, timeoutMs);
}

async function clickHost(selector: string): Promise<void> {
  await untilHost(`Boolean(document.querySelector(${JSON.stringify(selector)}))`, selector);
  await evalHost(`document.querySelector(${JSON.stringify(selector)}).click(); true`);
}

/** 按可见文字点击按钮或菜单项。Clicks a button or menu item by its visible text. */
async function clickText(text: string, scope = "document"): Promise<void> {
  const code = `(() => {
    const root = ${scope};
    const items = [...root.querySelectorAll('button, [role="menuitem"], a')];
    const item = items.find((element) => element.textContent.replace(/\\s+/g, " ").trim().includes(${JSON.stringify(text)}));
    if (!item) return false;
    item.click();
    return true;
  })()`;
  await untilHost(code, `clickable "${text}"`);
}

// 关闭动画期间旧菜单仍在 DOM 中，只在打开的菜单内查找。A closing menu stays in the DOM, so search only the open one.
const OPEN_MENU = `document.querySelector('[data-slot="dropdown-menu-content"][data-state="open"]')`;
const OPEN_CONTEXT_MENU = `document.querySelector('[data-slot="context-menu-content"][data-state="open"]')`;
const OPEN_DIALOG = `document.querySelector('[data-slot="dialog-content"][data-state="open"]')`;

/** Radix 菜单按 pointerdown 打开。Radix menus open on pointerdown. */
async function openMenu(selector: string): Promise<void> {
  await untilHost(`Boolean(document.querySelector(${JSON.stringify(selector)}))`, selector);
  await evalHost(`(() => {
    const trigger = document.querySelector(${JSON.stringify(selector)});
    trigger.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, button: 0, ctrlKey: false, pointerType: "mouse" }));
    return true;
  })()`);
}

async function pressEscape(): Promise<void> {
  await evalHost(
    `document.activeElement?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })) ?? true`,
  );
}

/** 设置 React 受控输入框的值。Sets a React-controlled input value. */
async function setInput(selector: string, value: string): Promise<void> {
  await untilHost(`Boolean(document.querySelector(${JSON.stringify(selector)}))`, selector);
  await evalHost(`(() => {
    const input = document.querySelector(${JSON.stringify(selector)});
    const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), "value").set;
    setter.call(input, ${JSON.stringify(value)});
    input.dispatchEvent(new Event("input", { bubbles: true }));
    return true;
  })()`);
}

function frames(extensionId?: string) {
  return runtime()
    .views.list()
    .filter((frame) => extensionId === undefined || frame.extensionId === extensionId);
}

function frameOf(extensionId: string, kind: string) {
  return frames(extensionId).find((frame) => frame.kind === kind);
}

async function untilFrame(extensionId: string, kind: string) {
  const frame = await until(() => {
    const found = frameOf(extensionId, kind);
    return found && runtime().registry.get(found.frameId)?.pageId ? found : null;
  }, `${extensionId} ${kind} frame`);
  return frame;
}

type FrameHandle = { frameId: string; view: WebContentsView };

function evalFrame<T>(frame: FrameHandle, code: string): Promise<T> {
  return frame.view.webContents.executeJavaScript(code, true) as Promise<T>;
}

/** 在 frame 中等待 `window.api` 可用后执行。Runs code in a frame once `window.api` is available. */
async function api<T>(frame: FrameHandle, code: string): Promise<T> {
  await until(() => evalFrame<boolean>(frame, "Boolean(window.api)"), "window.api");
  return evalFrame<T>(frame, `(async () => { const api = window.api; ${code} })()`);
}

async function errorCode(frame: FrameHandle, code: string): Promise<string> {
  return api<string>(
    frame,
    `try { ${code}; return "ok"; } catch (error) { return error.code ?? error.message; }`,
  );
}

async function serviceState(): Promise<{
  requests: string[];
  neverClosed: number;
  hub: { held: number; heldClosed: number; hubOpen: number; delayHub: boolean };
  echoInputs: string[];
}> {
  return (await fetch(`${config.testServiceUrl}/__test/state`)).json();
}

/** 用第一个 token 直接读取 Host 数据。Reads Host data directly with the first token. */
async function hostApi<T>(path: string): Promise<T> {
  const response = await fetch(`${config.hostUrl}${path}`, {
    headers: { accept: "application/json", authorization: `Bearer ${config.firstToken}` },
  });
  assert.equal(response.ok, true, `${path} -> ${response.status}`);
  const body = (await response.json()) as { data: T };
  return body.data;
}

async function readJson<T>(path: string): Promise<T> {
  return JSON.parse(await readFile(path, "utf8")) as T;
}

/* ---------- 导航 Navigation ---------- */

async function openSettingsPage(href: string): Promise<void> {
  const onSettings = await evalHost<boolean>(
    "Boolean(document.querySelector('.agw-settings-nav'))",
  );
  if (!onSettings) await clickHost('a[aria-label="Settings"]');
  await clickHost(`.agw-settings-nav a[href^="${href}?"]`);
  await untilHost(`location.pathname.startsWith(${JSON.stringify(href)})`, href);
}

async function goToExtensions(): Promise<void> {
  await openSettingsPage("/extensions/");
}

async function goToChat(): Promise<void> {
  if (await evalHost<boolean>("location.pathname.startsWith('/desktop/chat')")) return;
  await clickHost("a.agw-back-button");
  await untilHost("Boolean(document.querySelector('.agw-ribbon'))", "the chat workspace");
}

async function extensionStatus(id: string): Promise<string | null> {
  return evalHost<string | null>(
    `document.querySelector('[data-extension-id="${id}"] [data-slot="badge"]')?.textContent ?? null`,
  );
}

async function untilStatus(id: string, status: string, timeoutMs = 20_000): Promise<void> {
  await until(
    async () => (await extensionStatus(id)) === status,
    `${id} to be ${status}`,
    timeoutMs,
  );
}

/** 在 Extensions 页面打开启用开关并批准。Turns on the enable switch on the Extensions page and approves. */
async function enable(id: string, name: string): Promise<string> {
  await goToExtensions();
  await untilHost(`!${OPEN_DIALOG}`, "earlier dialogs to close");
  await clickHost(`[data-extension-id="${id}"] button[role="switch"]`);
  const dialogText = await until(
    () => evalHost<string | null>(`${OPEN_DIALOG}?.textContent ?? null`),
    "the approval dialog",
  );
  assert.match(dialogText, new RegExp(`Enable ${name}`, "u"));
  await clickText("Enable", OPEN_DIALOG);
  return dialogText;
}

async function disable(id: string): Promise<void> {
  await goToExtensions();
  await clickHost(`[data-extension-id="${id}"] button[role="switch"]`);
}

async function reloadExtension(id: string): Promise<void> {
  await goToExtensions();
  await clickText("Reload", `document.querySelector('[data-extension-id="${id}"]')`);
}

/** 通过 sample 的 workspace API 打开任意主区视图。Opens any main view through sample's workspace API. */
async function openMainView(viewId: string, params: unknown = {}): Promise<void> {
  const background = await untilFrame("sample", "background");
  await api(
    background,
    `await api.workspace.openMainView(${JSON.stringify(viewId)}, ${JSON.stringify(params)});`,
  );
}

/* ---------- 测试步骤 Steps ---------- */

async function boot(): Promise<void> {
  await app.whenReady();
  await until(() => hooks.mainWindow() && !host().isLoading(), "the main window");
  // 宿主页面的错误输出到测试日志。Host page errors go to the test log.
  host().on("console-message", (event) => {
    if (event.level === "error" || event.level === "warning")
      console.log(`[host ${event.level}] ${event.message}`);
  });
  // 窗口被其他窗口遮住时页面处于 hidden 状态，动画帧与计时器会暂停。An occluded window pauses frames and timers.
  app.focus({ steal: true });
  hooks.mainWindow()!.show();
  hooks.mainWindow()!.focus();
  await untilHost("Boolean(window.agwDesktop)", "the Desktop bridge");
  if (config.phase === "first") {
    await evalHost(
      `window.agwDesktop.saveToken(${JSON.stringify(config.remoteProfile.id)}, ${JSON.stringify(config.firstToken)}).then(() => true)`,
    );
  }
  await evalHost(
    `location.assign(${JSON.stringify(`/desktop/chat/?projectId=${config.projectId}`)}); true`,
  );
  await delay(500);
  await untilHost("Boolean(document.querySelector('.agw-ribbon'))", "the chat workspace", 60_000);
  await untilHost(
    `Boolean(document.querySelector('.agw-left-sidebar[data-view-id="agw.conversations"]'))`,
    "the conversation list",
  );
  pass("Desktop boots into the three-region layout");
}

async function beforeEnabling(): Promise<void> {
  const entries = await evalHost<string[]>(
    `[...document.querySelectorAll('.agw-ribbon [data-entry-id]')].map((item) => item.dataset.entryId)`,
  );
  assert.deepEqual(entries, ["agw.ribbon.conversations", "agw.ribbon.files"]);
  const hostInstanceId = runtime().registry.hostInstanceId;
  assert.ok(hostInstanceId);
  const result = await evalHost<{ ok: boolean; error?: { code: string } }>(
    `window.agwDesktop.extensionFileSystem({ hostInstanceId: ${JSON.stringify(hostInstanceId)}, extensionId: "sample", frameId: "test", pageId: "test" }, { operation: "readFile", uri: "file:///etc/hosts" })`,
  );
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "ERR_EXTENSION_DISABLED");
  pass("before enabling, the Ribbon has only built-in items and capability calls are refused");
}

type SettingsFile = {
  extensions: Record<string, { enabled: boolean; grantedPermissions: string[] }>;
  removedBundledExtensions: string[];
};

async function bundledDefaults(): Promise<void> {
  await goToExtensions();
  await untilStatus("markdown-preview", "Active");
  await untilStatus("excalidraw", "Active");
  assert.equal(await evalHost<boolean>(`Boolean(${OPEN_DIALOG})`), false);
  const settings = await readJson<SettingsFile>(join(config.userData, "settings.json"));
  assert.equal(settings.extensions["markdown-preview"].enabled, true);
  assert.deepEqual(settings.extensions["markdown-preview"].grantedPermissions, ["fs.cloud.read"]);
  assert.equal(settings.extensions.excalidraw.enabled, true);
  assert.deepEqual([...settings.extensions.excalidraw.grantedPermissions].sort(), [
    "fs.cloud.read",
    "fs.cloud.write",
  ]);
  assert.deepEqual(settings.removedBundledExtensions, []);
  await goToChat();
  pass("bundled extensions are active on first launch with their permissions approved");
}

async function enableSample(): Promise<void> {
  const dialogText = await enable("sample", "Sample");
  for (const permission of [
    "context.user",
    "context.project",
    "context.agent",
    "fs.cloud.read",
    "fs.cloud.write",
    "execution",
    "a2a",
    "chat.composer",
    "clipboard.read",
    "clipboard.write",
  ]) {
    assert.ok(dialogText.includes(permission), `approval lists ${permission}`);
  }
  assert.ok(
    dialogText.includes(new URL(config.testServiceUrl).host),
    "approval lists the host permission",
  );
  await untilStatus("sample", "Active");
  const settings = await readJson<{
    extensions: Record<string, { enabled: boolean; grantedPermissions: string[] }>;
  }>(join(config.userData, "settings.json"));
  assert.equal(settings.extensions.sample.enabled, true);
  assert.equal(settings.extensions.sample.grantedPermissions.length, 10);
  await goToChat();
  await untilHost(
    `Boolean(document.querySelector('.agw-ribbon [data-entry-id="sample:open"]'))`,
    "sample Ribbon items",
  );
  pass("enabling sample asks for approval, records it, and adds its Ribbon items");
}

async function processIsolation(): Promise<void> {
  await enable("react-ts", "React TS");
  await untilStatus("react-ts", "Active");
  await goToChat();
  await clickHost('.agw-ribbon [data-entry-id="sample:open"]');
  await clickHost('.agw-ribbon [data-entry-id="sample:list"]');
  const background = await untilFrame("sample", "background");
  const main = await untilFrame("sample", "mainView");
  const sidebar = await untilFrame("sample", "sidebarView");
  await openMainView("react-ts:app");
  const react = await untilFrame("react-ts", "mainView");
  const hostPid = host().getOSProcessId();
  for (const frame of [background, main, sidebar, react]) {
    assert.notEqual(frame.view.webContents.getOSProcessId(), hostPid);
  }
  const storagePath = (id: string) =>
    session.fromPartition(extensionPartition(id)).getStoragePath();
  for (const frame of [background, main, sidebar]) {
    assert.notEqual(
      frame.view.webContents.getOSProcessId(),
      react.view.webContents.getOSProcessId(),
    );
    assert.equal(frame.view.webContents.session.getStoragePath(), storagePath("sample"));
  }
  assert.equal(react.view.webContents.session.getStoragePath(), storagePath("react-ts"));
  await openMainView("sample:app");
  pass("extension frames run in their own processes and partitions");
}

async function slotBounds(frameId: string) {
  return evalHost<{ x: number; y: number; width: number; height: number }>(`(() => {
    const rect = document.querySelector('[data-frame-id="${frameId}"]').getBoundingClientRect();
    return { x: rect.left, y: rect.top, width: rect.width, height: rect.height };
  })()`);
}

async function assertBoundsMatch(
  frame: ReturnType<typeof frameOf> & object,
  label: string,
): Promise<void> {
  await until(async () => {
    const slot = await slotBounds(frame.frameId);
    const zoom = host().getZoomFactor();
    const bounds = frame.view.getBounds();
    return (
      Math.abs(bounds.x - slot.x * zoom) <= 1 &&
      Math.abs(bounds.y - slot.y * zoom) <= 1 &&
      Math.abs(bounds.width - slot.width * zoom) <= 1 &&
      Math.abs(bounds.height - slot.height * zoom) <= 1
    );
  }, `bounds to follow the slot (${label})`);
}

async function viewsAndLayout(): Promise<void> {
  await goToChat();
  const main = await untilFrame("sample", "mainView");
  const sidebar = await untilFrame("sample", "sidebarView");
  const header = await evalHost<string>(
    `document.querySelector('[data-extension-view="sample:app"] .agw-extension-view-header').textContent`,
  );
  assert.match(header, /Sample/u);
  await assertBoundsMatch(main, "initial main view");
  await assertBoundsMatch(sidebar, "initial sidebar view");

  const window = hooks.mainWindow()!;
  const [width, height] = window.getSize();
  window.setSize(width - 120, height - 80);
  await assertBoundsMatch(main, "after resizing the window");
  window.setSize(width, height);
  host().setZoomFactor(1.25);
  await assertBoundsMatch(main, "after zooming the host");
  host().setZoomFactor(1);
  await evalHost(`(() => {
    const handle = document.querySelector('.cursor-col-resize');
    const rect = handle.getBoundingClientRect();
    handle.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, clientX: rect.left, clientY: rect.top + 10 }));
    document.dispatchEvent(new MouseEvent("mousemove", { bubbles: true, clientX: rect.left + 60, clientY: rect.top + 10 }));
    document.dispatchEvent(new MouseEvent("mouseup", { bubbles: true, clientX: rect.left + 60, clientY: rect.top + 10 }));
    return true;
  })()`);
  await assertBoundsMatch(main, "after dragging the sidebar");
  await assertBoundsMatch(sidebar, "after dragging the sidebar");
  assert.equal(main.view.getVisible(), true);

  const instance = await evalFrame<string>(main, "window.instance");
  await api(main, `await api.workspace.openMainView("agw.chat");`);
  await until(() => !main.view.getVisible(), "the retained view to hide");
  assert.equal(frameOf("sample", "mainView")?.frameId, main.frameId);
  await clickHost('.agw-ribbon [data-entry-id="sample:open"]');
  await until(() => main.view.getVisible(), "the retained view to show");
  assert.equal(await evalFrame<string>(main, "window.instance"), instance);
  pass("views follow their slots and retained main views keep their page state");
}

async function hostOverlays(): Promise<void> {
  await goToChat();
  const main = await untilFrame("sample", "mainView");
  const sidebar = await untilFrame("sample", "sidebarView");
  await until(() => main.view.getVisible(), "the main view");

  await openMenu("button.agw-task-button");
  await until(() => !main.view.getVisible(), "the main view to be replaced by a snapshot");
  // view 在截图加载并画出之后才隐藏。The view hides only after the snapshot loaded and was painted.
  assert.equal(await evalHost<boolean>(snapshotLoaded(main.frameId)), true);
  await untilHost(
    `Boolean(document.querySelector('[data-slot="dropdown-menu-content"]'))`,
    "the menu",
  );
  await pressEscape();
  await until(() => main.view.getVisible(), "the main view to come back");
  await untilHost(
    `!document.querySelector('[data-frame-id="${main.frameId}"] img')`,
    "the snapshot to go away",
  );

  // 对话设置对话框位于对话列表的标题中。The chat settings dialog sits in the conversation list header.
  assert.equal(sidebar.view.getVisible(), true);
  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
  await until(() => !frameOf("sample", "sidebarView"), "the sample sidebar view to close");
  await clickHost('button[aria-label="Open chat settings"]');
  await untilHost(
    `Boolean(document.querySelector('[data-slot="dialog-content"]'))`,
    "the chat settings dialog",
  );
  await until(() => !main.view.getVisible(), "the modal dialog to cover the main view");
  await untilHost(
    `Boolean(document.querySelector('[data-frame-id="${main.frameId}"] img'))`,
    "the main view snapshot",
  );
  await pressEscape();
  await until(() => main.view.getVisible(), "the main view after the dialog closes");

  // 通知显示在标题栏位置；多行内容使它向下延伸到视图上。The toast sits at the title bar; many lines make it reach the view.
  await api(main, `api.ui.notify({ type: "info", message: "Overlay toast ".repeat(60) });`);
  await untilHost(`Boolean(document.querySelector('[data-sonner-toast]'))`, "the toast");
  const state = () => overlayState(main);
  await until(() => !main.view.getVisible(), "the toast to cover the main view", 20_000, state);
  await clickHost("[data-sonner-toast] [data-close-button]");
  await untilHost(`!document.querySelector('[data-sonner-toast]')`, "the toast to close");
  await until(() => main.view.getVisible(), "the main view after the toast", 20_000, state);

  // 快速打开并关闭菜单 20 次。Open and close a menu 20 times quickly.
  await evalHost(`(async () => {
    const trigger = document.querySelector("button.agw-task-button");
    for (let index = 0; index < 20; index += 1) {
      trigger.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, button: 0, pointerType: "mouse" }));
      await new Promise((resolve) => requestAnimationFrame(resolve));
      document.activeElement?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    }
    return true;
  })()`);
  await delay(1500);
  assert.equal(main.view.getVisible(), true, await state());
  assert.equal(
    await evalHost<boolean>(
      `Boolean(document.querySelector('[data-frame-id="${main.frameId}"] img'))`,
    ),
    false,
  );
  await evalFrame(
    main,
    "window.__clicks = 0; document.addEventListener('mousedown', () => window.__clicks++); true",
  );
  const bounds = main.view.getBounds();
  main.view.webContents.sendInputEvent({
    type: "mouseDown",
    x: Math.round(bounds.width / 2),
    y: 40,
    button: "left",
    clickCount: 1,
  });
  main.view.webContents.sendInputEvent({
    type: "mouseUp",
    x: Math.round(bounds.width / 2),
    y: 40,
    button: "left",
    clickCount: 1,
  });
  await until(() => evalFrame<number>(main, "window.__clicks"), "the view to receive a click");

  const hostInstanceId = JSON.stringify(runtime().registry.hostInstanceId);
  const snapshots = await evalHost<{ frameId: string; snapshot: string | null }[]>(`(async () => {
    const obscured = window.agwDesktop.obscureExtensionFrames(${hostInstanceId}, ["${main.frameId}"]);
    window.agwDesktop.revealExtensionFrames(${hostInstanceId}, ["${main.frameId}"]);
    return obscured;
  })()`);
  assert.equal(snapshots[0].snapshot, null);
  await delay(300);
  assert.equal(main.view.getVisible(), true);
  pass("host overlays swap covered views for snapshots, and stale captures are discarded");
}

async function leaveChatRoute(): Promise<void> {
  await goToChat();
  await clickHost('a[aria-label="Settings"]');
  await untilHost("Boolean(document.querySelector('.agw-settings-nav'))", "Settings");
  await until(
    () => frames().every((frame) => frame.kind === "background" || !frame.view.getVisible()),
    "extension views to be hidden",
  );
  await openSettingsPage("/extensions/");
  await until(
    () => frames().every((frame) => frame.kind === "background"),
    "view frames to close with the shell",
  );
  await goToChat();
  pass("leaving the chat route hides and closes extension views while Settings stays usable");
}

async function contextAndFiles(): Promise<void> {
  await clickHost('.agw-ribbon [data-entry-id="sample:open"]');
  const main = await untilFrame("sample", "mainView");
  type Context = {
    project: { id: string; name: string; directories: { uri: string }[] } | null;
    target: { type: string; id: string; name: string; description: string | null } | null;
    user: { id: string; isAdmin: boolean } | null;
    extension: { storageUri: string };
  };
  const readContext = () => api<Context>(main, "return api.context.get();");
  const context = await until(
    async () => {
      const value = await readContext();
      return value.project && value.target && value.user ? value : null;
    },
    "the project, target and user in the context",
    20_000,
    async () => JSON.stringify(await readContext()),
  );
  const session = await hostApi<{ userId: string; isAdmin: boolean }>("/api/auth/session");
  const projects = await hostApi<{ id: string; name: string }[]>("/api/projects");
  const agents =
    await hostApi<{ id: string; name: string; description: string | null }[]>("/api/agents");
  assert.equal(context.project!.id, config.projectId);
  assert.equal(
    context.project!.name,
    projects.find((project) => project.id === config.projectId)?.name,
  );
  const agent = agents.find((item) => item.id === context.target!.id);
  assert.ok(agent, `the target ${context.target!.id} is a Host Agent`);
  assert.deepEqual(
    {
      type: context.target!.type,
      name: context.target!.name,
      description: context.target!.description,
    },
    { type: "agent", name: agent.name, description: agent.description ?? null },
  );
  assert.deepEqual(
    { id: context.user!.id, isAdmin: context.user!.isAdmin },
    { id: session.userId, isAdmin: session.isAdmin },
  );
  const targets = await api<Record<string, unknown>[]>(main, "return api.context.listTargets();");
  assert.ok(targets.some((target) => target.name === config.agentName));
  assert.ok(targets.every((target) => !("systemPrompt" in target)));
  pass("context matches the Host data and listTargets omits configuration");

  const storageUri = context.extension.storageUri;
  assert.equal(
    await api<string>(
      main,
      `await api.fs.writeFile(${JSON.stringify(`${storageUri}/config.json`)}, '{"theme":"dark"}');
       return api.fs.readTextFile(${JSON.stringify(`${storageUri}/config.json`)});`,
    ),
    '{"theme":"dark"}',
  );
  await api(
    main,
    `
    window.__changes = [];
    window.__watch = await api.fs.watch(${JSON.stringify(`${storageUri}/config.json`)}, {}, (event) => window.__changes.push(event));`,
  );
  await delay(300);
  await api(
    main,
    `await api.fs.writeFile(${JSON.stringify(`${storageUri}/config.json`)}, '{"theme":"light"}');`,
  );
  await until(
    () => evalFrame<boolean>(main, "window.__changes.some((event) => event.type === 'changed')"),
    "a changed event",
  );
  pass("the storage directory is readable, writable and watchable");

  const directory = context.project!.directories[0].uri;
  await api(
    main,
    `await api.fs.writeFile(${JSON.stringify(`${directory}ext/written.md`)}, "from extension");`,
  );
  assert.equal(
    await readFile(join(config.projectDirectory, "ext", "written.md"), "utf8"),
    "from extension",
  );
  assert.equal(
    await api<string>(
      main,
      `return api.fs.readTextFile(${JSON.stringify(`${directory}readme.md`)});`,
    ),
    "hello cloud\n",
  );
  assert.equal(
    await errorCode(main, `await api.fs.readFile(${JSON.stringify(`${directory}missing.md`)})`),
    "ENOENT",
  );

  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.files"]');
  await untilHost(
    `[...document.querySelectorAll('.agw-left-sidebar *')].some((item) => item.textContent === "readme.md")`,
    "the file tree",
  );
  await evalHost(`(() => {
    const node = [...document.querySelectorAll('.agw-left-sidebar *')].find((item) => item.textContent === "readme.md");
    const rect = node.getBoundingClientRect();
    node.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, clientX: rect.left + 5, clientY: rect.top + 5 }));
    return true;
  })()`);
  await clickText("Read with Sample · Sample", OPEN_CONTEXT_MENU);
  const explorerFile = join(storage("sample"), "explorer-read.json");
  const explorerRead = await until(
    async () => (existsSync(explorerFile) ? readJson<{ text: string }>(explorerFile) : null),
    "the explorer action to read the file",
  );
  assert.equal(explorerRead.text, "hello cloud\n");
  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
  pass(
    "cloud files are readable and writable, and the explorer context menu reaches the extension",
  );
}

async function execution(): Promise<void> {
  const main = await untilFrame("sample", "mainView");
  const result = await api<{ conversationId: string; text: string }>(
    main,
    `
    const session = await api.execution.createSession({ projectId: ${JSON.stringify(config.projectId)} });
    const finished = new Promise((resolve) => {
      const texts = [];
      session.onMessage((message) => {
        if (message.role === "assistant") {
          for (const content of message.contents ?? []) if (typeof content.content === "string") texts.push(content.content);
        }
        if (message.additionalProperties?.type === "agw-turn-finished") resolve(texts.join(""));
      });
    });
    await session.send({ target: { type: "agent", id: ${JSON.stringify(config.agentId)} }, text: "integration hello" });
    const text = await finished;
    await session.dispose();
    return { conversationId: session.conversationId, text };`,
  );
  assert.match(result.text, /Echo: integration hello/u);
  // 刷新后列表最上方是最新的对话。After the refresh the newest conversation is at the top of the list.
  await untilHost(
    `document.querySelectorAll('.agw-left-sidebar[data-view-id="agw.conversations"] div.cursor-pointer').length > 0`,
    "the conversation in the list",
  );
  await evalHost(
    `document.querySelector('.agw-left-sidebar[data-view-id="agw.conversations"] div.cursor-pointer').click(); true`,
  );
  await untilHost(
    `new URLSearchParams(location.search).get("conversationId") === ${JSON.stringify(result.conversationId)}`,
    "the conversation to open",
  );
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.textContent.includes("Echo: integration hello")`,
    "the conversation in the chat view",
  );
  const codes = await api<string[]>(
    main,
    `
    const sessions = [];
    for (let index = 0; index < 4; index += 1) sessions.push(await api.execution.createSession({ projectId: ${JSON.stringify(config.projectId)} }));
    let fifth = "ok";
    try { await api.execution.createSession({ projectId: ${JSON.stringify(config.projectId)} }); } catch (error) { fifth = error.code; }
    for (const session of sessions) await session.dispose();
    return [fifth];`,
  );
  assert.deepEqual(codes, ["ERR_TOO_MANY_SESSIONS"]);
  pass(
    "extensions run Agents, the conversation appears in the list and chat, and sessions are limited to four",
  );
}

async function a2aAndHttp(): Promise<void> {
  await openMainView("sample:app");
  const main = await untilFrame("sample", "mainView");
  const agents = await api<{ name: string }[]>(main, "return api.a2a.listAgents();");
  assert.ok(agents.some((agent) => agent.name === config.agentName));
  const reply = await api<{ task?: { status: { state: string } } }>(
    main,
    `return api.a2a.request(${JSON.stringify(config.agentName)}, {
    method: "SendMessage",
    params: { message: { role: "ROLE_USER", messageId: crypto.randomUUID(), parts: [{ text: "a2a hello" }] } },
  });`,
  );
  // Server 当前的 SendMessage 结果不含 Agent 输出，因此检查任务完成且回显 Agent 收到了这条消息。
  // The Server's SendMessage result currently omits the Agent output, so check the task completed and the echo Agent got the message.
  assert.equal(reply.task?.status.state, "TASK_STATE_COMPLETED", JSON.stringify(reply));
  assert.ok((await serviceState()).echoInputs.includes("a2a hello"));
  pass("A2A lists the Agent and SendMessage runs it");

  const ok = await api<string>(
    main,
    `const response = await api.http.request({ url: ${JSON.stringify(`${config.testServiceUrl}/ok`)} }); return response.text();`,
  );
  assert.equal(ok, "ok");
  assert.equal(
    await errorCode(
      main,
      `await api.http.request({ url: ${JSON.stringify(`${config.testServiceUrl}/redirect-out`)} })`,
    ),
    "ERR_HOST_NOT_PERMITTED",
  );
  pass("http.request reaches the declared service and refuses redirects to undeclared hosts");
}

async function chatContributions(): Promise<void> {
  await openMainView("agw.chat");
  await openMenu('button[aria-label="Add"]');
  await clickText("Insert greeting · Sample", OPEN_MENU);
  await untilHost(
    `[...document.querySelectorAll("textarea")].some((area) => area.value.includes("Hello from sample"))`,
    "the inserted greeting",
  );
  pass("the composer + menu runs the extension command that inserts text");

  await untilHost(`!${OPEN_MENU}`, "the composer menu to close");
  await evalHost(`(() => {
    const trigger = [...document.querySelectorAll('.agw-msg-actions button')].find((button) => button.getAttribute("aria-label") === "More message actions");
    trigger.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, button: 0, pointerType: "mouse" }));
    return true;
  })()`);
  await clickText("Open in Sample · Sample", OPEN_MENU);
  const main = await untilFrame("sample", "mainView");
  const received = await until(
    () =>
      evalFrame<string | null>(
        main,
        "(window.lastShowParams ?? window.lastParams)?.message?.text || null",
      ),
    "the main view to receive the message",
  );
  // 打开的对话中第一条消息是 execution 步骤发送的文本。The first message of the open conversation is the text sent earlier.
  assert.equal(received, "integration hello");
  pass("message actions open the extension view with the message");
}

async function undeclaredExtension(): Promise<void> {
  await enable("undeclared", "Undeclared");
  await untilStatus("undeclared", "Active");
  await goToChat();
  await openMainView("undeclared:app");
  const page = await untilFrame("undeclared", "mainView");
  assert.equal(
    await evalFrame(page, `fetch("https://example.com/").then(() => "loaded", () => "blocked")`),
    "blocked",
  );
  // 很长的地址用于检查 Extensions 页面中的换行。A very long URL checks wrapping on the Extensions page.
  const longUrl = `https://example.com/${"long-path-segment".repeat(30)}`;
  assert.equal(
    await evalFrame(
      page,
      `fetch(${JSON.stringify(longUrl)}).then(() => "loaded", () => "blocked")`,
    ),
    "blocked",
  );
  const before = await evalFrame<string>(page, "location.href");
  await evalFrame(page, `location.href = "https://example.com/"; true`).catch(() => undefined);
  await delay(800);
  assert.equal(await evalFrame<string>(page, "location.href"), before);
  const windows = BrowserWindow.getAllWindows().length;
  assert.equal(await evalFrame(page, `window.open("https://example.com/") === null`), true);
  assert.equal(BrowserWindow.getAllWindows().length, windows);
  assert.equal(
    await errorCode(page, `await api.fs.readFile("file:///etc/hosts")`),
    "ERR_PERMISSION_DENIED",
  );
  assert.equal(
    await evalFrame(
      page,
      `fetch("agw-extension://sample/index.html").then(() => "loaded", () => "blocked")`,
    ),
    "blocked",
  );
  // 离开对话页面会关闭视图 frame，所以页面相关的检查在前。Leaving the chat page closes view frames, so page checks come first.
  await goToExtensions();
  await clickText("Details", `document.querySelector('[data-extension-id="undeclared"]')`);
  await untilHost(
    `document.querySelector('[data-extension-id="undeclared"] [data-testid="blocked-requests"]')?.textContent.includes(${JSON.stringify(longUrl)})`,
    "the blocked request on the Extensions page",
  );
  const overflow = await evalHost<{ list: number[]; card: number[] }>(`(() => {
    const card = document.querySelector('[data-extension-id="undeclared"]');
    const list = card.querySelector('[data-testid="blocked-requests"]');
    return { list: [list.scrollWidth, list.clientWidth], card: [card.scrollWidth, card.clientWidth] };
  })()`);
  assert.ok(
    overflow.list[0] <= overflow.list[1],
    `the blocked request list wraps: ${JSON.stringify(overflow)}`,
  );
  assert.ok(
    overflow.card[0] <= overflow.card[1],
    `the extension card does not overflow: ${JSON.stringify(overflow)}`,
  );
  pass("undeclared hosts, navigation, popups, local files and other extensions are blocked");
  await goToChat();
}

async function hookOrderAndReload(): Promise<void> {
  assert.deepEqual(await readJson<string[]>(join(storage("sample"), "hooks.json")), [
    "onInstalled:install:null",
    "activate",
  ]);
  await reloadExtension("sample");
  await untilStatus("sample", "Active");
  await until(
    async () => (await readJson<string[]>(join(storage("sample"), "hooks.json"))).length === 4,
    "hooks after reload",
  );
  assert.deepEqual(await readJson<string[]>(join(storage("sample"), "hooks.json")), [
    "onInstalled:install:null",
    "activate",
    "deactivate",
    "activate",
  ]);
  assert.equal(frames("sample").filter((frame) => frame.kind === "background").length, 1);
  await goToChat();
  pass("hooks run in order and Reload does not call onInstalled again");

  // Extensions 页面的 Inspect 为 background 打开可用的 DevTools。Inspect on the Extensions page opens working DevTools.
  await goToExtensions();
  await clickText("Details", `document.querySelector('[data-extension-id="sample"]')`);
  await clickText("Inspect", `document.querySelector('[data-extension-id="sample"]')`);
  const background = await untilFrame("sample", "background");
  const contents = background.view.webContents;
  await until(
    async () =>
      contents.isDevToolsOpened() &&
      contents.devToolsWebContents !== null &&
      !contents.devToolsWebContents.isLoading() &&
      (await contents.devToolsWebContents.executeJavaScript("document.readyState")) === "complete",
    "the background DevTools to load",
  );
  assert.match(contents.devToolsWebContents!.getURL(), /^devtools:\/\/devtools\/bundled\//u);
  assert.equal(await evalFrame<boolean>(background, "typeof window.api === 'object'"), true);
  contents.closeDevTools();
  await goToChat();
  pass("Inspect opens DevTools for the background, where window.api is available");
}

async function failureModes(): Promise<void> {
  await enable("no-background", "No Background");
  await untilStatus("no-background", "Active");
  await goToChat();
  await openMainView("no-background:app");
  await untilFrame("no-background", "mainView");
  pass("an extension without background activates directly and opens its view");

  await enable("broken", "Broken");
  await untilStatus("broken", "Failed");
  assert.match(
    await evalHost<string>(`document.querySelector('[data-extension-id="broken"]').textContent`),
    /broken on purpose/u,
  );
  assert.equal(await extensionStatus("sample"), "Active");
  pass("a failing activate marks only that extension as failed");

  const neverRequests = async () =>
    (await serviceState()).requests.filter((line) => line === "GET /never").length;
  const requestsBefore = await neverRequests();
  await enable("slow", "Slow");
  await untilStatus("slow", "Activating");
  // activate 发出请求后再停用，才能检查请求被关闭。Disable after activate sent its request, so its closing can be checked.
  await until(async () => (await neverRequests()) > requestsBefore, "the pending request");
  const neverClosed = (await serviceState()).neverClosed;
  await disable("slow");
  await untilStatus("slow", "Disabled", 3000);
  assert.equal(frames("slow").length, 0);
  await until(
    async () => (await serviceState()).neverClosed > neverClosed,
    "the pending request connection to close",
  );
  await enable("slow", "Slow");
  await untilStatus("slow", "Failed", 25_000);
  assert.match(
    await evalHost<string>(`document.querySelector('[data-extension-id="slow"]').textContent`),
    /ACTIVATION_TIMEOUT/u,
  );
  await disable("slow");
  pass("disabling during activation finishes at once, and a stuck activate times out");

  await enable("stuck-deactivate", "Stuck Deactivate");
  await untilStatus("stuck-deactivate", "Active");
  await goToChat();
  await openMainView("stuck-deactivate:app");
  await untilFrame("stuck-deactivate", "mainView");
  const started = Date.now();
  await disable("stuck-deactivate");
  await untilStatus("stuck-deactivate", "Disabled", 10_000);
  assert.ok(Date.now() - started >= 4500, "deactivate waited for its timeout");
  assert.ok(existsSync(join(storage("stuck-deactivate"), "view-unload.json")));
  assert.equal(frames("stuck-deactivate").length, 0);
  pass("a stuck deactivate ends after its timeout, after the view unloaded");
}

async function busyExtension(): Promise<void> {
  await enable("busy", "Busy");
  await untilStatus("busy", "Active");
  await goToChat();
  const background = await untilFrame("busy", "background");
  const pid = background.view.webContents.getOSProcessId();
  await openMenu('button[aria-label="Add"]');
  await clickText("Spin forever · Busy", OPEN_MENU);
  await goToExtensions();
  await untilStatus("busy", "Failed", 30_000);
  assert.match(
    await evalHost<string>(`document.querySelector('[data-extension-id="busy"]').textContent`),
    /UNRESPONSIVE/u,
  );
  assert.equal(frames("busy").length, 0);
  await until(
    () => !app.getAppMetrics().some((metric) => metric.pid === pid),
    "the hung process to exit",
  );
  const started = Date.now();
  await evalHost("1 + 1");
  assert.ok(Date.now() - started < 1000, "the host stays responsive");
  assert.equal(await extensionStatus("sample"), "Active");
  pass("a hung background is ended and only its extension fails");

  await reloadExtension("busy");
  await untilStatus("busy", "Active");
  await goToChat();
  await openMainView("busy:app");
  const view = await untilFrame("busy", "mainView");
  const busyBackground = await untilFrame("busy", "background");
  const samePid =
    view.view.webContents.getOSProcessId() === busyBackground.view.webContents.getOSProcessId();
  void evalFrame(view, "document.querySelector('#spin').click()").catch(() => undefined);
  if (!samePid) {
    await untilHost(
      `[...document.querySelectorAll('[data-extension-view="busy:app"] button')].some((button) => button.textContent.includes("Reload view"))`,
      "the view error panel",
      30_000,
    );
    assert.ok(frameOf("busy", "background"), "background keeps running");
    await goToExtensions();
    assert.equal(await extensionStatus("busy"), "Active");
    await goToChat();
    await clickText("Reload view", `document.querySelector('[data-extension-view="busy:app"]')`);
    await untilFrame("busy", "mainView");
  } else {
    await untilHost(
      `[...document.querySelectorAll('[data-extension-view="busy:app"] button')].some((button) => button.textContent.includes("Reload extension"))`,
      "the extension error view",
      30_000,
    );
    await clickText(
      "Reload extension",
      `document.querySelector('[data-extension-view="busy:app"]')`,
    );
    await goToExtensions();
    await untilStatus("busy", "Active");
    await goToChat();
    await openMainView("busy:app");
    await untilFrame("busy", "mainView");
  }
  assert.ok(frameOf("sample", "background"), "sample keeps running");
  pass(
    `a hung view is handled by its process (${samePid ? "shared with background" : "separate"})`,
  );

  const crashed = await untilFrame("busy", "mainView");
  crashed.view.webContents.forcefullyCrashRenderer();
  await untilHost(
    `[...document.querySelectorAll('[data-extension-view="busy:app"] button')].some((button) => /Reload (view|extension)/.test(button.textContent))`,
    "the crash error panel",
    30_000,
  );
  pass("a crashed view shows its error panel");
  await disable("busy");
  await goToChat();
}

async function badView(): Promise<void> {
  await enable("bad-view", "Bad View");
  await untilStatus("bad-view", "Active");
  await goToChat();
  await openMainView("bad-view:app");
  const frame = await until(() => frameOf("bad-view", "mainView"), "the bad view frame");
  const contents = frame.view.webContents;
  await untilHost(
    `[...document.querySelectorAll('[data-extension-view="bad-view:app"] button')].some((button) => button.textContent.includes("Reload view"))`,
    "the handshake error panel",
    25_000,
  );
  assert.ok(contents.isDestroyed());
  await goToExtensions();
  assert.equal(await extensionStatus("bad-view"), "Active");
  assert.ok(frameOf("bad-view", "background"));
  await disable("bad-view");
  await goToChat();
  pass("a view that never connects shows an error panel while its extension stays active");
}

async function closeReasons(): Promise<void> {
  await goToChat();
  await openMainView("sample:app");
  const main = await untilFrame("sample", "mainView");
  const background = await untilFrame("sample", "background");
  const hubBefore = (await serviceState()).hub.hubOpen;
  await api(main, `await window.openTrackedSession(${JSON.stringify(config.projectId)});`);
  await api(background, `await window.openTrackedSession(${JSON.stringify(config.projectId)});`);
  await until(
    async () => (await serviceState()).hub.hubOpen >= hubBefore + 2,
    "two session connections",
  );
  await disable("sample");
  await untilStatus("sample", "Disabled", 10_000);
  // 停用只能在 Extensions 页面进行；离开对话路由时视图先按 frame-unloaded 关闭，background 随后按 deactivated 关闭。
  // Disabling happens on the Extensions page: leaving the chat route closes the view with frame-unloaded first,
  // then deactivation closes the background with deactivated.
  for (const [file, reason] of [
    ["background-close.json", "deactivated"],
    ["main-close.json", "frame-unloaded"],
  ] as const) {
    const record = await readCloseRecord(file);
    assert.deepEqual(
      record.closes.map((close) => close.reason),
      [reason],
      file,
    );
    assert.ok(record.closes[0].at <= record.hookAt, `${file}: onClose ran before the hook`);
    assertRefusedWhileClosing(record, file);
  }
  await until(
    async () => (await serviceState()).hub.hubOpen <= hubBefore,
    "session connections to close",
  );
  pass(
    "closing frames end their sessions before the hooks run and refuse new sessions and watchers",
  );

  await enable("sample", "Sample");
  await untilStatus("sample", "Active");
  await goToChat();
  await clickHost('.agw-ribbon [data-entry-id="sample:list"]');
  const sidebar = await untilFrame("sample", "sidebarView");
  const sidebarHubBefore = (await serviceState()).hub.hubOpen;
  await api(sidebar, `await window.openTrackedSession(${JSON.stringify(config.projectId)});`);
  await until(
    async () => (await serviceState()).hub.hubOpen > sidebarHubBefore,
    "the sidebar session connection",
  );
  const storageUri = await api<string>(sidebar, "return api.context.get().extension.storageUri;");
  await api(
    sidebar,
    `window.__sidebarWatch = await api.fs.watch(${JSON.stringify(storageUri)}, {}, () => {});`,
  );
  assert.equal(runtime().frameResources(sidebar.frameId).watchers, 1);
  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
  await until(() => !frameOf("sample", "sidebarView"), "the sidebar view to close");
  const record = await readCloseRecord("sidebar-close.json");
  assert.deepEqual(
    record.closes.map((close) => close.reason),
    ["frame-unloaded"],
  );
  assertRefusedWhileClosing(record, "sidebar-close.json");
  assert.equal(runtime().frameResources(sidebar.frameId).watchers, 0);
  await until(
    async () => (await serviceState()).hub.hubOpen <= sidebarHubBefore,
    "the sidebar session connection to close",
  );
  pass("unloading a view closes its sessions with frame-unloaded and releases its watchers");

  await fetch(`${config.testServiceUrl}/__test/delay-hub?enabled=true`);
  const held = (await serviceState()).hub;
  await clickHost('.agw-ribbon [data-entry-id="sample:list"]');
  const pendingSidebar = await untilFrame("sample", "sidebarView");
  await api(
    pendingSidebar,
    `void window.openTrackedSession(${JSON.stringify(config.projectId)}).catch(() => undefined); return true;`,
  );
  await until(async () => (await serviceState()).hub.held > held.held, "the held hub connection");
  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
  await until(
    async () => (await serviceState()).hub.heldClosed > held.heldClosed,
    "the held connection to close",
  );
  await fetch(`${config.testServiceUrl}/__test/delay-hub?enabled=false`);
  const pending = await until(async () => {
    const current = await readCloseRecord("sidebar-close.json");
    return current.opens.length > 0 ? current : null;
  }, "the pending session record");
  assert.deepEqual(
    pending.opens.map((open) => open.outcome),
    ["ERR_FRAME_CLOSING"],
  );
  const hub = (await serviceState()).hub;
  assert.equal(hub.held, hub.heldClosed);
  pass("an unfinished createSession is aborted when its view closes");
}

type CloseRecord = {
  hookAt: number;
  closes: { reason: string; at: number }[];
  duringClose: { operation: string; outcome: string | null }[];
  duringHook: { operation: string; outcome: string | null }[];
  opens: { outcome: string | null }[];
};

function readCloseRecord(file: string): Promise<CloseRecord> {
  return readJson<CloseRecord>(join(storage("sample"), file));
}

/** 关闭期间（onClose 与关闭钩子中）新建会话与监听都被拒绝。New sessions and watchers are refused while closing. */
function assertRefusedWhileClosing(record: CloseRecord, file: string): void {
  assert.equal(record.duringHook.length, 2, `${file}: the closing hook tried both operations`);
  assert.equal(
    record.duringClose.length,
    record.closes.length * 2,
    `${file}: every onClose tried both operations`,
  );
  for (const attempt of [...record.duringClose, ...record.duringHook]) {
    assert.equal(attempt.outcome, "ERR_FRAME_CLOSING", `${file}: ${attempt.operation}`);
  }
}

async function otherStacks(): Promise<void> {
  for (const [id, name, text] of [
    ["react-ts", "React TS", "react-ts ok"],
    ["wasm", "WASM", "42"],
    ["vue", "Vue", "vue ok"],
    ["blazor", "Blazor", "blazor ok"],
  ] as const) {
    await goToExtensions();
    if ((await extensionStatus(id)) !== "Active") {
      await enable(id, name);
      await untilStatus(id, "Active");
    }
    await goToChat();
    await openMainView(`${id}:app`);
    const frame = await untilFrame(id, "mainView");
    await until(
      () =>
        evalFrame<string>(frame, "document.querySelector('#result')?.textContent ?? ''").then(
          (value) => value.includes(text),
        ),
      `${id} to render`,
      60_000,
    );
    pass(`${id} renders and completes context and storage calls`);
  }
}

async function zipInstall(): Promise<void> {
  await goToExtensions();
  const original = dialog.showOpenDialog;
  // 系统文件选择对话框无法自动操作，这里替换为返回 fixture 路径。The native picker cannot be automated, so it returns the fixture path.
  dialog.showOpenDialog = (async () => ({
    canceled: false,
    filePaths: [join(config.fixtures, "sample-v2.zip")],
  })) as typeof dialog.showOpenDialog;
  try {
    await clickText("Install from .zip");
    await untilHost(`${OPEN_DIALOG}?.textContent.includes("1.0.0 → 1.1.0")`, "the version change");
    await clickText("Replace", OPEN_DIALOG);
  } finally {
    dialog.showOpenDialog = original;
  }
  await untilStatus("sample", "Disabled");
  await untilHost(
    `document.querySelector('[data-extension-id="sample"]').textContent.includes("Permission approval required")`,
    "the approval badge",
  );
  await enable("sample", "Sample");
  await untilStatus("sample", "Active");
  await until(
    async () =>
      (await readJson<string[]>(join(storage("sample"), "hooks.json"))).includes(
        "onInstalled:update:1.0.0",
      ),
    "onInstalled(update)",
  );
  pass(
    "installing a zip with a new permission disables the extension until approved, then calls onInstalled(update)",
  );
}

async function connectionChanges(): Promise<void> {
  type Entry = { project: { id: string } | null; conversation: { id: string } | null };
  const logFile = join(storage("sample"), "context-log.json");
  const log = async () => (existsSync(logFile) ? readJson<Entry[]>(logFile) : []);
  // 清空次数：project 从有值变为 null 的次数。Clears: transitions of project from a value to null.
  const clears = (entries: Entry[]) =>
    entries.filter(
      (entry, index) => index > 0 && entry.project === null && entries[index - 1].project !== null,
    ).length;
  const editProfile = async (label: string, change: { url?: string; token?: string }) => {
    await goToChat();
    // 对话页面写回当前 Project 后再修改。Edit once the chat page has written the current Project back.
    await until(
      async () => (await log()).at(-1)?.project?.id === config.projectId,
      `the current project (${label})`,
    );
    const start = (await log()).length - 1;
    await openSettingsPage("/settings/");
    await clickHost(`button[aria-label="Edit ${config.remoteProfile.name}"]`);
    if (change.url) await setInput("#server-profile-url", change.url);
    if (change.token) await setInput("#server-profile-token", change.token);
    await clickHost('[data-slot="dialog-content"][data-state="open"] button[type="submit"]');
    await untilHost(`!${OPEN_DIALOG}`, "the profile dialog to close");
    await until(
      async () => clears((await log()).slice(start)) > 0,
      `a context clear after ${label}`,
    );
    await delay(1500);
    const entries = (await log()).slice(start);
    assert.equal(clears(entries), 1, `${label}: ${JSON.stringify(entries)}`);
    assert.equal(entries.at(-1)?.project, null);
    assert.equal(entries.at(-1)?.conversation, null);
  };
  await editProfile("the URL change", {
    url: config.remoteProfile.baseUrl.replace("127.0.0.1", "localhost"),
  });
  await editProfile("the token change", { token: config.secondToken });
  const background = await untilFrame("sample", "background");
  const projects = await api<{ id: string }[]>(background, "return api.context.listProjects();");
  assert.ok(projects.some((project) => project.id === config.projectId));
  await goToChat();
  pass("changing the profile URL or token clears the context once and requests keep working");
}

/** 在 Files 中点击根目录下的文件。Clicks a root file in Files. */
async function clickFileInTree(name: string): Promise<void> {
  await goToChat();
  if (
    !(await evalHost<boolean>(
      `Boolean(document.querySelector('.agw-left-sidebar[data-view-id="agw.files"]'))`,
    ))
  ) {
    await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.files"]');
  }
  const item = `[...document.querySelectorAll('.agw-left-sidebar *')].find((element) => element.textContent === ${JSON.stringify(name)} && element.children.length === 0)`;
  await untilHost(`Boolean(${item})`, `${name} in the file tree`);
  await evalHost(`${item}.click(); true`);
}

/** 在 Files 中点击根目录下的文件并等待文件视图显示它。Clicks a root file in Files and waits for the File view to show it. */
async function openFileInTree(name: string): Promise<void> {
  await clickFileInTree(name);
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.getAttribute('data-view-id') === "agw.file"`,
    "the file view",
  );
  await untilHost(shownFileIs(name), `the file view to show ${name}`);
}

function shownFileIs(name: string): string {
  return `[...document.querySelectorAll('.agw-main-workspace span.font-medium')].some((element) => element.textContent === ${JSON.stringify(name)})`;
}

function viewerTab(id: string): string {
  return `document.querySelector('[data-file-viewer-tab="${id}"]')`;
}

async function filesView(): Promise<void> {
  await openFileInTree("readme.md");
  // Markdown 文件默认由 Markdown Preview 显示，切换到 Source 显示原文。Markdown opens in Markdown Preview; Source shows the text.
  await untilHost(
    `${viewerTab("markdown-preview:preview")}?.getAttribute("aria-selected") === "true"`,
    "the Preview tab",
  );
  await clickHost('[data-file-viewer-tab="source"]');
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.textContent.includes("hello cloud")`,
    "the file content",
  );
  await openMainView("agw.chat");
  await untilHost(
    `Boolean(document.querySelector('.agw-main-workspace textarea'))`,
    "the composer",
  );
  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
  pass("the Files view opens file content and the chat composer survives switching back");
}

const MARKDOWN_VIEWER = "markdown-preview:preview";
const OPEN_CONFIRMATION = `document.querySelector('[data-host-confirmation][data-state="open"]')`;

function markdownText(includes: string): string {
  return `Boolean(document.querySelector("article.markdown-body")?.textContent.includes(${JSON.stringify(includes)}))`;
}

async function markdownPreview(): Promise<void> {
  await openFileInTree("guide.md");
  await untilHost(
    `${viewerTab(MARKDOWN_VIEWER)}?.getAttribute("aria-selected") === "true"`,
    "the Preview tab",
  );
  const firstPreview = await untilFrame("markdown-preview", "fileViewer");
  const rendered = `(() => {
    const article = document.querySelector("article.markdown-body");
    const image = article?.querySelector('img[alt="Logo"]');
    return article?.querySelector("h1")?.textContent === "Guide"
      && Boolean(article.querySelector("table td"))
      && Boolean(article.querySelector("pre code.hljs .hljs-keyword"))
      && Boolean(image?.src.startsWith("blob:") && image.complete && image.naturalWidth > 0);
  })()`;
  await until(() => evalFrame<boolean>(firstPreview, rendered), "the rendered Markdown");
  pass(
    "Markdown files open in Markdown Preview with headings, tables, highlighted code, and project images",
  );

  // 文件标题行右侧的 "Reload view" 是文件视图中唯一的刷新按钮，点击后用新的 frame 重新加载查看器。
  // "Reload view" next to the file name is the only one in the file view, and it reloads the viewer in a new frame.
  const reloadButtons = `document.querySelectorAll('.agw-main-workspace [aria-label="Reload view"]')`;
  assert.equal(await evalHost<number>(`${reloadButtons}.length`), 1);
  assert.equal(
    await evalHost<boolean>(
      `${reloadButtons}[0].parentElement === document.querySelector('.agw-main-workspace [role="tablist"]').parentElement`,
    ),
    true,
  );
  await evalHost(`${reloadButtons}[0].click(); true`);
  const preview = await until(() => {
    const frame = frameOf("markdown-preview", "fileViewer");
    return frame &&
      frame.frameId !== firstPreview.frameId &&
      runtime().registry.get(frame.frameId)?.pageId
      ? frame
      : null;
  }, "the reloaded preview");
  await until(() => evalFrame<boolean>(preview, rendered), "the Markdown after reloading");
  pass("Reload view next to the file name reloads the viewer");

  // 文件查看器没有宿主标题条，来源显示在 "Reload view" 右侧 Info 按钮的弹出层中。
  // File viewers have no host title bar; the source shows in the popup of the Info button right of "Reload view".
  assert.equal(
    await evalHost<boolean>(
      `Boolean(document.querySelector('.agw-main-workspace .agw-extension-view-header'))`,
    ),
    false,
  );
  assert.equal(
    await evalHost<string | null>(
      `${reloadButtons}[0].nextElementSibling?.getAttribute("aria-label") ?? null`,
    ),
    "View info",
  );
  const infoPopup = `document.querySelector('[data-slot="popover-content"][data-state="open"]')`;
  const pageBeforeInfo = runtime().registry.get(preview.frameId)!.pageId;
  await evalFrame(preview, "window.__infoProbe = 1; true");
  await clickHost('.agw-main-workspace [aria-label="View info"]');
  await untilHost(
    `${infoPopup}?.textContent.includes("Preview") && ${infoPopup}.textContent.includes("Markdown Preview")`,
    "the viewer info popup",
  );
  // 弹出层盖住查看器时换成已画出的截图，页面不重新加载。Under the popup the viewer swaps to a painted snapshot without reloading.
  await until(() => !preview.view.getVisible(), "the preview to be replaced by its snapshot");
  assert.equal(await evalHost<boolean>(snapshotLoaded(preview.frameId)), true);
  await pressEscape();
  await untilHost(`!${infoPopup}`, "the viewer info popup to close");
  await until(() => preview.view.getVisible(), "the preview to show again");
  await untilHost(
    `!document.querySelector('[data-frame-id="${preview.frameId}"] img')`,
    "the preview snapshot to go away",
  );
  assert.equal(runtime().registry.get(preview.frameId)!.pageId, pageBeforeInfo);
  assert.equal(await evalFrame<number>(preview, "window.__infoProbe"), 1);
  // 文件标题行的边框与主区域两侧对齐，没有内边距留下的空隙。The file title row's border spans the main workspace edge to edge.
  const edges = await evalHost<{ header: [number, number]; main: [number, number] }>(`(() => {
    const header = document.querySelector('.agw-main-workspace [role="tablist"]').parentElement.parentElement.getBoundingClientRect();
    const main = document.querySelector('.agw-main-workspace').getBoundingClientRect();
    return { header: [header.left, header.right], main: [main.left, main.right] };
  })()`);
  assert.deepEqual(edges.header, edges.main);
  pass("the Info button right of Reload view names the viewer and its extension");

  await clickHost('[data-file-viewer-tab="source"]');
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.textContent.includes("[Notes](docs/notes.md)")`,
    "the Markdown source",
  );
  // 切换到 Source 时查看器隐藏并保留页面。Switching to Source hides the viewer and keeps its page.
  await until(() => !preview.view.getVisible(), "the preview to hide");
  assert.equal(frameOf("markdown-preview", "fileViewer")?.frameId, preview.frameId);
  await clickHost(`[data-file-viewer-tab="${MARKDOWN_VIEWER}"]`);
  await until(() => preview.view.getVisible(), "the preview to show again");

  await evalFrame(preview, `document.querySelector('a[href="docs/notes.md"]').click(); true`);
  await untilHost(shownFileIs("notes.md"), "docs/notes.md in the file view");
  await until(
    () => evalFrame<boolean>(preview, markdownText("project notes")),
    "the notes preview",
  );
  assert.equal(frameOf("markdown-preview", "fileViewer")?.frameId, preview.frameId);
  pass("Source shows the text, and a relative link opens the linked file in the same viewer");

  await openFileInTree("guide.md");
  await until(() => evalFrame<boolean>(preview, markdownText("Example")), "the guide preview");
  await evalFrame(
    preview,
    `document.querySelector('a[href="https://example.com/docs"]').click(); true`,
  );
  await untilHost(
    `${OPEN_CONFIRMATION}?.getAttribute("data-host-confirmation") === "open-link"
      && ${OPEN_CONFIRMATION}.textContent.includes("https://example.com/docs")
      && ${OPEN_CONFIRMATION}.textContent.includes("Markdown Preview")`,
    "the Open link dialog",
  );
  await clickText("Cancel", OPEN_CONFIRMATION);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the Open link dialog to close");
  await untilHost(shownFileIs("guide.md"), "the guide to stay open");
  pass("an external link asks before opening the browser");

  await disable("markdown-preview");
  await untilStatus("markdown-preview", "Disabled", 10_000);
  assert.equal(frames("markdown-preview").length, 0);
  await openFileInTree("guide.md");
  await untilHost(
    `!document.querySelector('[data-file-viewer-tab]')
      && document.querySelector('.agw-main-workspace')?.textContent.includes("[Notes](docs/notes.md)")`,
    "the Markdown source without viewers",
  );
  await enable("markdown-preview", "Markdown Preview");
  await untilStatus("markdown-preview", "Active");
  await openFileInTree("readme.md");
  await untilHost(
    `${viewerTab(MARKDOWN_VIEWER)}?.getAttribute("aria-selected") === "true"`,
    "the Preview tab after enabling",
  );
  const enabled = await untilFrame("markdown-preview", "fileViewer");
  await until(() => evalFrame<boolean>(enabled, markdownText("hello cloud")), "the readme preview");
  pass(
    "disabling Markdown Preview leaves only the source, and enabling it again restores the preview",
  );
}

const EXCALIDRAW_VIEWER = "excalidraw:editor";
const UNSAVED_MARKER = `Boolean(${viewerTab(EXCALIDRAW_VIEWER)}?.querySelector('[aria-label="Unsaved changes"]'))`;
const DISCARD_DIALOG = `document.querySelector('[data-host-confirmation="discard-unsaved"][data-state="open"]')`;
// 静态画布上的深色像素数，画布尚未渲染时为 -1；暗色主题只给画布加 CSS 滤镜，像素仍是浅色主题。
// Dark pixels on the static canvas, or -1 before it renders; the dark theme only adds a CSS filter, so pixels stay light.
const DRAWN_PIXELS = `(() => {
  const canvas = document.querySelector("canvas.excalidraw__canvas.static");
  if (!canvas || canvas.width === 0) return -1;
  const data = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
  let count = 0;
  for (let index = 0; index < data.length; index += 4) if (data[index] < 128) count += 1;
  return count;
})()`;

/** 在 frame 中按下并松开一个键。Presses and releases a key in a frame. */
function pressKey(
  frame: FrameHandle,
  keyCode: string,
  modifiers: ("meta" | "control")[] = [],
): void {
  const contents = frame.view.webContents;
  contents.focus();
  contents.sendInputEvent({ type: "keyDown", keyCode, modifiers });
  if (modifiers.length === 0) contents.sendInputEvent({ type: "char", keyCode });
  contents.sendInputEvent({ type: "keyUp", keyCode, modifiers });
}

/** 在 frame 中按住左键从 start 拖到 end。Drags with the left button from start to end in a frame. */
function drag(frame: FrameHandle, start: { x: number; y: number }, end: { x: number; y: number }) {
  const contents = frame.view.webContents;
  contents.sendInputEvent({ type: "mouseMove", ...start });
  contents.sendInputEvent({ type: "mouseDown", ...start, button: "left", clickCount: 1 });
  for (let step = 1; step <= 5; step += 1) {
    contents.sendInputEvent({
      type: "mouseMove",
      x: Math.round(start.x + ((end.x - start.x) * step) / 5),
      y: Math.round(start.y + ((end.y - start.y) * step) / 5),
      modifiers: ["leftbuttondown"],
    });
  }
  contents.sendInputEvent({ type: "mouseUp", ...end, button: "left", clickCount: 1 });
}

async function excalidrawEditor(): Promise<void> {
  const diagramPath = join(config.projectDirectory, "diagram.excalidraw");
  await openFileInTree("diagram.excalidraw");
  await untilHost(
    `${viewerTab(EXCALIDRAW_VIEWER)}?.getAttribute("aria-selected") === "true"`,
    "the Excalidraw tab",
  );
  const editor = await untilFrame("excalidraw", "fileViewer");
  await until(
    async () => (await evalFrame<number>(editor, DRAWN_PIXELS)) > 0,
    "the restored diagram",
  );
  await delay(500);
  assert.equal(await evalHost<boolean>(UNSAVED_MARKER), false);
  pass("Excalidraw files open in the Excalidraw editor with the saved scene");

  pressKey(editor, "r");
  await until(
    () =>
      evalFrame<boolean>(
        editor,
        `document.querySelector('[data-testid="toolbar-rectangle"]')?.checked === true`,
      ),
    "the rectangle tool",
  );
  const { width, height } = editor.view.getBounds();
  const start = { x: Math.round(width * 0.55), y: Math.round(height * 0.65) };
  drag(editor, start, { x: start.x + 120, y: start.y + 80 });
  await untilHost(UNSAVED_MARKER, "the unsaved marker");

  await clickFileInTree("guide.md");
  await untilHost(
    `${DISCARD_DIALOG}?.textContent.includes("diagram.excalidraw") && ${DISCARD_DIALOG}.textContent.includes("Excalidraw")`,
    "the discard dialog",
  );
  await clickText("Cancel", DISCARD_DIALOG);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the discard dialog to close");
  assert.equal(await evalHost<boolean>(shownFileIs("diagram.excalidraw")), true);
  assert.equal(await evalHost<boolean>(UNSAVED_MARKER), true);

  await clickHost('a[aria-label="Settings"]');
  await untilHost(`Boolean(${DISCARD_DIALOG})`, "the discard dialog before Settings");
  await clickText("Cancel", DISCARD_DIALOG);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the discard dialog to close");
  assert.equal(await evalHost<boolean>("location.pathname.startsWith('/desktop/chat')"), true);
  pass("unsaved changes show a marker and ask before another file or Settings replaces them");

  pressKey(editor, "s", [process.platform === "darwin" ? "meta" : "control"]);
  await until(async () => {
    const scene = JSON.parse(await readFile(diagramPath, "utf8")) as {
      elements: { type: string; isDeleted: boolean }[];
    };
    return scene.elements.filter((element) => !element.isDeleted).length === 3;
  }, "the saved diagram");
  await untilHost(`!${UNSAVED_MARKER}`, "the unsaved marker to clear");
  await openFileInTree("guide.md");
  assert.equal(await evalHost<boolean>(`Boolean(${OPEN_CONFIRMATION})`), false);
  await until(() => frames("excalidraw").length === 0, "the Excalidraw frame to close");
  pass("saving writes the new element and later file changes need no confirmation");

  await openFileInTree("blank.excalidraw");
  const blank = await untilFrame("excalidraw", "fileViewer");
  await until(async () => (await evalFrame<number>(blank, DRAWN_PIXELS)) === 0, "the blank canvas");
  assert.equal(
    await evalFrame<boolean>(blank, `Boolean(document.querySelector(".status-error"))`),
    false,
  );
  await delay(500);
  assert.equal(await evalHost<boolean>(UNSAVED_MARKER), false);

  // 离开对话页面会关闭查看器，所以在最后检查被拦截的请求。Leaving the chat page closes viewers, so blocked requests come last.
  await goToExtensions();
  await clickText("Details", `document.querySelector('[data-extension-id="excalidraw"]')`);
  await untilHost(
    `Boolean(document.querySelector('[data-extension-id="excalidraw"] [data-slot="collapsible-content"][data-state="open"]'))`,
    "the Excalidraw details",
  );
  assert.equal(
    await evalHost<string | null>(
      `document.querySelector('[data-extension-id="excalidraw"] [data-testid="blocked-requests"]')?.textContent ?? null`,
    ),
    null,
  );
  await goToChat();
  pass("an empty file opens as a blank canvas and Excalidraw loads without blocked requests");
}

async function viewReloads(): Promise<void> {
  await goToChat();
  await openMainView("sample:app");
  const main = await untilFrame("sample", "mainView");
  const storageUri = await api<string>(main, "return api.context.get().extension.storageUri;");
  const watched = `${storageUri}/watched.json`;
  await api(main, `await api.fs.writeFile(${JSON.stringify(watched)}, "0");`);
  for (let round = 1; round <= 5; round += 1) {
    const hubBefore = (await serviceState()).hub.hubOpen;
    const previousPage = runtime().registry.get(main.frameId)!.pageId!;
    await api(
      main,
      `
      await window.openTrackedSession(${JSON.stringify(config.projectId)});
      window.watchEvents = [];
      await window.startWatching(${JSON.stringify(watched)});`,
    );
    await until(
      async () => (await serviceState()).hub.hubOpen > hubBefore,
      "the session connection",
    );
    void evalFrame(main, "location.reload()").catch(() => undefined);
    await until(() => {
      const pageId = runtime().registry.get(main.frameId)?.pageId;
      return pageId && pageId !== previousPage;
    }, "the reloaded page");
    await until(() => evalFrame<boolean>(main, "Boolean(window.api)"), "the reloaded page API");
    await until(
      async () => (await serviceState()).hub.hubOpen <= hubBefore,
      "the previous session connection to close",
    );
    assert.equal(runtime().frameResources(main.frameId, previousPage).watchers, 0);
  }
  await api(
    main,
    `window.watchEvents = []; await window.startWatching(${JSON.stringify(watched)});`,
  );
  await delay(300);
  await writeFile(join(storage("sample"), "watched.json"), "changed");
  await until(
    () => evalFrame<number>(main, "window.watchEvents.length"),
    "one event on the current page",
  );
  await delay(500);
  assert.equal(runtime().frameResources(main.frameId).watchers, 1);
  assert.equal(
    await errorCode(
      main,
      `const session = await api.execution.createSession({ projectId: ${JSON.stringify(config.projectId)} }); await session.dispose()`,
    ),
    "ok",
  );
  pass("reloading a view page releases the old page's sessions and watchers");
}

async function hostReloads(): Promise<void> {
  for (const trigger of ["tray", "crash"] as const) {
    await goToChat();
    await openMainView("sample:app");
    const main = await untilFrame("sample", "mainView");
    const storageUri = await api<string>(main, "return api.context.get().extension.storageUri;");
    await api(
      main,
      `window.watchEvents = []; await window.startWatching(${JSON.stringify(`${storageUri}/watched.json`)});`,
    );
    const before = await serviceState();
    const neverRequests = (state: typeof before) =>
      state.requests.filter((line) => line === "GET /never").length;
    void api(
      main,
      `return api.http.request({ url: ${JSON.stringify(`${config.testServiceUrl}/never`)} });`,
    ).catch(() => undefined);
    await until(
      async () => neverRequests(await serviceState()) > neverRequests(before),
      "the pending request",
    );
    const neverClosed = before.neverClosed;
    const oldContents = frames().map((frame) => frame.view.webContents);
    const proxyPorts = runtime().egressProxyPorts();
    if (trigger === "tray") await hooks.openChatFromTray();
    else host().forcefullyCrashRenderer();
    await until(
      () => oldContents.every((contents) => contents.isDestroyed()),
      "old extension WebContents to be destroyed",
    );
    const children = hooks.mainWindow()!.contentView.children.length;
    assert.equal(children, frames().filter((frame) => frame.kind !== "background").length);
    for (const port of proxyPorts) {
      await assert.rejects(
        new Promise((resolvePromise, reject) => {
          const socket = connect(port, "127.0.0.1", () => resolvePromise(socket));
          socket.on("error", reject);
        }),
      );
    }
    await until(
      async () => (await serviceState()).neverClosed > neverClosed,
      "the pending request to close",
    );
    await untilHost(
      "Boolean(document.querySelector('.agw-ribbon'))",
      "the recovered workspace",
      60_000,
    );
    // Ribbon 项在 extension 进入 active 时注册。Ribbon entries are registered when an extension becomes active.
    await untilHost(
      `["sample:open", "react-ts:open"].every((id) => document.querySelector(\`.agw-ribbon [data-entry-id="\${id}"]\`))`,
      "sample and react-ts to reactivate",
    );
    assert.equal(frames("sample").filter((frame) => frame.kind === "background").length, 1);
    const hooksLog = await readJson<string[]>(join(storage("sample"), "hooks.json"));
    assert.equal(hooksLog.filter((entry) => entry.startsWith("onInstalled")).length, 2);
    pass(
      `host page ${trigger === "tray" ? "reloads from the tray" : "crashes"} clean up every extension resource and reactivate once`,
    );
  }
}

async function rejectedNavigation(): Promise<void> {
  await goToChat();
  await openMainView("sample:app");
  const main = await untilFrame("sample", "mainView");
  const before = frames().length;
  await evalHost(`location.href = "https://example.com/"; true`).catch(() => undefined);
  await delay(800);
  assert.equal(frames().length, before);
  assert.equal(
    (await api<number>(main, "return api.context.get().extension.version.length;")) > 0,
    true,
  );
  await openMainView("undeclared:app");
  const page = await untilFrame("undeclared", "mainView");
  const pageId = runtime().registry.get(page.frameId)!.pageId;
  await evalFrame(page, `location.href = "https://example.com/"; true`).catch(() => undefined);
  await delay(800);
  assert.equal(runtime().registry.get(page.frameId)!.pageId, pageId);
  const storageUri = await api<string>(page, "return api.context.get().extension.storageUri;");
  assert.equal(
    await errorCode(
      page,
      `const watch = await api.fs.watch(${JSON.stringify(storageUri)}, {}, () => {}); watch.dispose()`,
    ),
    "ok",
  );
  pass("rejected navigations leave extensions and their ports running");
}

async function safeMode(): Promise<void> {
  await hooks.reloadWithoutExtensions();
  await untilHost(
    "Boolean(document.querySelector('.agw-ribbon'))",
    "the reloaded workspace",
    60_000,
  );
  await delay(1000);
  assert.equal(runtime().views.list().length, 0);
  assert.equal(runtime().egressProxyPorts().length, 0);
  await goToExtensions();
  await untilHost(
    `Boolean(document.querySelector('[data-testid="safe-mode"]'))`,
    "the safe mode banner",
  );
  await clickText("Resume extensions");
  await untilStatus("sample", "Active");
  assert.equal(frames("sample").filter((frame) => frame.kind === "background").length, 1);
  pass("Reload without extensions enters safe mode, and Resume reactivates");
}

async function uninstallBundled(): Promise<void> {
  await goToExtensions();
  await clickText("Uninstall", `document.querySelector('[data-extension-id="excalidraw"]')`);
  await clickHost("#extension-delete-data");
  await clickText("Uninstall", OPEN_DIALOG);
  await untilHost(
    `!document.querySelector('[data-extension-id="excalidraw"]')`,
    "Excalidraw to leave the list",
  );
  assert.equal(existsSync(storage("excalidraw")), false);
  assert.equal(frames("excalidraw").length, 0);
  const settings = await readJson<SettingsFile>(join(config.userData, "settings.json"));
  assert.deepEqual(settings.removedBundledExtensions, ["excalidraw"]);
  assert.equal(settings.extensions.excalidraw, undefined);
  pass(
    "uninstalling a bundled extension with data removes it from the list and records the removal",
  );
}

async function restartPhase(): Promise<void> {
  await goToExtensions();
  await untilStatus("markdown-preview", "Active", 30_000);
  assert.equal(
    await evalHost<boolean>(`Boolean(document.querySelector('[data-extension-id="excalidraw"]'))`),
    false,
  );
  await openFileInTree("diagram.excalidraw");
  await delay(500);
  assert.equal(
    await evalHost<boolean>(`Boolean(document.querySelector('[data-file-viewer-tab]'))`),
    false,
  );
  pass(
    "after restarting Desktop, Markdown Preview activates and the uninstalled Excalidraw stays absent",
  );

  await goToExtensions();
  await untilStatus("sample", "Active", 30_000);
  assert.equal(frames("sample").filter((frame) => frame.kind === "background").length, 1);
  const hooksLog = await readJson<string[]>(join(storage("sample"), "hooks.json"));
  assert.equal(hooksLog.at(-1), "activate");
  assert.equal(hooksLog.filter((entry) => entry.startsWith("onInstalled")).length, 2);
  pass("after restarting Desktop, sample activates once without onInstalled");

  await disable("sample");
  await untilStatus("sample", "Disabled", 10_000);
  await goToChat();
  assert.equal(
    await evalHost<boolean>(
      `Boolean(document.querySelector('.agw-ribbon [data-entry-id^="sample:"]'))`,
    ),
    false,
  );
  assert.equal(frames("sample").length, 0);
  await goToExtensions();
  await clickText("Uninstall", `document.querySelector('[data-extension-id="sample"]')`);
  await clickHost("#extension-delete-data");
  await clickText("Uninstall", OPEN_DIALOG);
  await until(
    () => !existsSync(join(config.userData, "extensions", "sample")),
    "the install directory to be removed",
  );
  assert.equal(existsSync(storage("sample")), false);
  pass("disabling removes entries and frames, and uninstalling with data removes both directories");
}

async function run(): Promise<void> {
  await boot();
  if (config.phase === "restart") {
    await restartPhase();
    return;
  }
  await beforeEnabling();
  await bundledDefaults();
  await enableSample();
  await processIsolation();
  await viewsAndLayout();
  await hostOverlays();
  await leaveChatRoute();
  await contextAndFiles();
  await execution();
  await a2aAndHttp();
  await chatContributions();
  await undeclaredExtension();
  await hookOrderAndReload();
  await failureModes();
  await busyExtension();
  await badView();
  await closeReasons();
  await otherStacks();
  await zipInstall();
  await connectionChanges();
  await filesView();
  await markdownPreview();
  await excalidrawEditor();
  await viewReloads();
  await hostReloads();
  await rejectedNavigation();
  await safeMode();
  await uninstallBundled();
}

void run().then(
  () => app.exit(0),
  (error: unknown) => {
    console.error(error);
    app.exit(1);
  },
);
