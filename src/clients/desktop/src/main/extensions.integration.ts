// extension 完整流程的集成测试：运行生产主进程与 renderer，通过 DOM 操作宿主页面，并读取主进程的 frame 登记。
// Full-flow extension integration test: runs the production main process and renderer, drives the host page through
// the DOM, and reads the main-process frame registry.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import { connect } from "node:net";
import { basename, join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { isDeepStrictEqual } from "node:util";
import {
  app,
  BrowserWindow,
  clipboard,
  dialog,
  nativeImage,
  session,
  type WebContents,
  type WebContentsView,
} from "electron";
import iconv from "iconv-lite";

import { DEFAULT_APPEARANCE } from "../shared/appearance";
import type { AppearanceSettings } from "../shared/contracts";
import { THEME_TOKEN_KINDS, THEME_TOKEN_NAMES } from "../shared/theme-tokens";
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
const storage = (extensionId: string) => join(config.userData, "extensions", "data", extensionId);
// 保存与撤销等快捷键的修饰键。The modifier of shortcuts such as save and undo.
const MOD = process.platform === "darwin" ? "meta" : "control";

// 每个 WebContents 的控制台输出与创建次数，用于检查 CSP 报错与额外打开的窗口。
// Console output and creation count of every WebContents, for checking CSP errors and extra windows.
const consoleMessages: { url: string; level: string; message: string }[] = [];
let createdWebContents = 0;
app.on("web-contents-created", (_event, contents) => {
  createdWebContents += 1;
  contents.on("console-message", (event) => {
    consoleMessages.push({ url: contents.getURL(), level: event.level, message: event.message });
  });
  contents.on("render-process-gone", (_goneEvent, details) => {
    console.log(`[render-process-gone] ${contents.getURL()} ${JSON.stringify(details)}`);
  });
});

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
  return until(() => evalHost<boolean>(code), label, timeoutMs, hostDialogs);
}

/** 宿主页面中打开的对话框与通知的文字，供超时信息使用。Open dialogs and toasts of the host page, for timeout messages. */
function hostDialogs(): Promise<string> {
  return evalHost<string>(
    `JSON.stringify([...document.querySelectorAll('[data-slot="dialog-content"][data-state="open"], [data-sonner-toast]')].map((element) => element.textContent))`,
  );
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

/** 打开菜单并等待它显示。Opens a menu and waits for it to show. */
async function openMenuAndWait(selector: string): Promise<void> {
  await openMenu(selector);
  await until(
    () => evalHost<boolean>(`Boolean(${OPEN_MENU})`),
    `the menu of ${selector}`,
    20_000,
    () =>
      evalHost<string>(
        `JSON.stringify({ triggers: [...document.querySelectorAll(${JSON.stringify(selector)})].map((button) => button.getAttribute("aria-expanded")), menus: [...document.querySelectorAll('[data-slot="dropdown-menu-content"]')].map((menu) => menu.dataset.state), focus: document.hasFocus(), active: document.activeElement?.outerHTML.slice(0, 160) ?? null })`,
      ),
  );
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

/** 文件查看器 `viewerId`（manifest 中的 id，例如 `preview`）的 frame。The frame of file viewer `viewerId` (its manifest id, e.g. `preview`). */
function viewerFrameOf(extensionId: string, viewerId: string) {
  return frames(extensionId).find(
    (frame) => runtime().registry.get(frame.frameId)?.viewId === viewerId,
  );
}

async function untilViewerFrame(extensionId: string, viewerId: string) {
  return until(() => {
    const found = viewerFrameOf(extensionId, viewerId);
    return found && runtime().registry.get(found.frameId)?.pageId ? found : null;
  }, `${extensionId} ${viewerId} frame`);
}

type FrameHandle = { frameId: string; view: WebContentsView };

async function evalFrame<T>(frame: FrameHandle, code: string): Promise<T> {
  // 页面所在进程结束时 executeJavaScript 不会返回，所以限定等待时间；返回后清除计时器。executeJavaScript never settles
  // when the page's process ends, so the wait is bounded; the timer is cleared once it settles.
  let timer: NodeJS.Timeout | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(
      () =>
        reject(
          new Error(`The frame ${frame.frameId} did not answer within 60 s: ${code.slice(0, 120)}`),
        ),
      60_000,
    );
  });
  try {
    return await Promise.race([
      frame.view.webContents.executeJavaScript(code, true) as Promise<T>,
      timeout,
    ]);
  } finally {
    clearTimeout(timer);
  }
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

/** 用第一个 token 向 Host 发送请求。Sends a request to the Host with the first token. */
async function hostSend<T>(method: "POST" | "DELETE", path: string, body?: unknown): Promise<T> {
  const response = await fetch(`${config.hostUrl}${path}`, {
    method,
    headers: {
      accept: "application/json",
      "content-type": "application/json",
      authorization: `Bearer ${config.firstToken}`,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  assert.equal(response.ok, true, `${method} ${path} -> ${response.status} ${text}`);
  return (JSON.parse(text) as { data: T }).data;
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

/**
 * 通过 "Back to chat" 回到聊天页，并断言地址是测试 Project 的 Chat。
 * Goes back through "Back to chat" and asserts the URL is the test Project's Chat.
 */
async function backToTestProjectChat(): Promise<void> {
  await goToChat();
  assert.deepEqual(
    await evalHost<{ path: string; projectId: string | null }>(
      `({ path: location.pathname, projectId: new URLSearchParams(location.search).get("projectId") })`,
    ),
    { path: "/desktop/chat/", projectId: config.projectId },
  );
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

async function textCodecBridge(): Promise<void> {
  const textBytes = [0xef, 0xbb, 0xbf, 0x61, 0x0d, 0x0a, 0x62];
  const binaryBytes = [0x50, 0x4b, 0x03, 0x04, 0x00, 0x00, 0x14, 0x00];
  const result = await evalHost<{
    decoded: unknown;
    binary: { ok: boolean; error?: { code: string | null } };
    encodedIsBytes: boolean;
    encodedBytes: number[];
    encodedSha256: string | null;
    encodingCount: number;
    gb18030Label: string | null;
  }>(`(async () => {
    const bridge = window.agwDesktop;
    const decoded = await bridge.decodeText(new Uint8Array(${JSON.stringify(textBytes)}));
    const binary = await bridge.decodeText(new Uint8Array(${JSON.stringify(binaryBytes)}));
    const encoded = await bridge.encodeText("a\\nb", { encoding: "utf-16le", bom: true, eol: "\\r\\n" });
    const encodings = await bridge.listTextEncodings();
    return {
      decoded,
      binary,
      encodedIsBytes: encoded.ok && encoded.value.bytes instanceof Uint8Array,
      encodedBytes: encoded.ok ? [...encoded.value.bytes] : [],
      encodedSha256: encoded.ok ? encoded.value.sha256 : null,
      encodingCount: encodings.length,
      gb18030Label: encodings.find((item) => item.id === "gb18030")?.label ?? null,
    };
  })()`);
  const sha256 = (bytes: number[]) =>
    createHash("sha256").update(Uint8Array.from(bytes)).digest("hex");
  assert.deepEqual(result.decoded, {
    ok: true,
    value: { text: "a\r\nb", encoding: "utf-8", bom: true, eol: "\r\n", sha256: sha256(textBytes) },
  });
  assert.equal(result.binary.ok, false);
  assert.equal(result.binary.error?.code, "ERR_NOT_TEXT");
  const expectedBytes = [0xff, 0xfe, 0x61, 0x00, 0x0d, 0x00, 0x0a, 0x00, 0x62, 0x00];
  assert.equal(result.encodedIsBytes, true);
  assert.deepEqual(result.encodedBytes, expectedBytes);
  assert.equal(result.encodedSha256, sha256(expectedBytes));
  assert.equal(result.encodingCount, 38);
  assert.equal(result.gb18030Label, "Chinese Simplified (GB18030)");
  pass("the host page decodes, encodes and lists text encodings through the Desktop bridge");
}

type SettingsFile = {
  extensions: Record<string, { enabled: boolean; grantedPermissions: string[] }>;
  removedBundledExtensions: string[];
};

async function bundledDefaults(): Promise<void> {
  await goToExtensions();
  await untilStatus("text-editor", "Active");
  await untilStatus("excalidraw", "Active");
  assert.equal(await evalHost<boolean>(`Boolean(${OPEN_DIALOG})`), false);
  const settings = await readJson<SettingsFile>(join(config.userData, "settings.json"));
  assert.equal(settings.extensions["text-editor"].enabled, true);
  assert.deepEqual([...settings.extensions["text-editor"].grantedPermissions].sort(), [
    "fs.cloud.read",
    "fs.cloud.write",
  ]);
  assert.equal(settings.extensions.excalidraw.enabled, true);
  assert.deepEqual([...settings.extensions.excalidraw.grantedPermissions].sort(), [
    "fs.cloud.read",
    "fs.cloud.write",
  ]);
  assert.deepEqual(settings.removedBundledExtensions, []);
  // 用户安装的包在 extensions/packages，数据在 extensions/data。Installed packages and data live side by side.
  assert.equal(
    existsSync(join(config.userData, "extensions", "packages", "sample", "manifest.json")),
    true,
  );
  assert.equal(existsSync(storage("text-editor")), true);
  assert.equal(existsSync(join(config.userData, "extension-data")), false);
  await goToChat();
  pass(
    "bundled extensions are active on first launch with their permissions approved, with data in extensions/data",
  );
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
  assert.equal(
    await evalHost<string>(`location.pathname`),
    "/settings/appearance/",
    "the Settings button opens the Appearance page",
  );
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
  // macOS FSEvents 可能在监听开始后才报告不久之前的写入：先等它过去，再以写入前的 changed 数量为基准。
  // macOS FSEvents may report a recent write after the watch starts: let it pass, then count changed events before writing.
  await delay(500);
  await api(
    main,
    `
    window.__changes = [];
    window.__watch = await api.fs.watch(${JSON.stringify(`${storageUri}/config.json`)}, {}, (event) => window.__changes.push(event));`,
  );
  await delay(300);
  const changedCount = "window.__changes.filter((event) => event.type === 'changed').length";
  const changedBefore = await evalFrame<number>(main, changedCount);
  await api(
    main,
    `await api.fs.writeFile(${JSON.stringify(`${storageUri}/config.json`)}, '{"theme":"light"}');`,
  );
  await until(
    () => evalFrame<boolean>(main, `${changedCount} > ${changedBefore}`),
    "a changed event after the write",
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

const CHAT_MARKDOWN = [
  "Markdown check[^n] and [^missing].",
  "",
  "| Item | Count |",
  "| --- | ---: |",
  "| apples | 3 |",
  "",
  "- [ ] first loose task",
  "",
  "- [x] second loose task",
  "",
  "Mixed list:",
  "",
  "- plain item",
  "- [ ] task item",
  "  see [^n]",
  "",
  "```ts",
  "const answer = 42;",
  "```",
  "",
  "```py",
  "if ready:",
  "    run()",
  "```",
  "",
  "Indented code follows:",
  "",
  "    indented code line",
  "",
  "<b>raw</b> C++ and C++ $x^2$",
  "",
  "[^n]: Footnote text.",
].join("\n");

const CONVERSATION_ITEMS = `[...document.querySelectorAll('.agw-left-sidebar[data-view-id="agw.conversations"] div.cursor-pointer')]`;
const MARKDOWN_CONVERSATION = `${CONVERSATION_ITEMS}.find((item) => item.textContent.includes("Markdown check"))`;
const MARKDOWN_REPLY = `[...document.querySelectorAll(".agw-main-workspace .msg-content")].find((element) => element.textContent.includes("Echo: Markdown check"))`;

/** 回复中标题为 language 的代码块。The reply's code block whose header names language. */
function codeBlock(language: string): string {
  return `[...${MARKDOWN_REPLY}.querySelectorAll(".msg-content-md-code-block")].find((block) => block.querySelector(".msg-content-md-code-header span")?.textContent === ${JSON.stringify(language)})`;
}

async function copyCode(language: string): Promise<string> {
  host().focus();
  await evalHost(
    `${codeBlock(language)}.querySelector('button[aria-label="Copy code"]').click(); true`,
  );
  await untilHost(
    `Boolean(${codeBlock(language)}.querySelector('button[aria-label="Code copied"]'))`,
    `the ${language} code to be copied`,
  );
  return clipboard.readText();
}

async function chatMarkdown(): Promise<void> {
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
    await session.send({ target: { type: "agent", id: ${JSON.stringify(config.agentId)} }, text: ${JSON.stringify(CHAT_MARKDOWN)} });
    const text = await finished;
    await session.dispose();
    return { conversationId: session.conversationId, text };`,
  );
  assert.match(result.text, /^Echo: Markdown check/u);
  await untilHost(`Boolean(${MARKDOWN_CONVERSATION})`, "the Markdown conversation in the list");
  await evalHost(`${MARKDOWN_CONVERSATION}.click(); true`);
  await untilHost(
    `new URLSearchParams(location.search).get("conversationId") === ${JSON.stringify(result.conversationId)}`,
    "the Markdown conversation to open",
  );
  await untilHost(
    `Boolean(${MARKDOWN_REPLY}?.querySelector("section[data-footnotes]"))`,
    "the rendered Markdown reply",
  );

  const reply = await evalHost<Record<string, unknown>>(`(() => {
    const message = ${MARKDOWN_REPLY};
    const reference = message.querySelector("sup > a[data-footnote-ref]");
    const footnote = message.querySelector("section[data-footnotes] li");
    const region = message.querySelector('[role="region"][aria-label="Scrollable table"]');
    const lists = [...message.querySelectorAll("ul")];
    const looseLists = lists.filter((list) => list.textContent.includes("loose task"));
    const mixedLists = lists.filter((list) => list.textContent.includes("plain item"));
    return {
      referenceText: reference?.textContent,
      referenceTarget: Boolean(footnote?.id) && reference?.getAttribute("href") === "#" + footnote.id,
      missingReferenceText: message.textContent.includes("[^missing]"),
      headCells: region?.querySelectorAll("thead th").length,
      secondColumnAlign: region?.querySelectorAll("tbody td")[1]?.style.textAlign,
      cellParagraphs: region?.querySelectorAll("td p").length,
      looseLists: looseLists.length,
      looseItemsWithCheckboxInParagraph: [...(looseLists[0]?.children ?? [])].map((item) => Boolean(item.querySelector(":scope > p > input[type=checkbox]"))),
      mixedLists: mixedLists.length,
      mixedListReference: Boolean(mixedLists[0]?.querySelector("li:last-child a[data-footnote-ref]")),
      pythonText: ${codeBlock("py")}?.querySelector("pre code")?.textContent,
      indentedCode: [...message.querySelectorAll("pre.msg-content-md-code")].map((pre) => [pre.closest(".msg-content-md-code-block") === null, pre.textContent]),
      htmlElements: message.querySelectorAll("b, u").length,
      literalText: message.textContent.includes("<b>raw</b>") && message.textContent.includes("C++ and C++"),
      math: Boolean(message.querySelector(".katex")),
    };
  })()`);
  assert.deepEqual(reply, {
    referenceText: "1",
    referenceTarget: true,
    missingReferenceText: true,
    headCells: 2,
    secondColumnAlign: "right",
    cellParagraphs: 0,
    looseLists: 1,
    looseItemsWithCheckboxInParagraph: [true, true],
    mixedLists: 1,
    mixedListReference: true,
    pythonText: "if ready:\n    run()\n",
    indentedCode: [[true, "indented code line\n"]],
    htmlElements: 0,
    literalText: true,
    math: true,
  });
  assert.equal(await copyCode("ts"), "const answer = 42;");
  assert.equal(await copyCode("py"), "if ready:\n    run()");
  pass(
    "chat replies render footnotes, tables, task lists, code blocks, math, and raw HTML as text with Tiptap",
  );

  const page = await evalHost<Record<string, unknown>>(`(() => {
    const userMessage = [...document.querySelectorAll(".agw-main-workspace .agw-msg-user")].find((element) => element.textContent.includes("Markdown check"));
    const ids = [...document.querySelectorAll(".agw-main-workspace [id]")].map((element) => element.id);
    const message = ${MARKDOWN_REPLY};
    message.querySelector("sup > a[data-footnote-ref]").click();
    return {
      userMessageReference: Boolean(userMessage?.querySelector("a[data-footnote-ref]")),
      uniqueIds: ids.length === new Set(ids).size,
      focusedFootnote: document.activeElement === message.querySelector("section[data-footnotes] li"),
      hash: location.hash,
    };
  })()`);
  assert.deepEqual(page, {
    userMessageReference: true,
    uniqueIds: true,
    focusedFootnote: true,
    hash: "",
  });
  pass(
    "user messages use Tiptap too, footnote ids stay unique, and a reference focuses its footnote",
  );

  await evalHost(
    `${CONVERSATION_ITEMS}.find((item) => !item.textContent.includes("Markdown check")).click(); true`,
  );
  await untilHost(
    `new URLSearchParams(location.search).get("conversationId") !== ${JSON.stringify(result.conversationId)}`,
    "another conversation to open",
  );
  await evalHost(`${MARKDOWN_CONVERSATION}.click(); true`);
  await untilHost(
    `Boolean(${MARKDOWN_REPLY}?.querySelector("section[data-footnotes]"))`,
    "the Markdown reply after switching back",
  );
  assert.equal(
    await evalHost<boolean>(
      `document.querySelector(".agw-main-workspace").textContent.includes("This view couldn’t be loaded")`,
    ),
    false,
  );
  pass("switching conversations keeps the Markdown reply rendering");
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

/**
 * 执行 `action` 期间系统文件选择对话框直接返回 `path`：系统对话框无法自动操作。
 * While `action` runs the native file picker returns `path` right away: the native picker cannot be automated.
 */
async function withPickedPath(path: string, action: () => Promise<void>): Promise<void> {
  const original = dialog.showOpenDialog;
  dialog.showOpenDialog = (async () => ({
    canceled: false,
    filePaths: [path],
  })) as typeof dialog.showOpenDialog;
  try {
    await action();
  } finally {
    dialog.showOpenDialog = original;
  }
}

async function zipInstall(): Promise<void> {
  await goToExtensions();
  await withPickedPath(join(config.fixtures, "sample-v2.zip"), async () => {
    await clickText("Install from .zip");
    await untilHost(`${OPEN_DIALOG}?.textContent.includes("1.0.0 → 1.1.0")`, "the version change");
    await clickText("Replace", OPEN_DIALOG);
  });
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

/** 文件树中名称为 name 的项（测试数据中的名称不重复）。The file tree item named name (names in the test data are unique). */
function treeItem(name: string): string {
  return `[...document.querySelectorAll('.agw-left-sidebar *')].find((element) => element.textContent === ${JSON.stringify(name)} && element.children.length === 0)`;
}

/**
 * 在 Files 中点击文件，`path` 中的目录依次展开。
 * Clicks a file in Files, expanding the directories in `path` on the way.
 */
async function clickFileInTree(path: string): Promise<void> {
  await goToChat();
  if (
    !(await evalHost<boolean>(
      `Boolean(document.querySelector('.agw-left-sidebar[data-view-id="agw.files"]'))`,
    ))
  ) {
    await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.files"]');
  }
  const segments = path.split("/");
  for (const directory of segments.slice(0, -1)) {
    const row = `${treeItem(directory)}?.parentElement`;
    await untilHost(`Boolean(${row})`, `${directory}/ in the file tree`);
    if (!(await evalHost<boolean>(`Boolean(${row}.querySelector(".lucide-chevron-down"))`))) {
      await evalHost(`${row}.click(); true`);
    }
  }
  const item = treeItem(segments.at(-1)!);
  await untilHost(`Boolean(${item})`, `${path} in the file tree`);
  await evalHost(`${item}.click(); true`);
}

/** 在 Files 中点击文件并等待文件视图显示它。Clicks a file in Files and waits for the File view to show it. */
async function openFileInTree(path: string): Promise<void> {
  await clickFileInTree(path);
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.getAttribute('data-view-id') === "agw.file"`,
    "the file view",
  );
  await untilHost(shownFileIs(basename(path)), `the file view to show ${path}`);
}

function shownFileIs(name: string): string {
  return `[...document.querySelectorAll('.agw-main-workspace span.font-medium')].some((element) => element.textContent === ${JSON.stringify(name)})`;
}

function viewerTab(id: string): string {
  return `document.querySelector('[data-file-viewer-tab="${id}"]')`;
}

async function filesView(): Promise<void> {
  await openFileInTree("readme.md");
  // Markdown 文件默认由 Text Editor 的 Edit 打开，切换到 Source 显示原文。Markdown opens in Text Editor's Edit; Source shows the text.
  await untilHost(
    `${viewerTab("text-editor:markdown")}?.getAttribute("aria-selected") === "true"`,
    "the Edit tab",
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

const MARKDOWN_VIEWER = "text-editor:preview";

/**
 * 文件标题行右侧各元素及其是否在窗口内可以点到，例如 `[["File views", true], ["More", true], ["Reload file", true], ["View info", true]]`。
 * The file title row's right-side items and whether each is clickable inside the window.
 */
const FILE_HEADER_ACTIONS = `(() => {
  const actions = document.querySelector('.agw-main-workspace [role="tablist"]')?.parentElement;
  const clickable = (element) => {
    const rect = element.getBoundingClientRect();
    const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    return rect.right <= window.innerWidth && (hit === element || element.contains(hit));
  };
  return [...(actions?.children ?? [])].map((element) => [element.getAttribute("aria-label") ?? element.getAttribute("role"), clickable(element)]);
})()`;
const OPEN_CONFIRMATION = `document.querySelector('[data-host-confirmation][data-state="open"]')`;

function markdownText(includes: string): string {
  return `Boolean(document.querySelector("article.markdown-body")?.textContent.includes(${JSON.stringify(includes)}))`;
}

/** 选择 Preview：Markdown 文件默认由 Edit 打开。Selects Preview; Markdown files open in Edit by default. */
async function selectPreview(): Promise<void> {
  await clickHost(`[data-file-viewer-tab="${MARKDOWN_VIEWER}"]`);
  await untilHost(
    `${viewerTab(MARKDOWN_VIEWER)}?.getAttribute("aria-selected") === "true"`,
    "the Preview tab",
  );
}

async function textEditorPreview(): Promise<void> {
  await openFileInTree("guide.md");
  await selectPreview();
  const firstPreview = await untilViewerFrame("text-editor", "preview");
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
    "Markdown files open in the Text Editor Preview with headings, tables, highlighted code, and project images",
  );

  const structure = await evalFrame<Record<string, unknown>>(
    firstPreview,
    `(() => {
      const article = document.querySelector("article.markdown-body");
      return {
        headingId: Boolean(article.querySelector("h1#guide")),
        headCells: article.querySelectorAll("table thead th").length,
        cellParagraphs: article.querySelectorAll("table td p").length,
        footnoteReference: Boolean(article.querySelector('sup a[data-footnote-ref][href="#user-content-fn-note"]')),
        footnote: Boolean(article.querySelector("section.footnotes li#user-content-fn-note")),
        tasks: article.querySelectorAll("li.task-list-item input[disabled]").length,
        boldElements: article.querySelectorAll("b").length,
        rawHtmlText: article.textContent.includes("<b>raw</b>"),
      };
    })()`,
  );
  assert.deepEqual(structure, {
    headingId: true,
    headCells: 2,
    cellParagraphs: 0,
    footnoteReference: true,
    footnote: true,
    tasks: 2,
    boldElements: 0,
    rawHtmlText: true,
  });
  pass("the Text Editor Preview renders heading ids, footnotes, task lists, and raw HTML as text");

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
    const frame = viewerFrameOf("text-editor", "preview");
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
  // 重新加载的查看器显示后再打开弹出层：遮挡开始时 view 还没有显示就直接隐藏，没有截图。Open the popup once the
  // reloaded viewer shows: a view that is not showing yet when obscuring starts is hidden at once, without a snapshot.
  await until(
    () => preview.view.getVisible(),
    "the reloaded preview to show",
    undefined,
    () => overlayState(preview),
  );
  await clickHost('.agw-main-workspace [aria-label="View info"]');
  await untilHost(
    `${infoPopup}?.textContent.includes("Preview") && ${infoPopup}.textContent.includes("Text Editor")`,
    "the viewer info popup",
  );
  // 弹出层盖住查看器时换成已画出的截图，页面不重新加载。Under the popup the viewer swaps to a painted snapshot without reloading.
  await until(() => !preview.view.getVisible(), "the preview to be replaced by its snapshot");
  assert.equal(
    await evalHost<boolean>(snapshotLoaded(preview.frameId)),
    true,
    await overlayState(preview),
  );
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
  assert.equal(viewerFrameOf("text-editor", "preview")?.frameId, preview.frameId);
  // 源码中的长行不撑宽主区；Source 时标题行仍有切换按钮、"More"、"Reload file" 与 Info，都在窗口内可以点到。
  // Long source lines do not widen the main workspace; with Source the title row keeps the switch, "More",
  // "Reload file" and Info, all clickable inside the window.
  assert.equal(
    await evalHost<boolean>(
      `document.querySelector('.agw-main-workspace').getBoundingClientRect().right <= window.innerWidth`,
    ),
    true,
  );
  assert.deepEqual(await evalHost(FILE_HEADER_ACTIONS), [
    ["File views", true],
    ["More", true],
    ["Reload file", true],
    ["View info", true],
  ]);
  // Info 仍说明切换前的查看器。Info still describes the viewer selected before the switch.
  await clickHost('.agw-main-workspace [aria-label="View info"]');
  await untilHost(
    `${infoPopup}?.textContent.includes("Preview") && ${infoPopup}.textContent.includes("Text Editor")`,
    "the viewer info popup with Source selected",
  );
  await pressEscape();
  await untilHost(`!${infoPopup}`, "the viewer info popup to close");
  // "Reload file" 重新读取源码。"Reload file" reads the source again.
  const guidePath = join(config.projectDirectory, "guide.md");
  const guide = await readFile(guidePath, "utf8");
  await writeFile(guidePath, `${guide}\nReloaded source line.\n`);
  await clickHost('.agw-main-workspace [aria-label="Reload file"]');
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.textContent.includes("Reloaded source line.")`,
    "the reloaded source",
  );
  await writeFile(guidePath, guide);
  await clickHost(`[data-file-viewer-tab="${MARKDOWN_VIEWER}"]`);
  await until(() => preview.view.getVisible(), "the preview to show again");

  await evalFrame(preview, `document.querySelector('a[href="docs/notes.md"]').click(); true`);
  await untilHost(shownFileIs("notes.md"), "docs/notes.md in the file view");
  // 打开的文件回到默认的 Edit，再选 Preview 时由同一个 Preview frame 显示。The opened file returns to the default
  // Edit; selecting Preview again shows it in the same Preview frame.
  await untilHost(selectedViewerIs(EDIT_VIEWER), "docs/notes.md in the default Edit viewer");
  await selectPreview();
  await until(
    () => evalFrame<boolean>(preview, markdownText("project notes")),
    "the notes preview",
  );
  assert.equal(viewerFrameOf("text-editor", "preview")?.frameId, preview.frameId);
  pass(
    "Source shows the text, a relative link opens the linked file in the default viewer, and Preview then shows it in the same frame",
  );

  await openFileInTree("guide.md");
  await selectPreview();
  await until(() => evalFrame<boolean>(preview, markdownText("Example")), "the guide preview");
  await evalFrame(
    preview,
    `document.querySelector('a[href="https://example.com/docs"]').click(); true`,
  );
  await untilHost(
    `${OPEN_CONFIRMATION}?.getAttribute("data-host-confirmation") === "open-link"
      && ${OPEN_CONFIRMATION}.textContent.includes("https://example.com/docs")
      && ${OPEN_CONFIRMATION}.textContent.includes("Text Editor")`,
    "the Open link dialog",
  );
  await clickText("Cancel", OPEN_CONFIRMATION);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the Open link dialog to close");
  await untilHost(shownFileIs("guide.md"), "the guide to stay open");
  pass("an external link asks before opening the browser");

  await disable("text-editor");
  await untilStatus("text-editor", "Disabled", 10_000);
  assert.equal(frames("text-editor").length, 0);
  await openFileInTree("guide.md");
  await untilHost(
    `!document.querySelector('[data-file-viewer-tab]')
      && document.querySelector('.agw-main-workspace')?.textContent.includes("[Notes](docs/notes.md)")`,
    "the Markdown source without viewers",
  );
  await enable("text-editor", "Text Editor");
  await untilStatus("text-editor", "Active");
  await openFileInTree("readme.md");
  await selectPreview();
  const enabled = await untilViewerFrame("text-editor", "preview");
  await until(() => evalFrame<boolean>(enabled, markdownText("hello cloud")), "the readme preview");
  pass("disabling Text Editor leaves only the source, and enabling it again restores the preview");
}

const EXCALIDRAW_VIEWER = "excalidraw:editor";
// 测试写到磁盘上的 Excalidraw 场景，元素的其余字段由 restore 补全。A scene the test writes to disk; restore fills the
// other element fields.
const DIAGRAM_ON_DISK = {
  type: "excalidraw",
  version: 2,
  source: "https://excalidraw.com",
  elements: [] as {
    id: string;
    type: string;
    x: number;
    y: number;
    width: number;
    height: number;
    isDeleted?: boolean;
  }[],
  appState: { viewBackgroundColor: "#ffffff" },
  files: {},
};
const UNSAVED_MARKER = `Boolean(${viewerTab(EXCALIDRAW_VIEWER)}?.querySelector('[aria-label="Unsaved changes"]'))`;
const DISCARD_DIALOG = `document.querySelector('[data-host-confirmation="discard-unsaved"][data-state="open"]')`;
// Excalidraw 根元素的主题，没有根元素时为 null。The theme of the Excalidraw root, or null without one.
const EXCALIDRAW_THEME = `(() => {
  const root = document.querySelector(".excalidraw");
  return root ? (root.classList.contains("theme--dark") ? "dark" : "light") : null;
})()`;
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
  modifiers: ("meta" | "control" | "shift" | "alt")[] = [],
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

  await clickHost('[data-file-viewer-tab="source"]');
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.textContent.includes('"elements"')`,
    "the Excalidraw source",
  );
  assert.deepEqual(await evalHost(FILE_HEADER_ACTIONS), [
    ["File views", true],
    ["More", true],
    ["Reload file", true],
    ["View info", true],
  ]);
  await clickHost(`[data-file-viewer-tab="${EXCALIDRAW_VIEWER}"]`);
  await until(() => editor.view.getVisible(), "the editor to show again");
  pass("with Source selected the Excalidraw file keeps the reload and info buttons");

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

  // 同一个文件重新显示时文档会话不读取，未保存的矩形与画布保留。Showing the same file again does not read, so the
  // unsaved rectangle and the canvas stay.
  const drawn = await evalFrame<number>(editor, DRAWN_PIXELS);
  await clickHost('[data-file-viewer-tab="source"]');
  await until(() => !editor.view.getVisible(), "the editor to hide");
  await clickHost(`[data-file-viewer-tab="${EXCALIDRAW_VIEWER}"]`);
  await until(() => editor.view.getVisible(), "the editor to show again");
  await delay(500);
  assert.equal(await evalHost<boolean>(UNSAVED_MARKER), true);
  assert.equal(await evalFrame<number>(editor, DRAWN_PIXELS), drawn);
  pass("showing the same Excalidraw file again keeps its unsaved drawing");

  // Mod+Shift+S 是 Excalidraw 自己的“保存到磁盘”：不打开保存对话框，也不写入文件，保存只经过文档会话。系统保存对话框
  // 无法自动操作，这里记录页面对 showSaveFilePicker 的调用与 session 中的下载，并在 window 的捕获阶段记录按键。
  // Mod+Shift+S is Excalidraw's own "save to disk": it opens no save dialog and writes nothing, so saving goes only
  // through the document session. The native save dialog cannot be automated, so the page's showSaveFilePicker calls
  // and the session's downloads are recorded, and the key is recorded in the window's capture phase.
  const sceneBeforeSaveAs = await readFile(diagramPath, "utf8");
  const pickerSupported = await evalFrame<boolean>(
    editor,
    `(() => {
      const supported = typeof window.showSaveFilePicker === "function";
      window.__saveAsCalls = 0;
      window.showSaveFilePicker = async () => {
        window.__saveAsCalls += 1;
        throw new DOMException("The user aborted a request.", "AbortError");
      };
      window.__saveAsKeys = [];
      window.addEventListener("keydown", (event) => {
        if (event.key.toLowerCase() === "s") window.__saveAsKeys.push({ shift: event.shiftKey, prevented: event.defaultPrevented });
      }, true);
      return supported;
    })()`,
  );
  const downloads: string[] = [];
  const onDownload = (_event: Electron.Event, item: Electron.DownloadItem) => {
    downloads.push(item.getFilename());
    item.cancel();
  };
  editor.view.webContents.session.on("will-download", onDownload);
  try {
    pressKey(editor, "s", [MOD, "shift"]);
    await until(
      () => evalFrame<boolean>(editor, "window.__saveAsKeys.length > 0"),
      "the Mod+Shift+S keydown",
    );
    await delay(1000);
  } finally {
    editor.view.webContents.session.off("will-download", onDownload);
  }
  console.log(`showSaveFilePicker in the Excalidraw page: ${pickerSupported}`);
  assert.deepEqual(
    {
      keys: await evalFrame(editor, "window.__saveAsKeys"),
      pickerCalls: await evalFrame<number>(editor, "window.__saveAsCalls"),
      downloads,
      excalidrawDialog: await evalFrame<boolean>(
        editor,
        `Boolean(document.querySelector(".excalidraw .Modal, .excalidraw .Dialog"))`,
      ),
      hostDialog: await evalHost<boolean>(`Boolean(${OPEN_CONFIRMATION})`),
      unsaved: await evalHost<boolean>(UNSAVED_MARKER),
      sceneUnchanged: (await readFile(diagramPath, "utf8")) === sceneBeforeSaveAs,
    },
    {
      keys: [{ shift: true, prevented: true }],
      pickerCalls: 0,
      downloads: [],
      excalidrawDialog: false,
      hostDialog: false,
      unsaved: true,
      sceneUnchanged: true,
    },
  );
  pass("Mod+Shift+S in Excalidraw opens no save dialog and writes nothing");

  pressKey(editor, "s", [MOD]);
  await until(async () => {
    const scene = JSON.parse(await readFile(diagramPath, "utf8")) as {
      elements: { type: string; isDeleted: boolean }[];
    };
    return scene.elements.filter((element) => !element.isDeleted).length === 3;
  }, "the saved diagram");
  await untilHost(`!${UNSAVED_MARKER}`, "the unsaved marker to clear");
  assert.equal(await evalHost<boolean>(`Boolean(${OPEN_CONFIRMATION})`), false);
  await openFileInTree("guide.md");
  assert.equal(await evalHost<boolean>(`Boolean(${OPEN_CONFIRMATION})`), false);
  await until(() => frames("excalidraw").length === 0, "the Excalidraw frame to close");
  pass("saving writes the new element and later file changes need no confirmation");

  // 冲突：保存时磁盘上的场景已被其他写入方修改。A conflict: the scene on disk was changed by another writer.
  await openFileInTree("diagram.excalidraw");
  const conflicted = await untilFrame("excalidraw", "fileViewer");
  await until(
    async () => (await evalFrame<number>(conflicted, DRAWN_PIXELS)) > 0,
    "the saved diagram on the canvas",
  );
  pressKey(conflicted, "r");
  await until(
    () =>
      evalFrame<boolean>(
        conflicted,
        `document.querySelector('[data-testid="toolbar-rectangle"]')?.checked === true`,
      ),
    "the rectangle tool",
  );
  const bounds = conflicted.view.getBounds();
  const third = { x: Math.round(bounds.width * 0.25), y: Math.round(bounds.height * 0.7) };
  drag(conflicted, third, { x: third.x + 100, y: third.y + 60 });
  await untilHost(UNSAVED_MARKER, "the unsaved marker for the conflict");
  type Scene = typeof DIAGRAM_ON_DISK;
  const readScene = async () => JSON.parse(await readFile(diagramPath, "utf8")) as Scene;
  const writeScene = (scene: Scene) =>
    writeFile(diagramPath, `${JSON.stringify(scene, null, 2)}\n`);
  const liveElements = (scene: Scene) => scene.elements.filter((element) => !element.isDeleted);
  await writeScene({
    ...DIAGRAM_ON_DISK,
    elements: [{ id: "disk", type: "diamond", x: 100, y: 300, width: 80, height: 80 }],
  });
  pressKey(conflicted, "s", [MOD]);
  await expectConflictDialog("Excalidraw", "diagram.excalidraw", conflicted);
  await clickText("Overwrite", OPEN_MESSAGE_BOX);
  await untilHost(`!${UNSAVED_MARKER}`, "the unsaved marker to clear");
  const overwritten = await readScene();
  assert.deepEqual(
    liveElements(overwritten)
      .map((element) => element.type)
      .sort(),
    ["ellipse", "rectangle", "rectangle", "rectangle"],
  );
  assert.equal(
    overwritten.elements.some((element) => element.id === "disk"),
    false,
  );
  pass(
    "saving over a changed diagram shows the Excalidraw conflict dialog, and Overwrite writes the new rectangle",
  );

  // 没有修改时重新显示读取磁盘上的新场景，未保存标记不出现。Showing again with no changes reads the new scene from
  // disk without the unsaved marker appearing.
  await clickHost('[data-file-viewer-tab="source"]');
  await until(() => !conflicted.view.getVisible(), "the editor to hide");
  const beforeRefresh = await evalFrame<number>(conflicted, DRAWN_PIXELS);
  await writeScene({
    ...overwritten,
    elements: [
      ...overwritten.elements,
      { id: "added", type: "diamond", x: 100, y: 320, width: 200, height: 200 },
    ],
  });
  await clickHost(`[data-file-viewer-tab="${EXCALIDRAW_VIEWER}"]`);
  let markerSeen = false;
  await until(async () => {
    if (await evalHost<boolean>(UNSAVED_MARKER)) markerSeen = true;
    return (await evalFrame<number>(conflicted, DRAWN_PIXELS)) !== beforeRefresh;
  }, "the refreshed diagram");
  await delay(500);
  assert.equal(markerSeen || (await evalHost<boolean>(UNSAVED_MARKER)), false);
  pass("showing the unchanged diagram again reads the file from disk without an unsaved marker");

  // 场景打开期间切换宿主主题，画布随之切换。The host theme changes while the scene is open and the canvas follows.
  const startTheme = await evalHost<"light" | "dark">(
    `document.querySelector('[aria-label="Use dark theme"]') ? "light" : "dark"`,
  );
  const otherTheme = startTheme === "light" ? "dark" : "light";
  const canvasThemeIs = (theme: "light" | "dark") => async () =>
    (await evalFrame<string | null>(conflicted, EXCALIDRAW_THEME)) === theme;
  await until(canvasThemeIs(startTheme), `the ${startTheme} Excalidraw canvas`);
  await clickHost(`[aria-label="Use ${otherTheme} theme"]`);
  await until(canvasThemeIs(otherTheme), `the ${otherTheme} Excalidraw canvas`);
  pass("a host theme change while a scene is open switches the Excalidraw canvas theme");

  // Reload 放弃画布上的修改并显示磁盘上的场景，载入后不出现未保存标记。Reload discards the canvas changes and shows the
  // scene on disk, with no unsaved marker after loading.
  pressKey(conflicted, "r");
  const fourth = { x: Math.round(bounds.width * 0.25), y: Math.round(bounds.height * 0.3) };
  drag(conflicted, fourth, { x: fourth.x + 100, y: fourth.y + 60 });
  await untilHost(UNSAVED_MARKER, "the unsaved marker for another rectangle");
  const beforeReload = await evalFrame<number>(conflicted, DRAWN_PIXELS);
  const reloaded = {
    ...DIAGRAM_ON_DISK,
    elements: [{ id: "reloaded", type: "rectangle", x: 0, y: 0, width: 120, height: 90 }],
  };
  await writeScene(reloaded);
  pressKey(conflicted, "s", [MOD]);
  await expectConflictDialog("Excalidraw", "diagram.excalidraw", conflicted);
  await clickText("Reload", OPEN_MESSAGE_BOX);
  await untilHost(`!${UNSAVED_MARKER}`, "the unsaved marker to clear after Reload");
  // 画布不再是 Reload 之前的绘制结果。The canvas no longer shows the drawing from before Reload.
  await until(
    async () => (await evalFrame<number>(conflicted, DRAWN_PIXELS)) !== beforeReload,
    "the diagram on disk on the canvas",
  );
  await delay(500);
  assert.equal(await evalHost<boolean>(UNSAVED_MARKER), false);
  assert.deepEqual(await readScene(), reloaded);
  pass("Reload shows the diagram on disk and leaves no unsaved marker");

  // 主题切换之后载入的场景保持宿主主题。A scene loaded after the theme change keeps the host theme.
  assert.equal(await canvasThemeIs(otherTheme)(), true);
  await clickHost(`[aria-label="Use ${startTheme} theme"]`);
  await until(canvasThemeIs(startTheme), `the ${startTheme} Excalidraw canvas again`);
  pass("a scene loaded after a host theme change keeps the host theme");

  await openFileInTree("guide.md");
  assert.equal(await evalHost<boolean>(`Boolean(${OPEN_CONFIRMATION})`), false);
  await until(() => frames("excalidraw").length === 0, "the Excalidraw frame to close");
  pass("after an Overwrite or a Reload, other files open without confirmation");

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

/* ---------- Text Editor 与 todo-editor Text Editor and todo-editor ---------- */

const EDIT_VIEWER = "text-editor:markdown";
const CODE_VIEWER = "text-editor:code";
const FILE_VIEWER_TABS = `[...document.querySelectorAll('[data-file-viewer-tab]')].map((tab) => tab.getAttribute("data-file-viewer-tab"))`;
const OPEN_MESSAGE_BOX = `document.querySelector('[data-host-confirmation="message"][data-state="open"]')`;
// 查看器顶部条或工具栏中的保存状态为 "Saved"。The save status in a viewer's top bar or toolbar reads "Saved".
const SAVED = `document.querySelector(".save-status")?.textContent === "Saved"`;
const EDITOR = `document.querySelector(".ProseMirror")`;
// Code 编辑器显示的全部文字，按行的位置排列；Monaco 把空格显示为 U+00A0。All text the Code editor shows, ordered by
// line position; Monaco shows spaces as U+00A0.
const CODE_TEXT = `[...document.querySelectorAll(".code-editor .view-line")].map((line) => [parseFloat(line.style.top), line.textContent.replace(/\\u00a0/g, " ")]).sort((left, right) => left[0] - right[0]).map((entry) => entry[1]).join("\\n")`;
const CODE_STATUS = `(document.querySelector(".code-status-bar")?.textContent ?? "")`;
const DOCUMENT_ERROR = `(document.querySelector(".document-error")?.textContent ?? "")`;

function unsavedIn(viewerId: string): string {
  return `Boolean(${viewerTab(viewerId)}?.querySelector('[aria-label="Unsaved changes"]'))`;
}

function selectedViewerIs(viewerId: string): string {
  return `${viewerTab(viewerId)}?.getAttribute("aria-selected") === "true"`;
}

function projectPath(path: string): string {
  return join(config.projectDirectory, ...path.split("/"));
}

async function selectViewer(viewerId: string): Promise<void> {
  await clickHost(`[data-file-viewer-tab="${viewerId}"]`);
  await untilHost(selectedViewerIs(viewerId), `the ${viewerId} tab`);
}

/** Edit 中文字包含 text 的顶层块，tag 为块的选择器。The top-level Edit block containing text; tag selects the block. */
function editBlock(text: string, tag = "*"): string {
  return `[...document.querySelectorAll(".ProseMirror > ${tag}")].find((block) => block.textContent.includes(${JSON.stringify(text)}))`;
}

/**
 * 等待 frame 渲染完下一帧：requestAnimationFrame 中排入的任务在这一帧的布局与 ResizeObserver 回调之后运行，
 * 读取的是这一帧渲染的布局（例如 floating-ui 在下一帧修正的弹出层位置）。
 * Waits until the frame has rendered its next frame: a task queued from requestAnimationFrame runs after that frame's
 * layout and ResizeObserver callbacks, so reads see the layout of that frame (for example a popup position floating-ui
 * corrects in the next frame).
 */
const NEXT_RENDERED_FRAME = `new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve)))`;

/** 渲染完下一帧后 frame 中元素的中心点，以及这一点是否点到元素本身。An element's center in a frame once the frame has
 *  rendered its next frame, and whether that point hits it. */
function centerOf(frame: FrameHandle, element: string) {
  return evalFrame<{ x: number; y: number; hit: boolean } | null>(
    frame,
    `(async () => {
      await ${NEXT_RENDERED_FRAME};
      const element = ${element};
      if (!element) return null;
      const rect = element.getBoundingClientRect();
      const x = rect.left + rect.width / 2;
      const y = rect.top + rect.height / 2;
      const hit = document.elementFromPoint(x, y);
      return { x, y, hit: rect.width > 0 && (hit === element || element.contains(hit)) };
    })()`,
  );
}

/** 用鼠标点击 frame 中可以点到的元素。Clicks a hittable element in a frame with the mouse. */
async function clickInFrame(
  frame: FrameHandle,
  element: string,
  modifiers: ("meta" | "control")[] = [],
): Promise<void> {
  // 排版规范使内容变高，元素可能在滚动区域之外，先滚动到可见位置。弹出层可能先显示在临时位置，到下一帧才修正（例如
  // `/` 菜单先按空容器定位在光标下方，floating-ui 的 autoUpdate 在下一帧把它移到光标上方）；对临时位置滚动会滚动整个页面，
  // 点击会点到别的项目。所以元素出现后等一帧渲染完再滚动，命中检查也在一帧渲染完后进行（centerOf）。
  // The typography makes content taller, so the element may lie outside the scroll area; scroll it into view first. A
  // popup can first show at a provisional position that is corrected in the next frame (for example the `/` menu is
  // first placed below the cursor for its empty container, and floating-ui's autoUpdate moves it above the cursor in
  // the next frame); scrolling to the provisional position scrolls the whole page and the click hits another item. So
  // the scroll waits for a rendered frame once the element exists, and the hit test also runs after a rendered frame
  // (centerOf).
  await until(
    () =>
      evalFrame<boolean>(
        frame,
        `(async () => {
          if (!(${element})) return false;
          await ${NEXT_RENDERED_FRAME};
          const element = ${element};
          if (!element) return false;
          element.scrollIntoView({ block: "center" });
          return true;
        })()`,
      ),
    `${element} to exist`,
  );
  const point = await until(async () => {
    const center = await centerOf(frame, element);
    return center?.hit ? center : null;
  }, `${element} to be clickable`);
  const contents = frame.view.webContents;
  contents.focus();
  const x = Math.round(point.x);
  const y = Math.round(point.y);
  contents.sendInputEvent({ type: "mouseMove", x, y, modifiers });
  contents.sendInputEvent({ type: "mouseDown", x, y, button: "left", clickCount: 1, modifiers });
  contents.sendInputEvent({ type: "mouseUp", x, y, button: "left", clickCount: 1, modifiers });
}

/**
 * 把可编辑区域中的光标放在元素末尾，或元素中文字 `after` 之后；ProseMirror 在 selectionchange 时读取。
 * Puts the caret at the end of an element in an editable area, or after the text `after` in it; ProseMirror reads
 * it on selectionchange.
 */
async function placeCaret(frame: FrameHandle, element: string, after?: string): Promise<void> {
  frame.view.webContents.focus();
  await evalFrame(
    frame,
    `(() => {
      const element = ${element};
      element.closest('[contenteditable="true"]').focus();
      const range = document.createRange();
      const after = ${JSON.stringify(after ?? null)};
      if (after === null) {
        // 元素中最后一段文字的末尾。The end of the element's last text.
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        let last = null;
        for (let node = walker.nextNode(); node; node = walker.nextNode()) last = node;
        if (last) range.setStart(last, last.data.length);
        else range.selectNodeContents(element);
        range.collapse(!!last);
      } else {
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        let node = walker.nextNode();
        while (node && !node.data.includes(after)) node = walker.nextNode();
        if (!node) throw new Error("The text " + after + " is not in the element.");
        range.setStart(node, node.data.indexOf(after) + after.length);
        range.collapse(true);
      }
      const selection = getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      return true;
    })()`,
  );
  await delay(200);
}

/** 选中可编辑区域中元素里的文字 text。Selects the text in an element of an editable area. */
async function selectTextIn(frame: FrameHandle, element: string, text: string): Promise<void> {
  frame.view.webContents.focus();
  await evalFrame(
    frame,
    `(() => {
      const element = ${element};
      element.closest('[contenteditable="true"]').focus();
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      let node = walker.nextNode();
      while (node && !node.data.includes(${JSON.stringify(text)})) node = walker.nextNode();
      const start = node.data.indexOf(${JSON.stringify(text)});
      const range = document.createRange();
      range.setStart(node, start);
      range.setEnd(node, start + ${text.length});
      const selection = getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      return true;
    })()`,
  );
  await delay(200);
}

/** 向 frame 中获得焦点的元素输入文字。Types text into the focused element of a frame. */
async function typeIn(frame: FrameHandle, text: string): Promise<void> {
  frame.view.webContents.focus();
  await frame.view.webContents.insertText(text);
}

function saveIn(frame: FrameHandle): void {
  pressKey(frame, "s", [MOD]);
}

/** 把 Monaco 的光标移到文档开头或末尾。Moves Monaco's cursor to the start or end of the document. */
function moveCodeCursor(frame: FrameHandle, to: "start" | "end"): void {
  if (process.platform === "darwin") pressKey(frame, to === "end" ? "Down" : "Up", ["meta"]);
  else pressKey(frame, to === "end" ? "End" : "Home", ["control"]);
}

async function untilFileText(path: string, expected: string, label: string): Promise<void> {
  await until(
    async () => (await readFile(path, "utf8")) === expected,
    label,
    20_000,
    async () => JSON.stringify(await readFile(path, "utf8")),
  );
}

async function untilFileBytes(path: string, expected: Buffer, label: string): Promise<void> {
  await until(
    async () => (await readFile(path)).equals(expected),
    label,
    20_000,
    async () => (await readFile(path)).toString("hex"),
  );
}

/** 等待 Edit 显示满足条件的已保存文档。Waits for Edit to show a saved document matching the predicate. */
async function editShowing(predicate: string, label: string): Promise<FrameHandle> {
  const frame = await untilViewerFrame("text-editor", "markdown");
  await until(
    () => evalFrame<boolean>(frame, `${SAVED} && Boolean(${EDITOR}) && (${predicate})`),
    label,
  );
  return frame;
}

/** 等待 Code 显示满足条件的已保存文档。Waits for Code to show a saved document matching the predicate. */
async function codeShowing(predicate: string, label: string): Promise<FrameHandle> {
  const frame = await untilViewerFrame("text-editor", "code");
  await until(
    () =>
      evalFrame<boolean>(
        frame,
        `${SAVED} && Boolean(document.querySelector(".code-status-bar")) && (${predicate})`,
      ),
    label,
  );
  return frame;
}

/**
 * 宿主绘制的冲突对话框：标题为 extension 名称，文字与按钮符合文档会话的规定，默认焦点为 Cancel，按钮都在窗口内可以点到，
 * 被盖住的查看器换成已画出的截图。
 * The host-drawn conflict dialog: titled with the extension name, with the document session's text and buttons,
 * Cancel focused, every button clickable inside the window, and the covered viewer swapped for its painted snapshot.
 */
async function expectConflictDialog(
  title: string,
  fileName: string,
  frame: FrameHandle,
): Promise<void> {
  await untilHost(
    `${OPEN_MESSAGE_BOX}?.querySelector('[data-slot="dialog-title"]')?.textContent === ${JSON.stringify(title)}`,
    `the ${title} conflict dialog`,
  );
  const readBox = () =>
    evalHost<{ clickable: boolean[]; focused: string | null }>(`(() => {
    const dialog = ${OPEN_MESSAGE_BOX};
    const buttons = [...dialog.querySelectorAll("[data-message-button]")];
    const clickable = (element) => {
      const rect = element.getBoundingClientRect();
      const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
      return rect.left >= 0 && rect.top >= 0 && rect.right <= innerWidth && rect.bottom <= innerHeight
        && (hit === element || element.contains(hit));
    };
    return {
      message: dialog.querySelector('[data-slot="dialog-description"]').textContent,
      detail: dialog.textContent.includes("Overwrite it with your version, or reload it and discard your changes."),
      buttons: buttons.map((button) => button.textContent),
      clickable: buttons.map(clickable),
      focused: document.activeElement?.textContent ?? null,
    };
  })()`);
  // 打开动画结束、焦点移到默认按钮之后再检查。Checked once the opening animation ends and focus reaches the default
  // button.
  const box = await until(
    async () => {
      const current = await readBox();
      return current.clickable.every(Boolean) && current.focused === "Cancel" ? current : null;
    },
    `the ${title} conflict dialog to settle`,
    20_000,
    async () =>
      `${JSON.stringify(await readBox())}; ${await evalHost<string>(`(() => {
        const button = ${OPEN_MESSAGE_BOX}.querySelector("[data-message-button]");
        const rect = button.getBoundingClientRect();
        const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
        const layers = [...document.querySelectorAll('[data-slot$="-overlay"], [data-slot$="-content"], [data-sonner-toast]')].map((element) => [element.dataset.slot ?? "toast", element.dataset.state, getComputedStyle(element).pointerEvents, getComputedStyle(element).opacity, element.getAttribute("data-host-confirmation")]);
        return JSON.stringify({ rect, hit: hit?.outerHTML.slice(0, 200) ?? null, layers, bodyPointer: getComputedStyle(document.body).pointerEvents, hasFocus: document.hasFocus() });
      })()`)}`,
  );
  assert.deepEqual(box, {
    message: `${fileName} was changed on disk after it was opened.`,
    detail: true,
    buttons: ["Overwrite", "Reload", "Cancel"],
    clickable: [true, true, true],
    focused: "Cancel",
  });
  await until(() => !frame.view.getVisible(), "the viewer to be replaced by its snapshot");
  assert.equal(await evalHost<boolean>(snapshotLoaded(frame.frameId)), true);
}

/** frame 中运行的 worker 的脚本地址（通过 DevTools 协议）。Script URLs of the workers running in a frame (through CDP). */
async function workerUrls(frame: FrameHandle): Promise<string[]> {
  const debuggerApi = frame.view.webContents.debugger;
  debuggerApi.attach("1.3");
  try {
    const urls: string[] = [];
    const onMessage = (
      _event: unknown,
      method: string,
      params: { targetInfo?: { url: string } },
    ) => {
      if (method === "Target.attachedToTarget" && params.targetInfo)
        urls.push(params.targetInfo.url);
    };
    debuggerApi.on("message", onMessage);
    await debuggerApi.sendCommand("Target.setAutoAttach", {
      autoAttach: true,
      waitForDebuggerOnStart: false,
      flatten: true,
    });
    await delay(300);
    debuggerApi.off("message", onMessage);
    return urls;
  } finally {
    debuggerApi.detach();
  }
}

async function closeToasts(): Promise<void> {
  while (await evalHost<boolean>(`Boolean(document.querySelector('[data-sonner-toast]'))`)) {
    await evalHost(
      `document.querySelector('[data-sonner-toast] [data-close-button]')?.click(); true`,
    );
    await delay(300);
  }
}

async function viewerOrderAndDefaults(): Promise<void> {
  for (const [path, tabs] of [
    ["notes/edit.md", [EDIT_VIEWER, MARKDOWN_VIEWER, CODE_VIEWER, "source"]],
    ["diagram.excalidraw", [EXCALIDRAW_VIEWER, CODE_VIEWER, "source"]],
    ["src/app.ts", [CODE_VIEWER, "source"]],
  ] as const) {
    await openFileInTree(path);
    await untilHost(selectedViewerIs(tabs[0]), `the default viewer of ${path}`);
    assert.deepEqual(await evalHost<string[]>(FILE_VIEWER_TABS), tabs, path);
  }
  pass(
    "viewers follow their priority: Edit, Preview, Code for Markdown, Excalidraw then Code for diagrams, Code for other files",
  );

  const settingsPath = join(config.userData, "settings.json");
  const defaults = async () =>
    (await readJson<{ fileViewerDefaults: Record<string, string> }>(settingsPath))
      .fileViewerDefaults;
  const more = '.agw-main-workspace button[aria-label="More"]';
  await openFileInTree("notes/edit.md");
  await selectViewer(CODE_VIEWER);
  // 切换到 Code 后立即打开 More 菜单（宿主页面有焦点，与点击按钮相同）：Code 随后读取完文件并让编辑器获得焦点，
  // 菜单保持打开。菜单盖住 Code 时 view 换成截图并隐藏，Monaco 不绘制文字，所以这里按状态栏中的语言判断读取完成。
  // Open the More menu right after switching to Code (the host page has focus, as after clicking the button): Code then
  // finishes reading the file and focuses its editor, and the menu stays open. Under the menu the Code view is swapped
  // for its snapshot and hidden, so Monaco draws no text; the status bar's language shows the read finished.
  host().focus();
  await openMenuAndWait(more);
  const codeHoldsEditMd = `${SAVED} && Boolean(document.querySelector(".code-status-bar")?.textContent.includes("Markdown"))`;
  const codeBeforeMenu = viewerFrameOf("text-editor", "code");
  const loadedBeforeMenu =
    codeBeforeMenu !== undefined && (await evalFrame<boolean>(codeBeforeMenu, codeHoldsEditMd));
  const code = await untilViewerFrame("text-editor", "code");
  await until(() => evalFrame<boolean>(code, codeHoldsEditMd), "edit.md read in Code");
  await delay(500);
  console.log(
    `More menu after a Code file switch: window focused ${hooks.mainWindow()!.isFocused()}, Code had read the file before the menu opened ${loadedBeforeMenu}`,
  );
  assert.equal(
    await evalHost<boolean>(`Boolean(${OPEN_MENU})`),
    true,
    "the More menu stays open after Code reads the file",
  );
  await clickText("Open with Code by default for *.md", OPEN_MENU);
  await until(
    async () => (await defaults())["*.md"] === CODE_VIEWER,
    "the *.md default in settings.json",
  );
  await openFileInTree("readme.md");
  await untilHost(selectedViewerIs(CODE_VIEWER), "Code as the default for readme.md");
  await codeShowing(`${CODE_TEXT}.includes("hello cloud")`, "readme.md in Code");
  // 用户默认的查看器排在最前。The user's default viewer comes first.
  assert.deepEqual(await evalHost<string[]>(FILE_VIEWER_TABS), [
    CODE_VIEWER,
    EDIT_VIEWER,
    MARKDOWN_VIEWER,
    "source",
  ]);
  await untilHost(`!${OPEN_MENU}`, "the More menu to close");
  await openMenuAndWait(more);
  await clickText("Reset default for *.md", OPEN_MENU);
  await until(async () => !("*.md" in (await defaults())), "the *.md default to be removed");
  // 重置不切换正在显示的查看器。The reset keeps the viewer being shown.
  await untilHost(
    `${FILE_VIEWER_TABS}.join() === ${JSON.stringify([EDIT_VIEWER, MARKDOWN_VIEWER, CODE_VIEWER, "source"].join())}`,
    "the priority order after the reset",
  );
  assert.equal(await evalHost<boolean>(selectedViewerIs(CODE_VIEWER)), true);
  await openFileInTree("guide.md");
  await untilHost(selectedViewerIs(EDIT_VIEWER), "Edit as the default for guide.md");
  pass(
    "More sets Code as the default for *.md in settings.json, and after Reset default for *.md the shown file keeps Code while other Markdown files open in Edit",
  );
}

async function textEditorMarkdown(): Promise<void> {
  const path = projectPath("notes/edit.md");
  const original = await readFile(path, "utf8");
  // 回到同一个文件时文件视图保留之前选中的 Code，这里重新选中 Edit。Returning to the same file keeps the Code chosen
  // earlier, so Edit is selected again.
  await openFileInTree("notes/edit.md");
  await selectViewer(EDIT_VIEWER);
  const edit = await editShowing(`Boolean(${editBlock("Second paragraph", "p")})`, "edit.md");

  // 1-2：修改一个 paragraph 后保存，只有这个 paragraph 变化；展开的 notes/ 重新读取子项。Editing one paragraph changes
  // only that paragraph on save; the expanded notes/ reads its children again.
  await placeCaret(edit, editBlock("Second paragraph", "p"));
  await typeIn(edit, " Typed in Edit.");
  await untilHost(unsavedIn(EDIT_VIEWER), "the unsaved marker");
  await writeFile(projectPath("notes/added.md"), "added on disk\n");
  await delay(500);
  assert.equal(await evalHost<boolean>(`Boolean(${treeItem("added.md")})`), false);
  saveIn(edit);
  await untilHost(`!${unsavedIn(EDIT_VIEWER)}`, "the unsaved marker to clear");
  const typed = original.replace("at the end.\n", "at the end. Typed in Edit.\n");
  await untilFileText(path, typed, "the paragraph edit on disk");
  await untilHost(`Boolean(${treeItem("added.md")})`, "notes/ to be read again after the save");
  pass("Edit saves a paragraph edit with Cmd/Ctrl+S, changing only that paragraph on disk");
  pass("saving in Edit reads the expanded notes/ directory of the file tree again");

  // 3：修改链接定义中的地址，只有该定义所在的行变化。Editing a definition's address changes only its line.
  await placeCaret(
    edit,
    editBlock("https://example.com/first", "div.raw-block"),
    "https://example.com/first",
  );
  await typeIn(edit, "-v2");
  await untilHost(unsavedIn(EDIT_VIEWER), "the unsaved marker for the definition");
  saveIn(edit);
  await untilHost(`!${unsavedIn(EDIT_VIEWER)}`, "the unsaved marker to clear");
  const definition = typed.replace(
    "[docs]: https://example.com/first\n",
    "[docs]: https://example.com/first-v2\n",
  );
  await untilFileText(path, definition, "the definition edit on disk");
  pass("editing a link definition changes only its line");

  // 4：空 paragraph 中输入 `/` 选择 Heading 2。`/` in an empty paragraph, then Heading 2.
  const trailing = `${EDITOR}.lastElementChild`;
  assert.equal(
    await evalFrame<boolean>(edit, `${trailing}.tagName === "P" && ${trailing}.textContent === ""`),
    true,
  );
  await placeCaret(edit, trailing);
  await typeIn(edit, "/");
  const heading2 = `[...document.querySelectorAll('.slash-menu [role="option"]')].find((option) => option.textContent === "Heading 2")`;
  await clickInFrame(edit, heading2);
  await until(
    () => evalFrame<boolean>(edit, `Boolean(document.querySelector(".ProseMirror > h2"))`),
    "the Heading 2 block",
  );
  await typeIn(edit, "New section");
  saveIn(edit);
  await untilHost(`!${unsavedIn(EDIT_VIEWER)}`, "the unsaved marker to clear");
  const heading = `${definition}\n## New section\n`;
  await untilFileText(path, heading, "the new heading on disk");
  pass("the / menu turns an empty paragraph into Heading 2, saved as a ## line");

  // 5：点击任务项的复选框。Clicking a task item's checkbox.
  await clickInFrame(
    edit,
    `[...document.querySelectorAll(".ProseMirror li.task-item")].find((item) => item.textContent.includes("open task"))?.querySelector('input[type="checkbox"]')`,
  );
  await untilHost(unsavedIn(EDIT_VIEWER), "the unsaved marker for the checkbox");
  saveIn(edit);
  await untilHost(`!${unsavedIn(EDIT_VIEWER)}`, "the unsaved marker to clear");
  const checked = heading.replace("- [ ] open task", "- [x] open task");
  await untilFileText(path, checked, "the checked task on disk");
  pass("clicking a task checkbox saves only that item as - [x]");

  // 6：Preview 显示保存的内容。Preview shows the saved content.
  await selectPreview();
  const preview = await untilViewerFrame("text-editor", "preview");
  await until(
    () =>
      evalFrame<boolean>(
        preview,
        `document.querySelector("article.markdown-body h2")?.textContent === "New section"`,
      ),
    "the saved content in Preview",
  );
  assert.deepEqual(
    await evalFrame(
      preview,
      `(() => {
        const article = document.querySelector("article.markdown-body");
        return {
          typed: article.textContent.includes("at the end. Typed in Edit."),
          href: [...article.querySelectorAll("a")].find((link) => link.textContent === "reference link")?.getAttribute("href") ?? null,
          openTask: [...article.querySelectorAll("li")].find((item) => item.textContent.includes("open task"))?.querySelector("input")?.checked ?? null,
        };
      })()`,
    ),
    { typed: true, href: "https://example.com/first-v2", openTask: true },
  );
  pass("Preview shows the content saved in Edit, with the link pointing to the new address");

  // 7：Code 中修改并保存，回到 Edit 时 onShow 读取新内容。Code edits and saves; back in Edit, onShow reads it.
  await selectViewer(CODE_VIEWER);
  // Monaco 只渲染可见的行，这里检查文件前部保存过的 paragraph。Monaco renders only visible lines, so the saved
  // paragraph near the start of the file is checked. 快捷键要等 Monaco 获得焦点后再按下。The shortcut waits until Monaco has focus.
  const code = await codeShowing(
    `${CODE_TEXT}.includes("at the end. Typed in Edit.") && Boolean(document.querySelector(".code-editor .monaco-editor")?.contains(document.activeElement))`,
    "the saved edit.md in Code",
  );
  moveCodeCursor(code, "end");
  await typeIn(code, "Code line.");
  await untilHost(unsavedIn(CODE_VIEWER), "the unsaved marker in Code");
  saveIn(code);
  await untilHost(`!${unsavedIn(CODE_VIEWER)}`, "the unsaved marker in Code to clear");
  await untilFileText(path, `${checked}Code line.`, "the Code edit on disk");
  await selectViewer(EDIT_VIEWER);
  await editShowing(`Boolean(${editBlock("Code line.", "p")})`, "Edit to show the Code change");
  assert.equal(await evalHost<boolean>(unsavedIn(EDIT_VIEWER)), false);
  pass("a change saved in Code shows in Edit when it is shown again");
}

/**
 * 在 frame 中用鼠标从一个元素的拖动手柄拖到另一个元素上方，返回拖动前手柄的中心点。
 * Drags a block by its handle onto another element and returns the handle's center before the drag.
 */
async function dragBlock(
  frame: FrameHandle,
  block: string,
  target: string,
): Promise<{ x: number; y: number }> {
  const contents = frame.view.webContents;
  const debuggerApi = contents.debugger;
  debuggerApi.attach("1.3");
  try {
    const mouse = (params: Record<string, unknown>) =>
      debuggerApi.sendCommand("Input.dispatchMouseEvent", params);
    const blockPoint = await until(() => centerOf(frame, block), "the block to drag");
    await evalFrame(
      frame,
      `window.__moves = window.__moves ?? 0; document.addEventListener("mousemove", () => window.__moves++, true); true`,
    );
    await mouse({ type: "mouseMoved", x: blockPoint.x, y: blockPoint.y });
    const handle = `document.querySelector(".drag-handle")`;
    const handlePoint = await until(
      async () => {
        const visible = await evalFrame<boolean>(
          frame,
          `getComputedStyle(${handle}).visibility === "visible"`,
        );
        const center = await centerOf(frame, handle);
        return visible && center?.hit ? center : null;
      },
      "the drag handle next to the block",
      20_000,
      () =>
        evalFrame<string>(
          frame,
          `(() => { const handle = ${handle}; const rect = handle?.getBoundingClientRect(); const hit = rect && document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2); return JSON.stringify({ moves: window.__moves, count: document.querySelectorAll(".drag-handle").length, style: handle?.getAttribute("style"), rect, hit: hit?.outerHTML.slice(0, 120), block: ${JSON.stringify(blockPoint)} }); })()`,
        ),
    );
    await debuggerApi.sendCommand("Input.setInterceptDrags", { enabled: true });
    const intercepted = new Promise<unknown>((resolveDrag) => {
      const onMessage = (_event: unknown, method: string, params: { data: unknown }) => {
        if (method !== "Input.dragIntercepted") return;
        debuggerApi.off("message", onMessage);
        resolveDrag(params.data);
      };
      debuggerApi.on("message", onMessage);
    });
    await mouse({ type: "mouseMoved", x: handlePoint.x, y: handlePoint.y });
    await mouse({
      type: "mousePressed",
      x: handlePoint.x,
      y: handlePoint.y,
      button: "left",
      buttons: 1,
      clickCount: 1,
    });
    const end = await evalFrame<{ x: number; y: number }>(
      frame,
      `(() => { const rect = ${target}.getBoundingClientRect(); return { x: rect.left + 10, y: rect.top + 2 }; })()`,
    );
    await mouse({ type: "mouseMoved", x: end.x, y: end.y, button: "left", buttons: 1 });
    const data = await Promise.race([
      intercepted,
      delay(5000).then(() => {
        throw new Error("The drag did not start from the handle.");
      }),
    ]);
    for (const type of ["dragEnter", "dragOver", "drop"]) {
      await debuggerApi.sendCommand("Input.dispatchDragEvent", { type, x: end.x, y: end.y, data });
    }
    await mouse({
      type: "mouseReleased",
      x: end.x,
      y: end.y,
      button: "left",
      buttons: 0,
      clickCount: 1,
    });
    await debuggerApi.sendCommand("Input.setInterceptDrags", { enabled: false });
    return { x: handlePoint.x, y: handlePoint.y };
  } finally {
    debuggerApi.detach();
  }
}

/**
 * Edit 与 Preview 使用共用排版：默认设置下按计算样式检查任务项、表格单元格、引用链接、拖动手柄菜单、引用块中的代码块与正文。
 * Edit and Preview use the shared typography: at the default settings the computed styles of task items, table cells,
 * a reference link, the drag-handle menu, a code block in a blockquote and body text are checked.
 */
async function textEditorTypography(): Promise<void> {
  await openFileInTree("notes/typography.md");
  await selectViewer(EDIT_VIEWER);
  const edit = await editShowing(`Boolean(${editBlock("Top paragraph", "p")})`, "typography.md");
  const styles = await evalFrame<Record<string, unknown>>(
    edit,
    `(() => {
      const root = document.querySelector(".ProseMirror");
      const center = (element) => { const rect = element.getBoundingClientRect(); return rect.top + rect.height / 2; };
      const item = [...root.querySelectorAll("li.task-item")].find((candidate) => candidate.textContent.includes("first task"));
      const link = [...root.querySelectorAll("a")].find((anchor) => anchor.textContent === "typography link");
      const margins = (element) => { const style = getComputedStyle(element); return [style.marginTop, style.marginBottom]; };
      const paragraph = root.querySelector(":scope > p");
      return {
        rootClass: root.className.includes("agw-prose") && root.getAttribute("data-density") === "document",
        taskOffset: Math.abs(center(item.querySelector("input[type=checkbox]")) - center(item.querySelector(".task-content > p"))),
        cellMargins: [...root.querySelectorAll("th > p, td > p")].map(margins),
        linkDecoration: link ? getComputedStyle(link).textDecorationLine : null,
        quotedCodeMarginTop: getComputedStyle(root.querySelector("blockquote > .code-block")).marginTop,
        paragraphSize: getComputedStyle(paragraph).fontSize,
        paragraphLineHeight: getComputedStyle(paragraph).lineHeight,
      };
    })()`,
  );
  assert.ok(styles.rootClass, "the editing area carries agw-prose and the document density");
  assert.ok(
    (styles.taskOffset as number) <= 1,
    `the task text center is ${styles.taskOffset}px from the checkbox center`,
  );
  const cells = styles.cellMargins as string[][];
  assert.ok(cells.length >= 4, `table cell paragraphs: ${cells.length}`);
  assert.deepEqual(
    cells.flat().filter((margin) => margin !== "0px"),
    [],
    "table cell paragraphs have no margin",
  );
  assert.ok(String(styles.linkDecoration).includes("underline"), `link: ${styles.linkDecoration}`);
  assert.equal(styles.quotedCodeMarginTop, "0px");
  assert.equal(styles.paragraphSize, "16px");
  assert.equal(styles.paragraphLineHeight, "28px");

  // 悬停到块上，点击拖动手柄打开 Turn into 菜单。Hover the block and click the drag handle to open the Turn into menu.
  const block = await until(
    () => centerOf(edit, editBlock("Top paragraph", "p")),
    "the paragraph to hover",
  );
  edit.view.webContents.sendInputEvent({
    type: "mouseMove",
    x: Math.round(block.x),
    y: Math.round(block.y),
  });
  await clickInFrame(edit, `document.querySelector(".drag-handle .chrome-button")`);
  await until(
    () => evalFrame<boolean>(edit, `Boolean(document.querySelector(".drag-handle .popup-menu"))`),
    "the drag-handle menu",
  );
  assert.equal(
    await evalFrame<string>(
      edit,
      `getComputedStyle(document.querySelector(".drag-handle .popup-menu")).fontSize`,
    ),
    "14px",
  );
  pass(
    "Edit computes the shared typography: aligned task items, margin-free cell paragraphs, underlined reference links, 14px menus, a flush code block in a quote, 16px/28px text",
  );

  await selectPreview();
  const preview = await untilViewerFrame("text-editor", "preview");
  await until(
    () =>
      evalFrame<boolean>(
        preview,
        `document.querySelector("article.markdown-body h1")?.textContent === "Typography sample"`,
      ),
    "typography.md in Preview",
  );
  assert.deepEqual(
    await evalFrame(
      preview,
      `(() => {
        const article = document.querySelector("article.markdown-body");
        return [
          getComputedStyle(article.querySelector(":scope > p")).fontSize,
          getComputedStyle(article.querySelector("h1")).fontSize,
        ];
      })()`,
    ),
    ["16px", "28px"],
  );
  pass("Preview computes 16px paragraphs and 28px level 1 headings at the default settings");
}

/**
 * first-line.md 中的块与各块已知的第一行：文字块选取第一个文字的第一个字符，代码块选取标题栏的语言选择框，分隔线选取自身的外框。
 * `line` 在 frame 中求值，可以使用 `block` 与 `firstCharacter`。
 * The blocks of first-line.md and each block's known first line: text blocks use the first character of their first
 * text, the code block its header's language select, the rule its own box. `line` is evaluated in the frame and may
 * use `block` and `firstCharacter`.
 */
const FIRST_LINE_BLOCKS: { name: string; block: string; line: string }[] = [
  {
    name: "front matter",
    block: `document.querySelector('.ProseMirror > .raw-block[data-kind="front-matter"]')`,
    line: `firstCharacter(block.querySelector(".raw-block-label"))`,
  },
  {
    name: "paragraph",
    block: editBlock("Paragraph line", "p"),
    line: "firstCharacter(block)",
  },
  { name: "H1", block: editBlock("Level one", "h1"), line: "firstCharacter(block)" },
  { name: "H3", block: editBlock("Level three", "h3"), line: "firstCharacter(block)" },
  {
    name: "list item",
    block: `[...document.querySelectorAll(".ProseMirror > ul > li")].find((item) => item.textContent === "Bullet item")`,
    line: `firstCharacter(block.querySelector("p"))`,
  },
  {
    name: "task item",
    block: `document.querySelector(".ProseMirror > ul > li.task-item")`,
    line: `firstCharacter(block.querySelector(".task-content > p"))`,
  },
  {
    name: "blockquote",
    block: editBlock("Quoted line", "blockquote"),
    line: `firstCharacter(block.querySelector("p"))`,
  },
  {
    name: "code block",
    block: editBlock("const line = 1;", "div.code-block"),
    line: `block.querySelector(".code-block-header select").getBoundingClientRect()`,
  },
  {
    name: "table",
    block: editBlock("Cell name", ".tableWrapper"),
    line: `firstCharacter(block.querySelector("th > p"))`,
  },
  {
    name: "image placeholder",
    block: `document.querySelector(".ProseMirror > p .image-placeholder")?.closest(".ProseMirror > p")`,
    line: `firstCharacter(block.querySelector(".image-placeholder-alt"))`,
  },
  {
    name: "horizontal rule",
    block: `document.querySelector(".ProseMirror > hr")`,
    line: "block.getBoundingClientRect()",
  },
  {
    name: "HTML block",
    block: `document.querySelector('.ProseMirror > .raw-block[data-kind="html"]')`,
    line: `firstCharacter(block.querySelector(".raw-block-label"))`,
  },
];

const DRAG_HANDLE = `document.querySelector(".drag-handle")`;

/**
 * 依次用鼠标悬停 first-line.md 的每个块，检查手柄中心与块的第一行中心相差不超过 1px，所有手柄的 x 与 paragraph 的相同。
 * Hovers every block of first-line.md with the mouse and checks that the handle center is within 1px of the block's
 * first-line center and that every handle has the paragraph handle's x.
 */
async function checkFirstLineHandles(frame: FrameHandle, size: string): Promise<void> {
  const debuggerApi = frame.view.webContents.debugger;
  debuggerApi.attach("1.3");
  try {
    const moveMouse = (point: { x: number; y: number }) =>
      debuggerApi.sendCommand("Input.dispatchMouseEvent", { type: "mouseMoved", ...point });
    // 顶部条在编辑区之外：滚动期间鼠标停留在这里，不会悬停到别的块上。The top bar lies outside the editing area; the mouse
    // rests there while scrolling, so it hovers no other block.
    const outside = await evalFrame<{ x: number; y: number }>(
      frame,
      `(() => { const rect = document.querySelector(".edit-toolbar").getBoundingClientRect(); return { x: rect.left + 8, y: rect.top + rect.height / 2 }; })()`,
    );
    const results: { name: string; handleX: number; handleCenter: number; lineCenter: number }[] =
      [];
    for (const { name, block, line } of FIRST_LINE_BLOCKS) {
      await moveMouse(outside);
      const before = await evalFrame<string>(frame, `${DRAG_HANDLE}.style.top`);
      // 鼠标在块的水平中点、顶部之下 2px；只有 1px 高的分隔线选取它的垂直中点。The mouse goes to the block's horizontal
      // middle, 2px below its top; the 1px high rule takes its vertical middle.
      const point = await evalFrame<{ x: number; y: number }>(
        frame,
        `(() => {
          const block = ${block};
          block.scrollIntoView({ block: "center" });
          const rect = block.getBoundingClientRect();
          return { x: rect.left + rect.width / 2, y: rect.top + Math.min(2, rect.height / 2) };
        })()`,
      );
      await moveMouse(point);
      // 手柄切换到这个块时 top 改变。The handle's top changes when it moves to this block.
      await until(
        () =>
          evalFrame<boolean>(
            frame,
            `getComputedStyle(${DRAG_HANDLE}).visibility === "visible" && ${DRAG_HANDLE}.style.top !== ${JSON.stringify(before)}`,
          ),
        `the drag handle next to the ${name} at ${size}`,
        20_000,
        async () =>
          `${await evalFrame<string>(
            frame,
            `JSON.stringify({ point: ${JSON.stringify(point)}, before: ${JSON.stringify(before)}, style: ${DRAG_HANDLE}.getAttribute("style"), block: (${block}).getBoundingClientRect() })`,
          )}; measured before: ${JSON.stringify(results)}`,
      );
      const measured = await evalFrame<{
        handleX: number;
        handleCenter: number;
        lineCenter: number;
      }>(
        frame,
        `(() => {
          const firstCharacter = (element) => {
            const text = element.firstChild;
            if (!(text instanceof Text)) throw new Error("The element does not start with text.");
            const range = document.createRange();
            range.setStart(text, 0);
            range.setEnd(text, 1);
            return range.getClientRects()[0];
          };
          const block = ${block};
          const line = ${line};
          const handle = ${DRAG_HANDLE}.getBoundingClientRect();
          return { handleX: handle.left, handleCenter: handle.top + handle.height / 2, lineCenter: line.top + line.height / 2 };
        })()`,
      );
      results.push({ name, ...measured });
    }
    console.log(
      `drag handle first lines at ${size}: ${JSON.stringify(results.map((result) => ({ ...result, offset: round(result.handleCenter - result.lineCenter) })))}`,
    );
    assert.deepEqual(
      results
        .filter((result) => Math.abs(result.handleCenter - result.lineCenter) > 1)
        .map((result) => `${result.name} ${round(result.handleCenter - result.lineCenter)}px`),
      [],
      `drag handles off the first line at ${size}`,
    );
    const paragraphX = results.find((result) => result.name === "paragraph")!.handleX;
    assert.deepEqual(
      results
        .filter((result) => result.handleX !== paragraphX)
        .map((result) => `${result.name} x ${result.handleX}`),
      [],
      `drag handles outside the paragraph handle's column (x ${paragraphX}) at ${size}`,
    );
  } finally {
    debuggerApi.detach();
  }
}

/** settings.json 中的外观设置。The appearance settings in settings.json. */
async function storedAppearance(): Promise<AppearanceSettings> {
  return (
    await readJson<{ appearance: AppearanceSettings }>(join(config.userData, "settings.json"))
  ).appearance;
}

/**
 * 在 Appearance 页面保存外观设置，等待 settings.json 中给出的字段与宿主字号更新。
 * Saves appearance settings on the Appearance page and waits for the given fields in settings.json and the host font
 * size to update.
 */
async function saveAppearance(
  appearance: Partial<AppearanceSettings> & { baseFontSize: number },
  action: () => Promise<void>,
) {
  await openSettingsPage("/settings/appearance/");
  await untilHost(
    `Boolean(document.getElementById("appearance-base-font-size"))`,
    "the Appearance page",
  );
  await action();
  await until(
    async () => {
      const stored = await storedAppearance();
      return Object.entries(appearance).every(
        ([name, value]) => stored[name as keyof AppearanceSettings] === value,
      );
    },
    `the appearance ${JSON.stringify(appearance)} in settings.json`,
  );
  await untilHost(
    `getComputedStyle(document.documentElement).fontSize === "${appearance.baseFontSize}px"`,
    `the ${appearance.baseFontSize}px host font size`,
  );
}

/** Reset all 保存的外观。The appearance Reset all saves. */
const RESET_APPEARANCE: AppearanceSettings = { ...DEFAULT_APPEARANCE, mode: "system" };

async function openFirstLine(): Promise<FrameHandle> {
  await openFileInTree("first-line.md");
  await untilHost(selectedViewerIs(EDIT_VIEWER), "Edit for first-line.md");
  return editShowing(
    `Boolean(${editBlock("Paragraph line", "p")}) && Boolean(document.querySelector(".ProseMirror .image-placeholder-alt"))`,
    "first-line.md in Edit",
  );
}

/**
 * 拖动手柄垂直居中在被悬停块的第一行上（设计 5），默认字号与基础字号 20 下各检查一次，最后恢复默认字号。
 * The drag handle is vertically centered on the hovered block's first line (design section 5), checked at the default
 * size and at base size 20; the default sizes are restored at the end.
 */
async function dragHandleFirstLine(): Promise<void> {
  const edit = await openFirstLine();
  // 排版检查在同一个编辑器中留下打开的手柄菜单，菜单打开时手柄锁定；Escape 关闭菜单。The typography check leaves the
  // handle menu open in the same editor, which locks the handle; Escape closes the menu.
  pressKey(edit, "Escape");
  await until(
    () => evalFrame<boolean>(edit, `!document.querySelector(".drag-handle .popup-menu")`),
    "the drag-handle menu to close",
  );
  await checkFirstLineHandles(edit, "the default sizes");

  const larger: AppearanceSettings = { ...DEFAULT_APPEARANCE, baseFontSize: 20 };
  await saveAppearance(larger, async () => {
    await evalHost(`(() => {
      const select = document.getElementById("appearance-base-font-size");
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(select, "20");
      select.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    })()`);
  });
  const largerEdit = await openFirstLine();
  await until(
    () =>
      evalFrame<boolean>(
        largerEdit,
        `getComputedStyle(${editBlock("Paragraph line", "p")}).fontSize === "20px"`,
      ),
    "20px paragraphs in Edit",
  );
  await checkFirstLineHandles(largerEdit, "base size 20");

  await saveAppearance(RESET_APPEARANCE, () => clickText("Reset all"));
  await goToChat();
  pass(
    "the drag handle is centered on the first line of every block kind, in one column, at the default sizes and at base size 20",
  );
}

/**
 * 当前页面上每个拥有自己文字节点的可见元素（inline 元素改为检查最近的非 inline 祖先）中，文字被截断的元素。
 * 排除 `text-overflow: ellipsis`、可滚动容器、视觉隐藏元素，以及 textarea、input、select、pre 内的内容。
 * Every visible element of the current page that owns a text node (an inline element is tested through its nearest
 * non-inline ancestor) whose text is truncated. Excluded: `text-overflow: ellipsis`, scroll containers, visually hidden
 * elements, and the content of textarea, input, select and pre.
 */
const TRUNCATED_TEXT = `(() => {
  const describe = (element) => {
    const path = [];
    for (let node = element; node && node !== document.body && path.length < 4; node = node.parentElement) {
      const classes = [...node.classList].slice(0, 3).map((name) => "." + name).join("");
      path.unshift(node.tagName.toLowerCase() + classes);
    }
    return path.join(" > ");
  };
  const ownsText = (element) =>
    [...element.childNodes].some((node) => node.nodeType === Node.TEXT_NODE && node.textContent.trim() !== "");
  const tested = new Set();
  for (const element of document.body.querySelectorAll("*")) {
    if (!ownsText(element)) continue;
    if (element.closest("script, style, textarea, input, select, pre, svg")) continue;
    let target = element;
    while (target && ["inline", "contents"].includes(getComputedStyle(target).display)) target = target.parentElement;
    if (!target || target === document.body || target === document.documentElement) continue;
    tested.add(target);
  }
  const hidden = (element) => {
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0 || style.visibility === "hidden") return true;
    if (rect.width <= 1 && style.overflow === "hidden") return true;
    return (style.clip !== "auto" && style.clip !== "") || (style.clipPath !== "none" && style.clipPath !== "");
  };
  const scrolls = (style) => ["auto", "scroll"].includes(style.overflowX) || ["auto", "scroll"].includes(style.overflowY);
  const checked = [...tested].filter((element) => !hidden(element));
  const truncated = checked
    .filter((element) => {
      const style = getComputedStyle(element);
      if (style.textOverflow === "ellipsis" || scrolls(style)) return false;
      // overflow-y 不是 visible 时内容被裁剪，超出 1px 即报告；visible 时内容溢出并可能盖住下方元素，超出半个行高才报告，
      // 这样 leading-none 标题约 2px 的字形溢出不计入。
      // With overflow-y not visible the content is clipped, so more than 1px is reported; with visible the content spills
      // and may cover what is below, so half a line height is reported, which skips the ~2px glyph overflow of a
      // leading-none title.
      const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize);
      const spill = element.scrollHeight - element.clientHeight;
      const verticalSpill = style.overflowY === "visible" ? spill >= 0.5 * lineHeight : spill > 1;
      return element.scrollWidth > element.clientWidth + 1 || verticalSpill;
    })
    .map((element) => ({
      selector: describe(element),
      text: element.textContent.trim().slice(0, 60),
      scroll: element.scrollWidth + "x" + element.scrollHeight,
      client: element.clientWidth + "x" + element.clientHeight,
    }));
  return { checked: checked.length, truncated };
})()`;

type ScanResult = { checked: number; truncated: { selector: string; text: string }[] };

/** 每个视图的扫描结果，在 hostScaling 结尾一起断言。The scan result of every view, asserted together at the end of hostScaling. */
const scanResults: { view: string; checked: number; reported: ScanResult["truncated"] }[] = [];

/** 扫描当前页面并记录结果，同时断言标题栏高度为 48。Scans the current page, records the result, and asserts the title bar is 48 high. */
async function scanTruncation(view: string): Promise<void> {
  const { checked, truncated } = await evalHost<ScanResult>(TRUNCATED_TEXT);
  assert.ok(checked > 0, `${view} has no text elements to check`);
  console.log(`SCAN ${view}: checked=${checked} reported=${truncated.length}`);
  scanResults.push({ view, checked, reported: truncated });
  assert.equal(
    await evalHost<number>(
      `document.querySelector(".agw-titlebar").getBoundingClientRect().height`,
    ),
    48,
    `${view} title bar height`,
  );
}

/** 依次打开 Settings 导航的每个页面并扫描。Opens every page of the Settings navigation in turn and scans it. */
async function scanSettingsPages(suffix: string): Promise<number> {
  const navLinks = `[...document.querySelectorAll(".agw-settings-nav section > a")]`;
  if (!(await evalHost<boolean>(`Boolean(document.querySelector(".agw-settings-nav"))`))) {
    await clickHost('a[aria-label="Settings"]');
  }
  await untilHost(`${navLinks}.length > 0`, "the Settings navigation");
  const labels = await evalHost<string[]>(`${navLinks}.map((link) => link.textContent.trim())`);
  for (const [index, label] of labels.entries()) {
    await evalHost(`${navLinks}[${index}].click(); true`);
    await untilHost(
      `(() => {
        const target = new URL(${navLinks}[${index}].href);
        return location.pathname === target.pathname && location.hash === target.hash;
      })()`,
      `${label} to be shown`,
    );
    await delay(500);
    await scanTruncation(`the Settings page ${label}${suffix}`);
  }
  return labels.length;
}

/** Quick prompts 页面中 `user` 徽标的高度。The height of the `user` badge on the Quick prompts page. */
async function quickPromptBadgeHeight(): Promise<number> {
  await openSettingsPage("/quick-prompts/");
  const badge = `[...document.querySelectorAll('[data-slot="badge"]')].find((element) => element.textContent.trim() === "user")`;
  await untilHost(`Boolean(${badge})`, "the user badge on Quick prompts");
  return evalHost<number>(`${badge}.getBoundingClientRect().height`);
}

/** 通过 Host API 添加一个 Quick prompt，使列表行得以渲染。Adds one quick prompt through the Host API so its list row renders. */
async function seedQuickPrompt(): Promise<void> {
  const headers = {
    accept: "application/json",
    authorization: `Bearer ${config.firstToken}`,
    "content-type": "application/json",
  };
  const manage = (await (
    await fetch(`${config.hostUrl}/api/quick-prompts/manage`, { headers })
  ).json()) as {
    data: { userVersion?: number | null };
  };
  const response = await fetch(`${config.hostUrl}/api/quick-prompts?kind=user`, {
    method: "PUT",
    headers,
    body: JSON.stringify({
      version: manage.data.userVersion ?? null,
      items: [{ id: "scaling-check", label: "Scaling check", text: "Summarize the open file." }],
    }),
  });
  assert.equal(response.ok, true, `PUT /api/quick-prompts -> ${response.status}`);
}

/**
 * 基础字号 20 下，Chat、Files、每个 Settings 页面与服务器配置对话框没有被截断的文字，标题栏保持 48 高，
 * macOS 红绿灯位置不变；最后恢复默认字号。
 * At base size 20, Chat, Files, every Settings page and the server profile dialog show no truncated text, the title
 * bar stays 48 high and the macOS traffic lights stay in place; the default sizes are restored at the end.
 */
async function hostScaling(): Promise<void> {
  scanResults.length = 0;
  await seedQuickPrompt();
  await saveAppearance(
    { baseFontSize: 20, chatFontSize: null },
    chooseSize("appearance-base-font-size", "20"),
  );

  await openFileInTree("readme.md");
  await untilHost(
    `${viewerTab("text-editor:markdown")}?.getAttribute("aria-selected") === "true"`,
    "the Edit tab",
  );
  // 对照：宽 20px 且隐藏溢出的按钮必须被扫描报告。Negative control: a 20px wide button with hidden overflow must be reported.
  await evalHost(`(() => {
    const control = document.createElement("button");
    control.id = "truncation-control";
    control.style.cssText = "width:20px;overflow:hidden;white-space:nowrap";
    control.textContent = "Truncated control";
    document.body.append(control);
    const spill = document.createElement("div");
    spill.id = "spill-control";
    spill.style.cssText = "height:1.2em;width:60px";
    spill.textContent = "Spilling control text that needs several lines to fit";
    document.body.append(spill);
    return true;
  })()`);
  const control = await evalHost<ScanResult>(TRUNCATED_TEXT);
  await evalHost(
    `document.getElementById("truncation-control").remove(); document.getElementById("spill-control").remove(); true`,
  );
  assert.ok(
    control.truncated.some((entry) => entry.text === "Truncated control"),
    `the scan reports the control button: ${JSON.stringify(control.truncated)}`,
  );
  assert.ok(
    control.truncated.some((entry) => entry.text.startsWith("Spilling control text")),
    `the scan reports the spilling control: ${JSON.stringify(control.truncated)}`,
  );
  await scanTruncation("the Files view with an open viewer tab");
  await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
  await scanTruncation("the chat workspace");

  const settingsPages = await scanSettingsPages("");
  assert.equal(await quickPromptBadgeHeight(), 25, "the Quick prompts badge height at base 20");

  await clickText("Servers", `document.querySelector(".agw-settings-nav")`);
  await untilHost(
    `Boolean(document.querySelector('button[aria-label="Add remote Server"]'))`,
    "Servers",
  );
  await clickHost('button[aria-label="Add remote Server"]');
  await untilHost(`Boolean(${OPEN_DIALOG})`, "the server profile dialog");
  await delay(500);
  await scanTruncation("the server profile dialog");
  await pressEscape();
  await untilHost(`!${OPEN_DIALOG}`, "the server profile dialog to close");

  // 主窗口最小宽度下再检查一次 Chat 与 Settings 页面。Chat and the Settings pages are checked again at the main window's minimum width.
  const window = hooks.mainWindow()!;
  const [width, height] = window.getSize();
  window.setSize(980, height);
  await untilHost(`window.innerWidth <= 980`, "the minimum window width");
  await goToChat();
  await scanTruncation("the chat workspace at width 980");
  await scanSettingsPages(" at width 980");
  window.setSize(width, height);

  if (process.platform === "darwin") {
    assert.deepEqual(window.getWindowButtonPosition(), { x: 18, y: 18 });
  }

  await saveAppearance(RESET_APPEARANCE, () => clickText("Reset all"));
  assert.equal(await quickPromptBadgeHeight(), 20, "the Quick prompts badge height at base 16");
  await goToChat();
  const reported = scanResults.flatMap((result) =>
    result.reported.map((entry) => ({ view: result.view, ...entry })),
  );
  assert.deepEqual(reported, [], `truncated text: ${JSON.stringify(reported, null, 2)}`);
  pass(
    `at base size 20, ${settingsPages} Settings pages (also at width 980), Chat, Files and the server profile dialog show no truncated text (${scanResults.length} views, ${scanResults.reduce((sum, result) => sum + result.checked, 0)} elements checked, the control button reported), the title bar is 48 high and the traffic lights stay at 18, 18`,
  );
}

/** 在 Appearance 页面把下拉框设为 value 并触发 change。Sets a select on the Appearance page to value and fires change. */
function chooseSize(id: string, value: string): () => Promise<void> {
  return async () => {
    await evalHost(`(() => {
      const select = document.getElementById(${JSON.stringify(id)});
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(select, ${JSON.stringify(value)});
      select.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    })()`);
  };
}

/** Appearance 页面的 Settings 小标题（text-xs）的字号。The font size of the Appearance page's text-xs eyebrow. */
function eyebrowSize(): Promise<string> {
  return evalHost<string>(
    `getComputedStyle([...document.querySelectorAll("p.text-xs")].find((element) => element.textContent === "Chat preview")).fontSize`,
  );
}

const CHAT_PROSE = `.agw-prose[data-density="compact"]`;

/** 回到 Chat 并打开 Markdown 对话。Goes back to Chat and opens the Markdown conversation. */
async function showMarkdownReply(): Promise<void> {
  await goToChat();
  if (!(await evalHost<boolean>(`Boolean(${MARKDOWN_REPLY})`))) {
    if (
      !(await evalHost<boolean>(
        `Boolean(document.querySelector('.agw-left-sidebar[data-view-id="agw.conversations"]'))`,
      ))
    ) {
      await clickHost('.agw-ribbon [data-entry-id="agw.ribbon.conversations"]');
    }
    await untilHost(`Boolean(${MARKDOWN_CONVERSATION})`, "the Markdown conversation in the list");
    await evalHost(`${MARKDOWN_CONVERSATION}.click(); true`);
  }
  await untilHost(
    `Boolean(${MARKDOWN_REPLY}?.querySelector("section[data-footnotes]"))`,
    "the rendered Markdown reply",
  );
}

/** Markdown 回复在 compact 排版中的第一个 paragraph 的字号。The font size of the first paragraph of the Markdown reply's compact prose. */
async function chatParagraphSize(): Promise<string> {
  await showMarkdownReply();
  return evalHost<string>(`(() => {
    const reply = ${MARKDOWN_REPLY};
    const prose = reply.matches(${JSON.stringify(CHAT_PROSE)}) ? reply : (reply.closest(${JSON.stringify(CHAT_PROSE)}) ?? reply.querySelector(${JSON.stringify(CHAT_PROSE)}));
    return getComputedStyle(prose.querySelector("p")).fontSize;
  })()`);
}

/**
 * 当前设置下新打开的 Edit、Preview 与 Code 的字号。
 * The font sizes of Edit, Preview and Code opened now with the current settings.
 */
async function openedViewSizes(): Promise<{
  edit: string;
  editCode: string;
  preview: string;
  code: string;
}> {
  await showMarkdownReply();
  await openFileInTree("notes/typography.md");
  await selectViewer(EDIT_VIEWER);
  const edit = await editShowing(`Boolean(${editBlock("Top paragraph", "p")})`, "typography.md");
  const editSizes = await evalFrame<{ paragraph: string; code: string }>(
    edit,
    `(() => {
      const paragraph = ${editBlock("Top paragraph", "p")};
      return {
        paragraph: getComputedStyle(paragraph).fontSize,
        code: getComputedStyle(paragraph.querySelector("code")).fontSize,
      };
    })()`,
  );
  await selectPreview();
  const preview = await untilViewerFrame("text-editor", "preview");
  await until(
    () =>
      evalFrame<boolean>(
        preview,
        `document.querySelector("article.markdown-body h1")?.textContent === "Typography sample"`,
      ),
    "typography.md in Preview",
  );
  const previewSize = await evalFrame<string>(
    preview,
    `getComputedStyle(document.querySelector("article.markdown-body > p")).fontSize`,
  );
  await openFileInTree("src/app.ts");
  await untilHost(selectedViewerIs(CODE_VIEWER), "the Code tab for app.ts");
  const code = await codeShowing(
    `document.querySelectorAll(".code-editor .view-line").length > 0`,
    "app.ts in Code",
  );
  const codeSize = await evalFrame<string>(
    code,
    `getComputedStyle(document.querySelector(".code-editor .view-lines")).fontSize`,
  );
  return {
    edit: editSizes.paragraph,
    editCode: editSizes.code,
    preview: previewSize,
    code: codeSize,
  };
}

/**
 * Appearance 设置：字号随设置改变，已打开的 extension 后台 frame 不重建并收到新的主题令牌，新打开的视图使用当前字号，
 * 最小字号限制生效；Appearance 页面没有 Document font size，Edit 的文档字号跟随基础字号。
 * Appearance settings: sizes follow the settings, an extension's background frame stays alive and receives the new
 * theme tokens, views opened later use the current sizes, and the minimum font size applies; the Appearance page has
 * no Document font size and Edit's document size follows the base size.
 */
async function appearanceSettings(): Promise<void> {
  const background = await untilFrame("sample", "background");
  await evalFrame(background, `window.__appearanceMarker = "kept"; true`);
  const baseToken = (frame: FrameHandle) =>
    api<string>(frame, `return api.theme.get().tokens["--agw-font-size-base"];`);
  assert.equal(await baseToken(background), "16px");
  assert.equal(
    await api<string>(
      background,
      `return String(api.theme.get().tokens["--agw-font-size-document"]);`,
    ),
    "undefined",
  );

  // 基础字号 20：宿主、Settings 页面、Chat、新打开的 Edit、Preview 与 Code 一起放大。
  // Base size 20 scales the host, the Settings page, Chat and Edit, Preview and Code opened now.
  await saveAppearance(
    { ...DEFAULT_APPEARANCE, baseFontSize: 20 },
    chooseSize("appearance-base-font-size", "20"),
  );
  assert.equal(await eyebrowSize(), "15px");
  assert.equal(
    await evalHost<boolean>(
      `!document.getElementById("appearance-document-font-size") && !document.body.textContent.includes("Document font size")`,
    ),
    true,
    "the Appearance page has no Document font size",
  );
  await until(
    async () => (await baseToken(background)) === "20px",
    "the 20px base token in the background frame",
  );
  const kept = frameOf("sample", "background");
  assert.equal(kept?.frameId, background.frameId);
  assert.equal(await evalFrame<string>(background, "window.__appearanceMarker"), "kept");
  assert.equal(await chatParagraphSize(), "17.5px");
  const larger = await openedViewSizes();
  assert.equal(larger.edit, "20px");
  assert.equal(larger.preview, "20px");
  assert.equal(larger.code, "17.5px");
  pass(
    "base size 20 scales the host and the Settings page, updates the background frame's tokens without rebuilding it, and Chat, Edit, Preview and Code opened afterwards use the new sizes",
  );

  // 聊天字号单独设置，基础字号改变时保持不变，Edit 与 Preview 跟随基础字号。
  // The chat size is set on its own and stays when the base size changes; Edit and Preview follow the base size.
  await saveAppearance(
    { ...DEFAULT_APPEARANCE, baseFontSize: 20, chatFontSize: 15 },
    chooseSize("appearance-chat-font-size", "15"),
  );
  assert.equal(await chatParagraphSize(), "15px");
  let sizes = await openedViewSizes();
  assert.equal(sizes.edit, "20px");
  assert.equal(sizes.preview, "20px");
  await saveAppearance(
    { ...DEFAULT_APPEARANCE, baseFontSize: 17, chatFontSize: 15 },
    chooseSize("appearance-base-font-size", "17"),
  );
  assert.equal(await chatParagraphSize(), "15px");
  sizes = await openedViewSizes();
  assert.equal(sizes.edit, "17px");
  assert.equal(sizes.preview, "17px");
  pass(
    "the chat size 15 stays unchanged when the base size becomes 17, and Edit and Preview follow 17px",
  );

  // 重置后聊天字号回到跟随基础字号。Resetting returns the chat size to following the base size.
  await saveAppearance({ ...DEFAULT_APPEARANCE, baseFontSize: 17 }, () =>
    clickHost('button[aria-label="Reset chat font size"]'),
  );
  assert.equal(await chatParagraphSize(), "14.875px");
  sizes = await openedViewSizes();
  assert.equal(sizes.edit, "17px");
  assert.equal(sizes.preview, "17px");
  pass("Reset chat font size returns to 14.875px at base size 17");

  // 最小字号固定为 14px：text-xs 为 14px，Edit 行内代码不小于 14px，Appearance 页面没有最小字号设置。
  // The minimum font size is fixed at 14px: text-xs is 14px, Edit inline code is never below 14px, and the Appearance
  // page has no minimum font size setting.
  await saveAppearance(DEFAULT_APPEARANCE, chooseSize("appearance-base-font-size", "16"));
  assert.equal(await eyebrowSize(), "14px");
  assert.ok(parseFloat((await openedViewSizes()).editCode) >= 14);
  assert.equal(
    await evalHost<boolean>(
      `!document.getElementById("appearance-min-font-size") && !document.body.textContent.includes("Minimum font size")`,
    ),
    true,
    "the Appearance page has no minimum font size",
  );
  pass("the fixed minimum font size 14 limits text-xs and Edit inline code");
}

/* ---------- 主题 Themes ---------- */

const SOFT_THEME = "soft-theme:soft";
const CONTRAST_THEME = "contrast-theme:contrast";
const RING_THEME = "contrast-theme:ring";
const THEME_SELECT = `document.getElementById("appearance-theme")`;

/** 等待 settings.json 中的外观包含给出的字段。Waits for the appearance in settings.json to contain the given fields. */
async function untilAppearance(expected: Partial<AppearanceSettings>): Promise<void> {
  await until(
    async () => {
      const stored = await storedAppearance();
      return Object.entries(expected).every(
        ([name, value]) => stored[name as keyof AppearanceSettings] === value,
      );
    },
    `the appearance ${JSON.stringify(expected)} in settings.json`,
  );
}

/** 宿主根元素上变量的计算值。The computed value of a variable on the host root. */
function hostVariable(name: string): Promise<string> {
  return evalHost<string>(
    `getComputedStyle(document.documentElement).getPropertyValue(${JSON.stringify(name)}).trim()`,
  );
}

/** 宿主页面中元素的计算样式。A computed style of a host page element. */
function hostStyle(element: string, property: string): Promise<string> {
  return evalHost<string>(
    `getComputedStyle(${element}).getPropertyValue(${JSON.stringify(property)})`,
  );
}

/** CSS 颜色值在宿主页面中的计算颜色。The computed color the host page gives a CSS color value. */
function hostColor(value: string): Promise<string> {
  return evalHost<string>(`(() => {
    const probe = document.createElement("span");
    probe.style.color = ${JSON.stringify(value)};
    document.body.append(probe);
    const color = getComputedStyle(probe).color;
    probe.remove();
    return color;
  })()`);
}

/** 把鼠标移到宿主页面元素的中心。Moves the mouse to the center of a host page element. */
async function hoverHost(element: string): Promise<void> {
  const point = await evalHost<{ x: number; y: number }>(`(() => {
    const rect = ${element}.getBoundingClientRect();
    return { x: Math.round(rect.left + rect.width / 2), y: Math.round(rect.top + rect.height / 2) };
  })()`);
  host().sendInputEvent({ type: "mouseMove", x: point.x, y: point.y });
  await untilHost(`${element}.matches(":hover")`, `the pointer over ${element}`);
  await untilTransitionsEnd();
}

/** 用键盘方式聚焦宿主页面元素：先按一次 Shift，脚本聚焦随后显示焦点指示。Focuses a host element the keyboard way. */
async function focusVisibleHost(element: string): Promise<void> {
  host().focus();
  host().sendInputEvent({ type: "keyDown", keyCode: "Shift" });
  host().sendInputEvent({ type: "keyUp", keyCode: "Shift" });
  await evalHost(`${element}.focus(); true`);
  await untilHost(`${element}.matches(":focus-visible")`, `keyboard focus on ${element}`);
  await untilTransitionsEnd();
}

/** 显示左侧栏视图；Ribbon 项会切换视图，所以已显示时不点击。Shows a sidebar view; Ribbon items toggle, so a shown view is not clicked. */
async function showSidebarView(viewId: string, entryId = viewId): Promise<void> {
  const shown = `Boolean(document.querySelector('.agw-left-sidebar[data-view-id="${viewId}"]'))`;
  if (!(await evalHost<boolean>(shown)))
    await clickHost(`.agw-ribbon [data-entry-id="${entryId}"]`);
  await untilHost(shown, `the ${viewId} sidebar view`);
  await untilTransitionsEnd();
}

/** 等待宿主页面的 CSS 过渡结束，读取的是最终颜色。Waits for the host's CSS transitions to end so reads see final colors. */
async function untilTransitionsEnd(): Promise<void> {
  await untilHost(
    `document.getAnimations().every((animation) => !(animation instanceof CSSTransition) || animation.playState === "finished")`,
    "CSS transitions to end",
  );
}

async function openAppearancePage(): Promise<void> {
  await openSettingsPage("/settings/appearance/");
  await untilHost(`Boolean(${THEME_SELECT})`, "the Appearance page");
}

/** 在 Appearance 页面选择主题。Chooses a theme on the Appearance page. */
async function chooseTheme(themeId: string): Promise<void> {
  await openAppearancePage();
  await untilHost(
    `[...${THEME_SELECT}.options].some((option) => option.value === ${JSON.stringify(themeId)})`,
    `${themeId} in the Theme select`,
  );
  await chooseSize("appearance-theme", themeId)();
  await untilAppearanceApplied({ theme: themeId });
}

/** 在 Appearance 页面选择 Mode。Chooses a mode on the Appearance page. */
async function chooseMode(mode: "system" | "light" | "dark"): Promise<void> {
  await openAppearancePage();
  await clickHost(`[data-appearance-mode="${mode}"]`);
  await untilAppearanceApplied({ mode });
}

/** 宿主页面中 ThemeProvider 提供的值。The value ThemeProvider provides in the host page. */
const THEME_CONTEXT = `(() => {
  const start = document.querySelector(".agw-app-shell");
  let fiber = start[Object.keys(start).find((key) => key.startsWith("__reactFiber$"))];
  while (fiber && typeof fiber.memoizedProps?.value?.saveAppearance !== "function") fiber = fiber.return;
  if (!fiber) throw new Error("The theme context was not found.");
  return fiber.memoizedProps.value;
})()`;

/**
 * 等待 settings.json 与 ThemeProvider 都使用给出的外观，并且 `<html>` 上正好是它的主题变量与深浅色。
 * Waits until settings.json and ThemeProvider both use the given appearance and `<html>` carries exactly its theme
 * variables and light or dark class.
 */
async function untilAppearanceApplied(expected: Partial<AppearanceSettings>): Promise<void> {
  await untilAppearance(expected);
  await untilHost(
    `(() => {
      const theme = ${THEME_CONTEXT};
      const expected = ${JSON.stringify(expected)};
      const root = document.documentElement;
      const names = ${JSON.stringify([...THEME_TOKEN_KINDS.keys()])};
      const mode = expected.mode ?? theme.appearance?.mode;
      return Object.entries(expected).every(([name, value]) => theme.appearance?.[name] === value)
        && names.every((name) => root.style.getPropertyValue(name) === (theme.appliedTokens[name] ?? ""))
        && theme.kind !== null
        && root.classList.contains("dark") === (theme.kind === "dark")
        && (mode === "system" || theme.kind === mode);
    })()`,
    `the appearance ${JSON.stringify(expected)} to apply`,
  );
}

/**
 * 不离开当前页面修改外观：调用 ThemeProvider 提供的 saveAppearance，与 Appearance 页面保存时的路径相同。用于必须保持打开的
 * 视图（图片预览、对话框、extension 视图），Appearance 页面在另一个路由中。
 * Changes the appearance without leaving the page: calls the saveAppearance ThemeProvider provides, the same path the
 * Appearance page saves through. Used for views that must stay open (the image preview, dialogs, extension views),
 * since the Appearance page is another route.
 */
async function applyAppearanceInPlace(changes: Partial<AppearanceSettings>): Promise<void> {
  await evalHost(`(async () => {
    const theme = ${THEME_CONTEXT};
    await theme.saveAppearance({ ...theme.appearance, ...${JSON.stringify(changes)} });
    return true;
  })()`);
  await untilAppearanceApplied(changes);
}

/**
 * 主题选择：不合法的主题文件显示校验错误且不出现在下拉框中；Manage 只显示主题 extension；目录安装的主题 extension 启用后
 * 出现在下拉框中；Soft 与 Light 下宿主与侧栏的颜色、Edit 页面的变量；切换主题时没有过渡动画。
 * Theme selection: an invalid theme file shows its validation error and stays out of the select; Manage shows only
 * theme extensions; a theme extension installed from a folder appears once enabled; Soft with Light colors the host,
 * the sidebars and the Edit page variables; switching themes runs no transitions.
 */
async function themeSelection(): Promise<void> {
  // 首次启动时把 next-themes 的值（没有值时为 system）迁移到 settings.json。The first launch migrates next-themes' value.
  const migrated = await storedAppearance();
  assert.equal(migrated.theme, "default");
  assert.equal(migrated.mode, "system");
  pass("the first launch stores the Default theme and the migrated System mode");

  await goToExtensions();
  const badTheme = `document.querySelector('[data-extension-id="bad-theme"]')`;
  await untilHost(`Boolean(${badTheme})`, "the bad-theme card");
  assert.equal(await extensionStatus("bad-theme"), "Invalid");
  const badText = await evalHost<string>(`${badTheme}.textContent`);
  assert.ok(
    badText.includes("INVALID_THEME_VALUE (contributes.themes[0].light.--background)"),
    badText,
  );
  assert.ok(
    badText.includes("UNKNOWN_THEME_VARIABLE (contributes.themes[0].light.--page-background)"),
    badText,
  );
  assert.ok(badText.includes("Themes: Bad"), badText);
  await openAppearancePage();
  assert.equal(
    await evalHost<boolean>(
      `[...${THEME_SELECT}.options].some((option) => option.value.startsWith("bad-theme:"))`,
    ),
    false,
  );
  pass("an invalid theme file shows its validation error and the theme stays out of the select");

  await clickText("Manage");
  await untilHost(
    `new URLSearchParams(location.search).get("category") === "themes"`,
    "the theme extensions",
  );
  const listed = () =>
    evalHost<string[]>(
      `[...document.querySelectorAll("[data-extension-id]")].map((card) => card.dataset.extensionId)`,
    );
  await until(async () => (await listed()).length > 0, "theme extension cards");
  assert.deepEqual(await listed(), ["bad-theme", "soft-theme"]);
  assert.ok(
    (
      await evalHost<string>(
        `document.querySelector('[data-testid="extension-category"]').textContent`,
      )
    ).includes("Theme extensions"),
  );
  assert.ok(
    (
      await evalHost<string>(
        `document.querySelector('[data-extension-id="soft-theme"]').textContent`,
      )
    ).includes("Themes: Soft"),
  );
  await clickText("Show all", `document.querySelector('[data-testid="extension-category"]')`);
  await untilHost(`!new URLSearchParams(location.search).has("category")`, "every extension");
  await until(async () => (await listed()).includes("sample"), "the full extension list");
  pass(
    "Manage opens the Extensions page with only theme extensions, and Show all lists every extension",
  );

  await installFromFolder(join(config.fixtures, "contrast-theme"), "Contrast Themes");
  await untilStatus("contrast-theme", "Disabled");
  const approval = await enable("contrast-theme", "Contrast Themes");
  assert.ok(approval.includes("This extension requests no permissions."), approval);
  await untilStatus("contrast-theme", "Active");
  await openAppearancePage();
  assert.deepEqual(
    await evalHost<[string, string][]>(
      `[...${THEME_SELECT}.options].map((option) => [option.value, option.textContent])`,
    ),
    [
      ["default", "Default"],
      [CONTRAST_THEME, "Contrast"],
      [RING_THEME, "Ring only"],
      [SOFT_THEME, "Soft"],
    ],
  );
  pass("a theme extension installed from a folder lists its themes in the select once enabled");

  await chooseMode("light");
  // 推送给 extension 的变量：Default 下全部有值；Soft 没有设置的变量与 Default 相同。
  // Variables pushed to extensions: all have values under Default; those Soft leaves unset equal Default's.
  const background = await untilFrame("sample", "background");
  const pushed = () => api<Record<string, string>>(background, "return api.theme.get().tokens;");
  const defaultTokens = await pushed();
  assert.deepEqual(
    THEME_TOKEN_NAMES.filter((name) => !defaultTokens[name]),
    [],
    "pushed variables without a Default value",
  );
  await chooseTheme(SOFT_THEME);
  const softFile = await readJson<Record<"common" | "light", Record<string, string>>>(
    resolve("../packages/extensions/soft-theme/themes/soft.json"),
  );
  const softNames = new Set([...Object.keys(softFile.common), ...Object.keys(softFile.light)]);
  const derived = [
    "--radius-sm",
    "--radius-md",
    "--radius-lg",
    "--radius-xl",
    "--font-sans",
    "--font-mono",
  ];
  const softTokens = await until(async () => {
    const tokens = await pushed();
    return tokens["--foreground"] === "#0d0d0d" ? tokens : null;
  }, "the Soft variables in the background frame");
  assert.deepEqual(
    THEME_TOKEN_NAMES.filter(
      (name) =>
        !softNames.has(name) && !derived.includes(name) && softTokens[name] !== defaultTokens[name],
    ),
    [],
    "variables Soft leaves unset that differ from Default",
  );
  assert.equal(softTokens["--radius-lg"], "0.75rem");
  pass(
    "every pushed variable has a Default value, and the ones Soft leaves unset keep their Default values",
  );
  await untilHost(
    `getComputedStyle(document.body).backgroundColor === "rgb(255, 255, 255)" && getComputedStyle(document.querySelector(".agw-settings-nav")).backgroundColor === "rgb(249, 249, 249)"`,
    "the Soft host and settings navigation colors",
  );
  await goToChat();
  assert.equal(
    await hostStyle(`document.querySelector(".agw-ribbon")`, "background-color"),
    "rgb(249, 249, 249)",
  );
  assert.equal(
    await hostStyle(`document.querySelector(".agw-left-sidebar")`, "background-color"),
    "rgb(249, 249, 249)",
  );
  await openFileInTree("readme.md");
  await untilHost(selectedViewerIs(EDIT_VIEWER), "Edit for readme.md");
  const edit = await editShowing(EDITOR, "readme.md in Edit");
  await until(
    async () =>
      (await evalFrame<string>(
        edit,
        `getComputedStyle(document.documentElement).getPropertyValue("--background").trim()`,
      )) === "#ffffff",
    "the Soft background variable in Edit",
  );
  pass(
    "Soft with Light colors the host white, the Ribbon, Left Sidebar and Settings navigation #f9f9f9, and Edit gets --background #ffffff",
  );

  // 同一 Mode 下从 Default 切换到 Soft：下一帧没有 CSS 过渡，选中的设置项已经是 Soft 的颜色。
  // Default to Soft in the same mode: the next frame runs no CSS transition and the selected item already shows Soft.
  await chooseTheme("default");
  await untilHost(
    `!document.documentElement.style.getPropertyValue("--sidebar-accent")`,
    "Default",
  );
  const switched = await evalHost<{
    transitions: string[];
    background: string;
  }>(`new Promise((resolve) => {
    const root = document.documentElement;
    const observer = new MutationObserver(() => {
      if (root.style.getPropertyValue("--sidebar-accent") !== "#ececec") return;
      observer.disconnect();
      requestAnimationFrame(() => resolve({
        transitions: document.getAnimations().filter((animation) => animation instanceof CSSTransition).map((animation) => animation.transitionProperty),
        background: getComputedStyle(document.querySelector(".agw-settings-nav section > a.is-active")).backgroundColor,
      }));
    });
    observer.observe(root, { attributes: true, attributeFilter: ["style"] });
    const select = ${THEME_SELECT};
    Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(select, ${JSON.stringify(SOFT_THEME)});
    select.dispatchEvent(new Event("change", { bubbles: true }));
  })`);
  assert.deepEqual(switched, { transitions: [], background: "rgb(236, 236, 236)" });
  await untilAppearance({ theme: SOFT_THEME });
  pass(
    "switching from Default to Soft in Light runs no CSS transition and the selected navigation item shows Soft at once",
  );
}

/**
 * 侧栏变量：测试主题只把侧栏设为深色，主界面保持浅色；侧栏中共享组件的普通、悬停、选中与焦点状态和边框使用侧栏变量，
 * 主区域与 body 下的对话框使用界面变量。
 * Sidebar variables: the test theme darkens only the sidebars and the main interface stays light; shared components in
 * the sidebars use the sidebar variables for normal, hover, selected and focus states and borders, while the main area
 * and dialogs under body use the interface variables.
 */
async function sidebarThemes(): Promise<void> {
  await chooseMode("light");
  await chooseTheme(CONTRAST_THEME);
  const expected: Record<string, string> = {
    "--sidebar": "#17191d",
    "--sidebar-foreground": "#ffffff",
    "--agw-sidebar-muted-foreground": "#d1d5db",
    "--sidebar-accent": "#303030",
    "--sidebar-accent-foreground": "#ffffff",
    "--sidebar-border": "#525252",
    "--sidebar-ring": "#48aaff",
  };
  for (const [name, value] of Object.entries(expected)) {
    assert.equal(await hostVariable(name), value, name);
  }
  assert.equal(
    await evalHost<boolean>(`document.documentElement.classList.contains("dark")`),
    false,
  );
  assert.equal(await hostStyle("document.body", "background-color"), "rgb(255, 255, 255)");

  const sidebar = "rgb(23, 25, 29)";
  const white = "rgb(255, 255, 255)";
  const muted = "rgb(209, 213, 219)";
  const accent = "rgb(48, 48, 48)";
  const border = "rgb(82, 82, 82)";
  const focusRing = await hostColor("color-mix(in oklab, #48aaff 50%, transparent)");

  // 设置导航。The settings navigation.
  const nav = `document.querySelector(".agw-settings-nav")`;
  const navLink = (active: boolean) =>
    `[...document.querySelectorAll(".agw-settings-nav section > a")].find((link) => link.classList.contains("is-active") === ${active})`;
  assert.equal(await hostStyle(nav, "background-color"), sidebar);
  assert.equal(await hostStyle(nav, "border-right-color"), border);
  assert.equal(
    await hostStyle(`${nav}.querySelector(".agw-settings-nav-heading")`, "color"),
    white,
  );
  assert.equal(
    await hostStyle(`${nav}.querySelector("h2")`, "color"),
    await hostColor("color-mix(in oklab, #d1d5db 70%, transparent)"),
  );
  assert.equal(await hostStyle(navLink(true), "background-color"), accent);
  assert.equal(await hostStyle(navLink(true), "color"), white);
  assert.equal(await hostStyle(navLink(false), "color"), muted);
  await hoverHost(navLink(false));
  assert.equal(
    await hostStyle(navLink(false), "background-color"),
    await hostColor("color-mix(in oklab, #303030 70%, transparent)"),
  );
  assert.equal(await hostStyle(navLink(false), "color"), white);
  await focusVisibleHost(navLink(false));
  assert.equal(await hostStyle(navLink(false), "outline-color"), focusRing);

  // body 下的对话框与主区域使用界面变量。Dialogs under body and the main area use the interface variables.
  await goToExtensions();
  await clickText("Uninstall", `document.querySelector('[data-extension-id="contrast-theme"]')`);
  await untilHost(`Boolean(${OPEN_DIALOG})`, "the uninstall dialog");
  assert.equal(await hostStyle(OPEN_DIALOG, "background-color"), "rgb(255, 247, 237)");
  assert.equal(await hostStyle(OPEN_DIALOG, "color"), "rgb(23, 25, 29)");
  assert.equal(
    await hostStyle(`document.querySelector(".agw-settings-main")`, "color"),
    "rgb(23, 25, 29)",
  );
  await clickText("Cancel", OPEN_DIALOG);
  await untilHost(`!${OPEN_DIALOG}`, "the uninstall dialog to close");

  // Ribbon 与对话列表。The Ribbon and the conversation list.
  await goToChat();
  await showSidebarView("agw.conversations", "agw.ribbon.conversations");
  const ribbon = `document.querySelector(".agw-ribbon")`;
  const ribbonItem = (id: string) =>
    `document.querySelector('.agw-ribbon [data-entry-id="${id}"]')`;
  assert.equal(await hostStyle(ribbon, "background-color"), sidebar);
  assert.equal(await hostStyle(ribbon, "border-right-color"), border);
  assert.equal(
    await hostStyle(`${ribbon}.querySelector(".agw-ribbon-separator")`, "background-color"),
    border,
  );
  assert.equal(await hostStyle(ribbonItem("agw.ribbon.conversations"), "background-color"), accent);
  assert.equal(await hostStyle(ribbonItem("agw.ribbon.conversations"), "color"), white);
  assert.equal(await hostStyle(ribbonItem("agw.ribbon.files"), "color"), muted);
  await hoverHost(ribbonItem("agw.ribbon.files"));
  assert.equal(await hostStyle(ribbonItem("agw.ribbon.files"), "background-color"), accent);
  assert.equal(await hostStyle(ribbonItem("agw.ribbon.files"), "color"), white);
  await focusVisibleHost(ribbonItem("agw.ribbon.files"));
  assert.equal(await hostStyle(ribbonItem("agw.ribbon.files"), "outline-color"), focusRing);

  const leftSidebar = `document.querySelector(".agw-left-sidebar")`;
  assert.equal(await hostStyle(leftSidebar, "background-color"), sidebar);
  const title = `[...${leftSidebar}.querySelectorAll("h2")].find((heading) => heading.textContent === "Conversations")`;
  assert.equal(await hostStyle(title, "color"), white);
  await untilHost(`${CONVERSATION_ITEMS}.length >= 2`, "two conversations");
  const activeConversation = `${CONVERSATION_ITEMS}.find((item) => item.classList.contains("bg-accent"))`;
  const idleConversation = `${CONVERSATION_ITEMS}.find((item) => !item.classList.contains("bg-accent"))`;
  await evalHost(`${MARKDOWN_CONVERSATION}.click(); true`);
  await untilHost(
    `${MARKDOWN_CONVERSATION}?.classList.contains("bg-accent")`,
    "the open conversation in the list",
  );
  await untilTransitionsEnd();
  assert.equal(await hostStyle(activeConversation, "background-color"), accent);
  assert.equal(await hostStyle(activeConversation, "color"), white);
  assert.equal(await hostStyle(idleConversation, "color"), white);
  await hoverHost(idleConversation);
  assert.equal(
    await hostStyle(idleConversation, "background-color"),
    await hostColor("color-mix(in oklab, #303030 80%, transparent)"),
  );
  assert.equal(await hostStyle(idleConversation, "color"), white);
  assert.equal(
    await hostStyle(`document.querySelector(".agw-main-workspace")`, "color"),
    "rgb(23, 25, 29)",
  );

  // 文件树。The file tree.
  await showSidebarView("agw.files", "agw.ribbon.files");
  const fileRow = `${treeItem("readme.md")}?.closest(".cursor-pointer")`;
  await untilHost(`Boolean(${fileRow})`, "readme.md in the file tree");
  assert.equal(await hostStyle(fileRow, "color"), white);
  await hoverHost(fileRow);
  assert.equal(await hostStyle(fileRow, "background-color"), accent);
  assert.equal(await hostStyle(fileRow, "color"), white);
  const chevron = `${leftSidebar}.querySelector(".file-tree .lucide-chevron-right, .file-tree .lucide-chevron-down")`;
  await untilHost(`Boolean(${chevron})`, "a folder chevron");
  assert.equal(await hostStyle(chevron, "color"), muted);
  await showSidebarView("agw.conversations", "agw.ribbon.conversations");
  pass(
    "the sidebar variables color the Ribbon, Settings navigation, conversation list and file tree in every state while the main area and dialogs keep the interface colors",
  );
}

/** 128×96 的两色 PNG。A two-color 128×96 PNG. */
function testImageDataUrl(): string {
  const width = 128;
  const height = 96;
  const bitmap = Buffer.alloc(width * height * 4);
  for (let index = 0; index < width * height; index += 1) {
    const left = index % width < width / 2;
    // BGRA
    bitmap.set(left ? [200, 80, 20, 255] : [20, 160, 60, 255], index * 4);
  }
  return nativeImage.createFromBitmap(bitmap, { width, height }).toDataURL();
}

const IMAGE_REPLY = `[...document.querySelectorAll(".agw-main-workspace .msg-content")].find((element) => element.textContent.includes("Echo: Image check"))`;

/** 图片预览中受主题影响的颜色。The image preview colors themes affect. */
async function imagePreviewColors(): Promise<Record<string, string>> {
  const zoomIn = `document.querySelector('.agw-image-zoom button[aria-label="Zoom in"]')`;
  await hoverHost(zoomIn);
  await focusVisibleHost(zoomIn);
  return evalHost<Record<string, string>>(`(() => {
    const container = getComputedStyle(document.querySelector(".agw-image-preview .yarl__container"));
    const zoom = getComputedStyle(document.querySelector(".agw-image-zoom"));
    const button = getComputedStyle(${zoomIn});
    return {
      backdrop: container.backgroundColor,
      toolbar: zoom.backgroundColor,
      text: zoom.color,
      border: zoom.borderTopColor,
      shadow: zoom.boxShadow,
      hover: button.backgroundColor,
      focus: button.outlineColor,
    };
  })()`);
}

/** 状态指示器、运行图标、警告提示与 Server 光环的颜色；没有实际状态的项用相同 class 的探针。Status colors; probes reuse the classes. */
async function statusColors(): Promise<Record<string, string>> {
  return evalHost<Record<string, string>>(`(() => {
    const titlebar = document.querySelector(".agw-titlebar");
    const probes = document.createElement("div");
    probes.innerHTML = '<span class="agw-status-dot is-running"></span><span class="agw-status-dot is-waiting-approval"></span><span class="agw-status-dot is-completed-unread"></span><svg class="animate-spin text-(--agw-status-running)"></svg><div class="border border-amber-500/40 bg-amber-500/10"></div>';
    titlebar.append(probes);
    const style = (element) => getComputedStyle(element);
    const [running, waiting, completed, icon, warning] = probes.children;
    const online = document.querySelector(".agw-server-state.is-online");
    const result = {
      idle: style(document.querySelector(".agw-project-tab .agw-status-dot")).backgroundColor,
      running: style(running).backgroundColor,
      runningHalo: style(running).boxShadow,
      waiting: style(waiting).backgroundColor,
      completed: style(completed).backgroundColor,
      runningIcon: style(icon).color,
      warningBorder: style(warning).borderTopColor,
      warningBackground: style(warning).backgroundColor,
      online: style(online).backgroundColor,
      onlineHalo: style(online).boxShadow,
    };
    probes.remove();
    return result;
  })()`);
}

/**
 * 图片预览、状态指示器与聊天链接：打开预览后切换 Default、Soft 与测试主题，颜色立即更新，图片与缩放比例保持；只修改
 * --sidebar-ring 时运行图标颜色不变；回到 Default 后恢复默认值。
 * The image preview, status indicators and chat links: switching Default, Soft and the test theme with the preview open
 * updates its colors at once while the image and zoom stay; changing only --sidebar-ring keeps the running icon color;
 * Default restores the default values.
 */
async function imagePreviewThemes(): Promise<void> {
  await chooseMode("light");
  await chooseTheme("default");
  await goToChat();
  await openMainView("sample:app");
  const main = await untilFrame("sample", "mainView");
  const text = `Image check [docs](https://example.com/docs)\n\n![Pixel](${testImageDataUrl()})`;
  const conversationId = await api<string>(
    main,
    `
    const session = await api.execution.createSession({ projectId: ${JSON.stringify(config.projectId)} });
    const finished = new Promise((resolve) => {
      session.onMessage((message) => {
        if (message.additionalProperties?.type === "agw-turn-finished") resolve();
      });
    });
    await session.send({ target: { type: "agent", id: ${JSON.stringify(config.agentId)} }, text: ${JSON.stringify(text)} });
    await finished;
    await session.dispose();
    return session.conversationId;`,
  );
  await openMainView("agw.chat");
  const conversation = `${CONVERSATION_ITEMS}.find((item) => item.textContent.includes("Image check"))`;
  await untilHost(`Boolean(${conversation})`, "the image conversation in the list");
  await evalHost(`${conversation}.click(); true`);
  await untilHost(
    `new URLSearchParams(location.search).get("conversationId") === ${JSON.stringify(conversationId)}`,
    "the image conversation to open",
  );
  const link = `${IMAGE_REPLY}?.querySelector('.agw-prose a[href="https://example.com/docs"]')`;
  const image = `${IMAGE_REPLY}?.querySelector('img[aria-label="Preview image: Pixel"]')`;
  await untilHost(
    `Boolean(${link}) && ${image}?.complete && ${image}.naturalWidth === 128`,
    "the image reply",
  );

  await chooseTheme(SOFT_THEME);
  await goToChat();
  await untilHost(`Boolean(${link})`, "the image reply after Soft");
  assert.equal(await hostStyle(link, "color"), "rgb(1, 105, 204)");
  pass("chat links use the Soft link color rgb(1, 105, 204)");
  await chooseTheme("default");
  await goToChat();
  await untilHost(`Boolean(${image}?.complete)`, "the image reply after Default");
  const defaultStatus = await statusColors();

  await evalHost(`${image}.click(); true`);
  const preview = `document.querySelector('.agw-image-preview [aria-label="Zoom level"]')`;
  await untilHost(`Boolean(${preview})`, "the image preview");
  await clickHost('.agw-image-zoom button[aria-label="Zoom in"]');
  await untilHost(`${preview}.textContent !== "100%"`, "the zoomed image");
  const zoomLevel = await evalHost<string>(`${preview}.textContent`);
  const shownImage = `document.querySelector(".agw-image-preview .yarl__slide_image")`;
  const source = await evalHost<string>(`${shownImage}.currentSrc`);
  const defaultColors = await imagePreviewColors();
  assert.deepEqual(defaultColors, {
    backdrop: await hostColor("rgb(9 11 15 / 96%)"),
    toolbar: "rgb(32, 35, 41)",
    text: "rgb(245, 246, 247)",
    border: await hostColor("rgb(255 255 255 / 16%)"),
    shadow: `${await hostColor("rgb(0 0 0 / 24%)")} 0px 4px 24px 0px`,
    hover: await hostColor("rgb(255 255 255 / 12%)"),
    focus: "rgb(255, 255, 255)",
  });

  const unchangedPreview = async (label: string) => {
    assert.equal(
      await evalHost<string>(`${preview}.textContent`),
      zoomLevel,
      `${label} keeps the zoom`,
    );
    assert.equal(
      await evalHost<string>(`${shownImage}.currentSrc`),
      source,
      `${label} keeps the image`,
    );
  };
  await applyAppearanceInPlace({ theme: SOFT_THEME });
  await untilHost(
    `getComputedStyle(document.querySelector(".agw-image-zoom")).backgroundColor === "rgb(48, 48, 48)"`,
    "the Soft image toolbar",
  );
  assert.deepEqual(await imagePreviewColors(), {
    backdrop: await hostColor("rgb(13 13 13 / 96%)"),
    toolbar: "rgb(48, 48, 48)",
    text: "rgb(236, 236, 236)",
    border: "rgb(66, 66, 66)",
    shadow: `${await hostColor("rgb(0 0 0 / 24%)")} 0px 4px 24px 0px`,
    hover: "rgb(66, 66, 66)",
    focus: "rgb(72, 170, 255)",
  });
  await unchangedPreview("Soft");

  await applyAppearanceInPlace({ theme: CONTRAST_THEME });
  await untilHost(
    `getComputedStyle(document.querySelector(".agw-image-zoom")).backgroundColor === "rgb(17, 34, 51)"`,
    "the test theme image toolbar",
  );
  assert.deepEqual(await imagePreviewColors(), {
    backdrop: await hostColor("rgb(10 20 30 / 90%)"),
    toolbar: "rgb(17, 34, 51)",
    text: "rgb(250, 250, 250)",
    border: "rgb(68, 85, 102)",
    shadow: `${await hostColor("rgb(1 2 3 / 50%)")} 0px 4px 24px 0px`,
    hover: "rgb(51, 68, 85)",
    focus: "rgb(255, 136, 0)",
  });
  await unchangedPreview("the test theme");
  pass(
    "with the image preview open, Default, Soft and the test theme recolor its backdrop, toolbar, border, hover, focus and shadow at once while the image and zoom stay",
  );

  // 测试主题的色阶与状态变量；Tailwind 组合多个阴影，光环是最后一个。The test theme's palette and status variables;
  // Tailwind composes several shadows and the halo is the last one.
  const { runningHalo, onlineHalo, ...contrastStatus } = await statusColors();
  assert.deepEqual(contrastStatus, {
    idle: "rgb(17, 17, 17)",
    running: "rgb(68, 68, 68)",
    waiting: "rgb(34, 34, 34)",
    completed: "rgb(51, 51, 51)",
    runningIcon: "rgb(68, 68, 68)",
    warningBorder: await hostColor("color-mix(in oklab, #222222 40%, transparent)"),
    warningBackground: await hostColor("color-mix(in oklab, #222222 10%, transparent)"),
    online: "rgb(51, 51, 51)",
  });
  const halo = async (color: string) =>
    `${await hostColor(`color-mix(in srgb, ${color} 14%, transparent)`)} 0px 0px 0px 3px`;
  assert.ok(runningHalo.endsWith(await halo("#444444")), runningHalo);
  assert.ok(onlineHalo.endsWith(await halo("#555555")), onlineHalo);
  await applyAppearanceInPlace({ theme: RING_THEME });
  await until(
    async () => (await hostVariable("--sidebar-ring")) === "#ff00ff",
    "the ring-only theme",
  );
  const ringStatus = await statusColors();
  assert.equal(ringStatus.runningIcon, defaultStatus.runningIcon);
  assert.equal(ringStatus.running, defaultStatus.running);
  assert.equal(defaultStatus.runningIcon, "rgb(0, 111, 238)");
  await applyAppearanceInPlace({ theme: "default" });
  await until(async () => (await hostVariable("--sidebar-ring")) !== "#ff00ff", "Default");
  assert.deepEqual(await statusColors(), defaultStatus);
  assert.deepEqual(await imagePreviewColors(), defaultColors);
  await unchangedPreview("Default");
  pass(
    "the test theme recolors the status indicators, running icon, warning and Server halo; --sidebar-ring alone keeps the running icon; Default restores every default color",
  );
  await pressEscape();
  await untilHost(`!document.querySelector(".agw-image-preview")`, "the image preview to close");
}

/**
 * Memory 的 Markdown 预览：两种 Mode 下切换 Default、Soft 与测试主题，inline code 与 fenced code 的背景立即更新，
 * pre code 保持透明，内容与排版不变。
 * The Memory Markdown preview: switching Default, Soft and the test theme in both modes updates the inline and fenced
 * code backgrounds at once; pre code stays transparent and the content and typography stay.
 */
async function memoryPreviewThemes(): Promise<void> {
  await openSettingsPage("/user-memory/");
  await clickText("Add Memory");
  await untilHost(`Boolean(${OPEN_DIALOG})`, "the memory dialog");
  await setInput(
    'textarea[aria-label="Memory content"]',
    "Use `inline code` here.\n\n```\nfenced code\n```\n",
  );
  await evalHost(`(() => {
    const tab = [...${OPEN_DIALOG}.querySelectorAll('[role="tab"]')].find((element) => element.textContent === "Preview");
    tab.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, button: 0 }));
    return true;
  })()`);
  const article = `${OPEN_DIALOG}?.querySelector("article.prose")`;
  await untilHost(`Boolean(${article}?.querySelector("pre code"))`, "the memory preview");
  const read = () =>
    evalHost<Record<string, string>>(`(() => {
      const article = ${article};
      const inline = getComputedStyle(article.querySelector("p code"));
      const pre = getComputedStyle(article.querySelector("pre"));
      const preCode = getComputedStyle(article.querySelector("pre code"));
      return {
        inline: inline.backgroundColor,
        pre: pre.backgroundColor,
        preCode: preCode.backgroundColor,
        text: article.textContent,
        typography: [inline.fontSize, inline.padding, inline.borderRadius, pre.fontSize, pre.padding, pre.margin, pre.borderRadius].join(" "),
      };
    })()`);
  const initial = await read();
  const expect = async (mode: "light" | "dark", themeId: string, background: string) => {
    await applyAppearanceInPlace({ mode, theme: themeId });
    await untilHost(
      `document.documentElement.classList.contains("dark") === ${mode === "dark"}`,
      `the ${mode} mode`,
    );
    const color = await hostColor(background);
    await until(async () => (await read()).pre === color, `${themeId} ${mode} code background`);
    const values = await read();
    assert.deepEqual(
      values,
      {
        inline: color,
        pre: color,
        preCode: "rgba(0, 0, 0, 0)",
        text: initial.text,
        typography: initial.typography,
      },
      `${themeId} ${mode}`,
    );
  };
  for (const mode of ["light", "dark"] as const) {
    await expect(mode, "default", mode === "light" ? "oklch(0.95 0 0)" : "oklch(0.2 0 0)");
    await expect(mode, SOFT_THEME, mode === "light" ? "#f9f9f9" : "#181818");
    await expect(mode, CONTRAST_THEME, mode === "light" ? "#fde68a" : "#3f3f46");
  }
  await pressEscape();
  await untilHost(`!${OPEN_DIALOG}`, "the memory dialog to close");
  await applyAppearanceInPlace({ mode: "light", theme: "default" });
  pass(
    "in both modes Default, Soft and the test theme update the Memory preview's inline and fenced code backgrounds at once while pre code stays transparent and the content and typography stay",
  );
}

/**
 * 深色 Mode 与 extension 视图：Soft 深色的宿主背景；调用 theme.apply() 的视图在原 frame 中收到新值，不调用的页面没有主题
 * 变量且样式不变；标题栏按钮修改 Mode 后 Appearance 页面与 settings.json 同步。
 * Dark mode and extension views: Soft's dark host background; a view that calls theme.apply() receives the new values in
 * the same frame, a page that does not has no theme variables and keeps its style; the title bar button's mode change
 * reaches the Appearance page and settings.json.
 */
async function darkModeAndExtensionViews(): Promise<void> {
  await chooseMode("light");
  await chooseTheme(SOFT_THEME);
  await goToChat();
  await openFileInTree("readme.md");
  await untilHost(selectedViewerIs(EDIT_VIEWER), "Edit for readme.md");
  const edit = await editShowing(EDITOR, "readme.md in Edit");
  await evalFrame(edit, `window.__themeMarker = "kept"; true`);
  // sample 的侧栏视图不调用 theme.apply()，与 Edit 同时显示。Sample's sidebar view does not call theme.apply() and shows
  // next to Edit.
  await showSidebarView("sample:list");
  const sample = await untilFrame("sample", "sidebarView");
  const sampleStyle = () =>
    evalFrame<{ variables: string[]; background: string; color: string; font: string }>(
      sample,
      `(() => {
        const root = document.documentElement;
        const body = getComputedStyle(document.body);
        return {
          variables: [...root.style].filter((name) => name.startsWith("--")),
          background: body.backgroundColor,
          color: body.color,
          font: body.fontFamily,
        };
      })()`,
    );
  const sampleBefore = await sampleStyle();
  assert.deepEqual(sampleBefore.variables, []);

  await applyAppearanceInPlace({ mode: "dark" });
  await untilHost(
    `getComputedStyle(document.body).backgroundColor === "rgb(33, 33, 33)"`,
    "the Soft dark host background",
  );
  await until(
    async () =>
      (await evalFrame<string>(
        edit,
        `getComputedStyle(document.documentElement).getPropertyValue("--background").trim()`,
      )) === "#212121",
    "the dark Soft background in the open Edit frame",
  );
  assert.equal(viewerFrameOf("text-editor", "markdown")?.frameId, edit.frameId);
  assert.equal(await evalFrame<string>(edit, "window.__themeMarker"), "kept");
  assert.deepEqual(await sampleStyle(), sampleBefore);
  await showSidebarView("agw.conversations", "agw.ribbon.conversations");
  pass(
    "Soft with Dark colors the host rgb(33, 33, 33), the open Edit frame receives the new variables without being rebuilt, and a page without theme.apply() has no theme variables and keeps its style",
  );

  // 标题栏按钮在浅色与深色之间切换并保存 Mode。The title bar button toggles light and dark and saves the mode.
  await clickHost('[aria-label="Use light theme"]');
  await untilAppearance({ mode: "light" });
  await untilHost(`!document.documentElement.classList.contains("dark")`, "the light mode");
  await openAppearancePage();
  assert.equal(
    await evalHost<string | null>(
      `document.querySelector('[data-appearance-mode="light"]').getAttribute("aria-pressed")`,
    ),
    "true",
  );
  await clickHost('[aria-label="Use dark theme"]');
  await untilAppearance({ mode: "dark" });
  await untilHost(
    `document.querySelector('[data-appearance-mode="dark"]').getAttribute("aria-pressed") === "true"`,
    "Dark mode on the Appearance page",
  );
  pass("the title bar button saves the light and dark modes and the Appearance page follows");
  await chooseMode("light");
  await chooseTheme("default");
}

/** 包含五种边线的最小 Agentflow。The smallest Agentflow with all five edge kinds. */
function themeEdgesAgentflow(): Record<string, unknown> {
  const node = (nodeId: string, kind: number, relateId: string | null, x: number, y: number) => ({
    nodeId,
    kind,
    relateId,
    name: nodeId,
    positionJson: JSON.stringify({ x, y }),
    instructions: null,
    configJson: null,
  });
  const edge = (edgeId: string, sourceNodeId: string, targetNodeId: string, kind: number) => ({
    edgeId,
    sourceNodeId,
    targetNodeId,
    kind,
    label: null,
    conditionJson: kind === 3 ? JSON.stringify({ contains: "urgent" }) : null,
    configJson: kind === 3 ? JSON.stringify({ switchCaseOrder: 0 }) : null,
  });
  return {
    name: "Theme edges",
    description: "Desktop theme integration test graph",
    nodes: [
      node("input", 10, null, 0, 120),
      node("agent-a", 0, config.agentId, 320, 0),
      node("agent-b", 0, config.agentId, 640, 240),
      node("output", 9, null, 960, 120),
    ],
    edges: [
      edge("e-switch-case", "input", "agent-a", 3),
      edge("e-switch-default", "input", "agent-b", 4),
      edge("e-fan-out", "agent-a", "agent-b", 1),
      edge("e-fan-in", "agent-a", "output", 2),
      edge("e-direct", "agent-b", "output", 0),
    ],
  };
}

/** 构建器中每条边线与它箭头的颜色，以及图的状态。The color of every edge and its arrowhead, and the graph state. */
const AGENTFLOW_GRAPH = `(() => {
  const dialog = ${OPEN_DIALOG};
  const markers = [...dialog.querySelectorAll("marker")];
  const edges = Object.fromEntries([...dialog.querySelectorAll(".react-flow__edge")].map((edge) => {
    const path = edge.querySelector("path.react-flow__edge-path");
    const marker = markers.find((item) => path.getAttribute("marker-end") === "url('#" + item.id + "')");
    const arrow = getComputedStyle(marker.querySelector("polyline"));
    return [edge.dataset.testid, [getComputedStyle(path).stroke, arrow.stroke, arrow.fill]];
  }));
  const nodes = [...dialog.querySelectorAll(".react-flow__node")].map((node) => [node.dataset.id, node.style.transform, Boolean(node.querySelector(".border-primary"))]);
  const labels = [...dialog.querySelectorAll(".react-flow__edge-textwrapper")].map((label) => label.textContent);
  return { edges, nodes, labels, unsaved: dialog.textContent.includes("Unsaved changes") };
})()`;

/**
 * Agentflow 边线：构建器中五种边线与 SVG 箭头使用主题变量，切换主题时一起变化，节点、边线数据、选中状态与未保存修改保持。
 * Agentflow edges: the builder's five edge kinds and their SVG arrowheads use the theme variables and change together
 * with the theme, while nodes, edge data, the selection and unsaved changes stay.
 */
async function agentflowEdgeThemes(): Promise<void> {
  const created = await hostSend<{ id: string }>("POST", "/api/agentflows", themeEdgesAgentflow());
  await openSettingsPage("/agentflows/");
  const row = `[...document.querySelectorAll("tr")].find((item) => item.textContent.includes("Theme edges"))`;
  await untilHost(
    `Boolean(${row}?.querySelector('[title="Edit agentflow"]'))`,
    "the Theme edges row",
  );
  await evalHost(`${row}.querySelector('[title="Edit agentflow"]').click(); true`);
  await untilHost(
    `${OPEN_DIALOG}?.querySelectorAll(".react-flow__edge").length === 5 && ${OPEN_DIALOG}.querySelectorAll("marker polyline").length === 5`,
    "the five edges in the builder",
  );
  // 拖动 agent-b：选中它并产生未保存修改。Dragging agent-b selects it and makes an unsaved change.
  const node = `${OPEN_DIALOG}.querySelector('.react-flow__node[data-id="agent-b"]')`;
  const start = await evalHost<{ x: number; y: number }>(`(() => {
    const rect = ${node}.getBoundingClientRect();
    return { x: Math.round(rect.left + rect.width / 2), y: Math.round(rect.top + 12) };
  })()`);
  host().sendInputEvent({ type: "mouseMove", ...start });
  host().sendInputEvent({ type: "mouseDown", ...start, button: "left", clickCount: 1 });
  for (let step = 1; step <= 5; step += 1) {
    host().sendInputEvent({
      type: "mouseMove",
      x: start.x + step * 8,
      y: start.y + step * 6,
      modifiers: ["leftbuttondown"],
    });
  }
  host().sendInputEvent({
    type: "mouseUp",
    x: start.x + 40,
    y: start.y + 30,
    button: "left",
    clickCount: 1,
  });
  await untilHost(`${OPEN_DIALOG}.textContent.includes("Unsaved changes")`, "the moved agent-b");
  // 拖动不会选中节点，点击才会。A drag does not select a node; a click does.
  const end = { x: start.x + 40, y: start.y + 30 };
  host().sendInputEvent({ type: "mouseDown", ...end, button: "left", clickCount: 1 });
  host().sendInputEvent({ type: "mouseUp", ...end, button: "left", clickCount: 1 });
  await untilHost(`Boolean(${node}.querySelector(".border-primary"))`, "the selected agent-b");
  type Graph = {
    edges: Record<string, [string, string, string]>;
    nodes: [string, string, boolean][];
    labels: string[];
    unsaved: boolean;
  };
  const before = await evalHost<Graph>(AGENTFLOW_GRAPH);
  const colors = (values: Record<string, string>) =>
    Object.fromEntries(
      Object.entries(values).map(([id, color]) => [`rf__edge-${id}`, [color, color, color]]),
    );
  assert.deepEqual(
    before.edges,
    colors({
      "e-direct": "rgb(71, 85, 105)",
      "e-fan-out": "rgb(37, 99, 235)",
      "e-fan-in": "rgb(217, 119, 6)",
      "e-switch-case": "rgb(124, 58, 237)",
      "e-switch-default": "rgb(219, 39, 119)",
    }),
  );

  await applyAppearanceInPlace({ theme: CONTRAST_THEME });
  const contrast = colors({
    "e-direct": "rgb(1, 2, 3)",
    "e-fan-out": "rgb(4, 5, 6)",
    "e-fan-in": "rgb(7, 8, 9)",
    "e-switch-case": "rgb(10, 11, 12)",
    "e-switch-default": "rgb(13, 14, 15)",
  });
  await until(
    async () => isDeepStrictEqual((await evalHost<Graph>(AGENTFLOW_GRAPH)).edges, contrast),
    "the test theme edge colors",
  );
  const after = await evalHost<Graph>(AGENTFLOW_GRAPH);
  assert.deepEqual({ ...after, edges: before.edges }, before);
  await applyAppearanceInPlace({ theme: "default" });
  await until(
    async () => isDeepStrictEqual((await evalHost<Graph>(AGENTFLOW_GRAPH)).edges, before.edges),
    "the Default edge colors",
  );
  assert.deepEqual(await evalHost<Graph>(AGENTFLOW_GRAPH), before);
  pass(
    "the test theme recolors the five Agentflow edges and their arrowheads together while nodes, edge data, the selection and unsaved changes stay",
  );

  await pressEscape();
  await untilHost(
    `[...document.querySelectorAll('[role="alertdialog"]')].some((element) => element.textContent.includes("Discard unsaved changes?"))`,
    "the discard confirmation",
  );
  await clickText("Discard", `[...document.querySelectorAll('[role="alertdialog"]')].at(-1)`);
  await untilHost(`!${OPEN_DIALOG}`, "the builder to close");
  await hostSend("DELETE", `/api/agentflows/${created.id}`);
}

/**
 * Code 视图的字体：已打开的 Code 中切换到 Soft，同一个 frame 的编辑器换成 Soft 的等宽字体。
 * The Code view font: switching to Soft with Code open changes the editor in the same frame to Soft's monospace font.
 */
async function codeViewThemeFont(): Promise<void> {
  await goToChat();
  await openFileInTree("readme.md");
  await selectViewer(CODE_VIEWER);
  const code = await codeShowing(
    `document.querySelectorAll(".code-editor .view-line").length > 0`,
    "readme.md in Code",
  );
  const font = () =>
    evalFrame<string>(
      code,
      `getComputedStyle(document.querySelector(".code-editor .view-lines")).fontFamily`,
    );
  const defaultFont = await font();
  assert.ok(defaultFont.startsWith('"JetBrains Mono"'), defaultFont);
  await applyAppearanceInPlace({ theme: SOFT_THEME });
  await until(
    async () => (await font()).startsWith('ui-monospace, "SF Mono", Menlo, Consolas, monospace'),
    "Soft's monospace font in Code",
  );
  assert.equal(viewerFrameOf("text-editor", "code")?.frameId, code.frameId);
  await applyAppearanceInPlace({ theme: "default" });
  await until(async () => (await font()) === defaultFont, "the Default monospace font in Code");
  pass(
    "switching to Soft with Code open changes the editor font in the same frame, and Default restores it",
  );
}

/** Excalidraw 界面引用的宿主变量。The host variables the Excalidraw interface references. */
const EXCALIDRAW_VARIABLES: Record<string, string> = {
  "--island-bg-color": "--popover",
  "--popup-bg-color": "--popover",
  "--sidebar-bg-color": "--popover",
  "--default-bg-color": "--background",
  "--input-bg-color": "--background",
  "--color-surface-lowest": "--background",
  "--popup-secondary-bg-color": "--muted",
  "--color-surface-low": "--muted",
  "--color-surface-mid": "--muted",
  "--color-on-surface": "--foreground",
  "--text-primary-color": "--foreground",
  "--popup-text-color": "--foreground",
  "--icon-fill-color": "--foreground",
  "--input-label-color": "--muted-foreground",
  "--keybinding-color": "--muted-foreground",
  "--color-disabled": "--muted-foreground",
  "--default-border-color": "--border",
  "--dialog-border-color": "--border",
  "--sidebar-border-color": "--border",
  "--color-border-outline": "--border",
  "--color-border-outline-variant": "--border",
  "--input-border-color": "--input",
  "--button-hover-bg": "--accent",
  "--button-active-bg": "--accent",
  "--input-hover-bg-color": "--accent",
  "--color-surface-high": "--accent",
  "--color-primary-light": "--accent",
  "--color-primary-light-darker": "--accent",
  "--color-surface-primary-container": "--accent",
  "--color-on-primary-container": "--accent-foreground",
  "--color-primary": "--primary",
  "--color-primary-darker": "--primary",
  "--color-primary-darkest": "--primary",
  "--color-primary-hover": "--primary",
  "--color-brand-hover": "--primary",
  "--color-brand-active": "--primary",
  "--button-active-border": "--primary",
  "--popup-text-inverted-color": "--primary-foreground",
  "--focus-highlight-color": "--ring",
  "--select-highlight-color": "--ring",
  "--link-color": "--agw-prose-link",
};

/**
 * Excalidraw 界面的颜色：映射的变量与宿主变量不同的名称，以及工具栏、菜单、属性面板、选中与悬停的工具、Save 按钮和弹窗的
 * [实际, 期望] 颜色。
 * The Excalidraw interface colors: the mapped variables that differ from the host variables, and [actual, expected]
 * colors of the toolbar, menu, properties panel, selected and hovered tools, the Save button and dialogs.
 */
const EXCALIDRAW_COLORS = `(() => {
  const mapping = ${JSON.stringify(EXCALIDRAW_VARIABLES)};
  const host = getComputedStyle(document.documentElement);
  const token = (name) => host.getPropertyValue(name).trim();
  const probe = document.createElement("span");
  document.body.append(probe);
  const color = (value) => {
    probe.style.color = value;
    return getComputedStyle(probe).color;
  };
  const differing = (element) =>
    element ? Object.entries(mapping).filter(([name, source]) => getComputedStyle(element).getPropertyValue(name).trim() !== token(source)).map(([name]) => name) : null;
  const pair = (selector, property, source) => {
    const element = document.querySelector(selector);
    return element ? [getComputedStyle(element).getPropertyValue(property), color(token(source))] : [selector, "missing"];
  };
  const result = {
    root: differing(document.querySelector(".editor .excalidraw")),
    modal: differing(document.querySelector(".excalidraw.excalidraw-modal-container")),
    toolbar: pair(".Island.App-toolbar", "background-color", "--popover"),
    panel: pair(".App-menu__left .Island, .App-menu__left.Island", "background-color", "--popover"),
    selectedTool: pair(".ToolIcon_type_radio:checked + .ToolIcon__icon", "background-color", "--accent"),
    saveBorder: pair(".save-button", "border-top-color", "--border"),
    saveText: pair(".save-button", "color", "--foreground"),
  };
  const hovered = document.querySelector(".ToolIcon__icon:hover");
  if (hovered) result.hoveredTool = [getComputedStyle(hovered).backgroundColor, color(token("--accent"))];
  const menu = document.querySelector(".dropdown-menu .dropdown-menu-container");
  if (menu) result.menu = [getComputedStyle(menu).backgroundColor, color(token("--popover"))];
  const dialog = document.querySelector(".excalidraw-modal-container .Island");
  if (dialog) result.dialog = [getComputedStyle(dialog).backgroundColor, color(token("--popover"))];
  probe.remove();
  return result;
})()`;

/**
 * Excalidraw 的工具栏与菜单：同一 Mode 下切换 Default、Soft 与测试主题，两种 Mode 都检查；界面颜色引用宿主变量，frame、
 * 文档会话、画布与未保存修改保持。
 * Excalidraw's toolbar and menus: switching Default, Soft and the test theme in the same mode, in both modes; the
 * interface colors reference the host variables while the frame, the document session, the canvas and unsaved changes
 * stay.
 */
async function excalidrawThemes(): Promise<void> {
  await chooseMode("light");
  await goToChat();
  await openFileInTree("diagram.excalidraw");
  await untilHost(
    `${viewerTab(EXCALIDRAW_VIEWER)}?.getAttribute("aria-selected") === "true"`,
    "the Excalidraw tab",
  );
  const editor = await untilFrame("excalidraw", "fileViewer");
  await until(async () => (await evalFrame<number>(editor, DRAWN_PIXELS)) > 0, "the diagram");
  pressKey(editor, "r");
  const { width, height } = editor.view.getBounds();
  const start = { x: Math.round(width * 0.6), y: Math.round(height * 0.6) };
  drag(editor, start, { x: start.x + 90, y: start.y + 60 });
  await untilHost(UNSAVED_MARKER, "the unsaved marker");
  const drawn = await evalFrame<number>(editor, DRAWN_PIXELS);
  // 悬停一个工具按钮，打开主菜单。Hover a tool button and open the main menu.
  const toolPoint = await evalFrame<{ x: number; y: number }>(
    editor,
    `(() => { const rect = document.querySelector('[data-testid="toolbar-ellipse"]').closest("label").getBoundingClientRect(); return { x: Math.round(rect.left + rect.width / 2), y: Math.round(rect.top + rect.height / 2) }; })()`,
  );
  editor.view.webContents.sendInputEvent({ type: "mouseMove", ...toolPoint });
  await evalFrame(
    editor,
    `document.querySelector('[data-testid="main-menu-trigger"]').click(); true`,
  );
  await until(
    () =>
      evalFrame<boolean>(
        editor,
        `Boolean(document.querySelector(".dropdown-menu .dropdown-menu-container")) && Boolean(document.querySelector(".ToolIcon__icon:hover"))`,
      ),
    "the open main menu and a hovered tool",
  );

  type Colors = Record<string, string[] | null>;
  const check = async (label: string) => {
    const colors = await evalFrame<Colors>(editor, EXCALIDRAW_COLORS);
    const { root, modal, ...pairs } = colors;
    assert.deepEqual(root, [], `${label}: variables of the Excalidraw root`);
    if (modal !== null) assert.deepEqual(modal, [], `${label}: variables of the dialog container`);
    for (const [name, [actual, expected]] of Object.entries(pairs) as [string, string[]][]) {
      assert.equal(actual, expected, `${label}: ${name}`);
    }
    assert.equal(await evalFrame<number>(editor, DRAWN_PIXELS), drawn, `${label}: the canvas`);
    assert.equal(await evalHost<boolean>(UNSAVED_MARKER), true, `${label}: the unsaved marker`);
    assert.equal(
      frameOf("excalidraw", "fileViewer")?.frameId,
      editor.frameId,
      `${label}: the frame`,
    );
    return pairs;
  };
  // 各主题的 --accent，用来确认 frame 已经收到这个主题的变量。Each theme's --accent, to confirm the frame received it.
  const accents: Record<"light" | "dark", Record<string, string>> = {
    light: { default: "#f0f1f3", [SOFT_THEME]: "#ececec", [CONTRAST_THEME]: "#fed7aa" },
    dark: { default: "#202329", [SOFT_THEME]: "#2f2f2f", [CONTRAST_THEME]: "#44403c" },
  };
  const untilFrameTheme = async (mode: "light" | "dark", themeId: string) => {
    await until(
      async () => (await evalFrame<string>(editor, EXCALIDRAW_THEME)) === mode,
      `the ${mode} Excalidraw`,
    );
    await until(
      async () =>
        (await evalFrame<string>(
          editor,
          `getComputedStyle(document.documentElement).getPropertyValue("--accent").trim()`,
        )) === accents[mode][themeId],
      `${themeId} ${mode} variables in the Excalidraw frame`,
    );
  };
  for (const mode of ["light", "dark"] as const) {
    const seen: string[] = [];
    for (const themeId of ["default", SOFT_THEME, CONTRAST_THEME, "default"]) {
      await applyAppearanceInPlace({ mode, theme: themeId });
      await untilFrameTheme(mode, themeId);
      await until(async () => {
        const colors = await evalFrame<Colors>(editor, EXCALIDRAW_COLORS);
        return colors.root?.length === 0 && colors.toolbar?.[0] === colors.toolbar?.[1];
      }, `${themeId} ${mode} in Excalidraw`);
      const pairs = await check(`${themeId} ${mode} with the menu open`);
      assert.ok(
        pairs.menu && pairs.hoveredTool,
        `${themeId} ${mode}: the open menu and the hovered tool`,
      );
      seen.push(pairs.selectedTool![0]);
    }
    // Default、Soft 与测试主题的选中工具颜色各不相同，回到 Default 后恢复。The three themes differ and Default comes back.
    assert.equal(new Set(seen.slice(0, 3)).size, 3, `${mode}: ${seen.join(", ")}`);
    assert.equal(seen[3], seen[0]);
  }

  // 主菜单的 Help 打开弹窗，弹窗渲染在 body 下的容器中。Help in the main menu opens a dialog in a container under body.
  await evalFrame(editor, `document.querySelector('[data-testid="help-menu-item"]').click(); true`);
  await until(
    () =>
      evalFrame<boolean>(
        editor,
        `Boolean(document.querySelector(".excalidraw-modal-container .Island"))`,
      ),
    "the help dialog",
  );
  for (const themeId of [CONTRAST_THEME, "default"]) {
    await applyAppearanceInPlace({ mode: "light", theme: themeId });
    await untilFrameTheme("light", themeId);
    await until(async () => {
      const colors = await evalFrame<Colors>(editor, EXCALIDRAW_COLORS);
      return colors.modal?.length === 0 && colors.dialog?.[0] === colors.dialog?.[1];
    }, `${themeId} in the Excalidraw dialog`);
    await check(`${themeId} light with the help dialog open`);
  }
  pressKey(editor, "Escape");
  pressKey(editor, "s", [MOD]);
  await untilHost(`!${UNSAVED_MARKER}`, "the saved diagram");
  pass(
    "in both modes Default, Soft and the test theme recolor Excalidraw's toolbar, menu, properties panel, tools, Save button and dialogs through the host variables, while the frame, canvas and unsaved changes stay",
  );
}

/**
 * 主题不可用：停用 Soft 所属的 extension 或删除测试主题的 extension 后，主题恢复为 Default 并显示提示。
 * Unavailable themes: disabling Soft's extension or uninstalling the test themes' extension returns to Default with a
 * notice.
 */
async function themeExtensionRemoval(): Promise<void> {
  await chooseTheme(SOFT_THEME);
  await disable("soft-theme");
  await untilStatus("soft-theme", "Disabled");
  await untilAppearance({ theme: "default" });
  await untilHost(
    `[...document.querySelectorAll("[data-sonner-toast]")].some((toast) => toast.textContent.includes("Soft is no longer available. Using Default."))`,
    "the Soft notice",
  );
  await untilHost(
    `!document.documentElement.style.getPropertyValue("--sidebar")`,
    "the theme variables to be removed",
  );
  pass("disabling Soft's extension returns to Default with a notice and saves default");
  await enable("soft-theme", "Soft Theme");
  await untilStatus("soft-theme", "Active");

  await chooseTheme(CONTRAST_THEME);
  await goToExtensions();
  await clickText("Uninstall", `document.querySelector('[data-extension-id="contrast-theme"]')`);
  await clickText("Uninstall", OPEN_DIALOG);
  await untilHost(
    `!document.querySelector('[data-extension-id="contrast-theme"]')`,
    "the test themes to leave the list",
  );
  await untilAppearance({ theme: "default" });
  await untilHost(
    `[...document.querySelectorAll("[data-sonner-toast]")].some((toast) => toast.textContent.includes("Contrast is no longer available. Using Default."))`,
    "the Contrast notice",
  );
  await untilHost(
    `!document.documentElement.style.getPropertyValue("--sidebar")`,
    "the theme variables to be removed",
  );
  pass("uninstalling the test themes' extension returns to Default with a notice");
}

/** 为重新启动的检查保存 Soft 与 Dark。Saves Soft and Dark for the restart check. */
async function themeBeforeRestart(): Promise<void> {
  await chooseMode("dark");
  await chooseTheme(SOFT_THEME);
  await untilHost(
    `getComputedStyle(document.body).backgroundColor === "rgb(33, 33, 33)"`,
    "Soft dark",
  );
  pass("Soft and Dark are saved for the restart check");
}

const TEXT_EDITOR_SETTINGS_FILE = join(storage("text-editor"), "settings.json");
const TEXT_EDITOR_DEFAULTS = { schemaVersion: 1, documentFontSize: null, codeFontSize: null };
const DOCUMENT_SIZE_SELECT = "text-editor-document-font-size";
const CODE_SIZE_SELECT = "text-editor-code-font-size";

/** 在设置页 frame 中按文字或 aria-label 找按钮的表达式。Expression for a button in the settings page frame, by text or aria-label. */
function settingsButton(name: string): string {
  return `[...document.querySelectorAll("button")].find((button) => (button.getAttribute("aria-label") ?? button.textContent.trim()) === ${JSON.stringify(name)})`;
}

/** 打开 Extensions → Text Editor 的设置页并等待两个下拉框出现。Opens Extensions → Text Editor settings and waits for both selects. */
async function openTextEditorSettings(): Promise<FrameHandle> {
  await goToExtensions();
  await until(() => !frameOf("text-editor", "settings"), "the previous settings frame to close");
  await clickHost(`[data-extension-id="text-editor"] a[href^="/extensions/settings/"]`);
  await untilHost(`location.pathname.startsWith("/extensions/settings/")`, "the settings route");
  const frame = await untilFrame("text-editor", "settings");
  await until(
    () =>
      evalFrame<boolean>(
        frame,
        `Boolean(document.getElementById(${JSON.stringify(DOCUMENT_SIZE_SELECT)}) && document.getElementById(${JSON.stringify(CODE_SIZE_SELECT)}))`,
      ),
    "the Text Editor settings selects",
  );
  // 设置页只显示 extension 自己的页面，没有视图标题条。The settings page shows only the extension's page, with no
  // view title bar.
  assert.deepEqual(
    await evalHost<{ view: boolean; header: boolean }>(`(() => {
      const view = document.querySelector('[data-extension-view="settings:text-editor"]');
      return { view: Boolean(view), header: Boolean(view?.querySelector(".agw-extension-view-header")) };
    })()`),
    { view: true, header: false },
    "the settings page has no view header",
  );
  // 原生视图是矩形的，与圆角边框各边至少相隔 4px 才不会盖住圆角。The native view is rectangular; at least 4px from every
  // side of the rounded border keeps it off the corners.
  const insets = await evalHost<number[]>(`(() => {
    const view = document.querySelector('[data-extension-view="settings:text-editor"]');
    const slot = view.querySelector(".agw-extension-view-slot").getBoundingClientRect();
    const border = view.parentElement.getBoundingClientRect();
    return [slot.left - border.left, slot.top - border.top, border.right - slot.right, border.bottom - slot.bottom];
  })()`);
  assert.ok(
    insets.every((inset) => inset >= 4),
    `the settings view stays inside the rounded border: ${JSON.stringify(insets)}`,
  );
  return frame;
}

/** 在设置页把下拉框设为 value 并触发 change。Sets a select on the settings page to value and fires change. */
async function chooseInSettings(frame: FrameHandle, id: string, value: string): Promise<void> {
  await evalFrame(
    frame,
    `(() => {
      const select = document.getElementById(${JSON.stringify(id)});
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value").set.call(select, ${JSON.stringify(value)});
      select.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    })()`,
  );
}

async function untilTextEditorSettings(
  expected: { schemaVersion: number; documentFontSize: number | null; codeFontSize: number | null },
  label: string,
): Promise<void> {
  await until(
    async () => isDeepStrictEqual(await readJson<unknown>(TEXT_EDITOR_SETTINGS_FILE), expected),
    label,
    20_000,
    async () => readFile(TEXT_EDITOR_SETTINGS_FILE, "utf8"),
  );
}

/**
 * Text Editor 设置：设置页显示两项设置，选择写入 settings.json，新打开的 Edit、Preview 与 Code 使用选择的字号；Reset 恢复
 * 跟随；设置文件不合法时查看器显示错误，设置页只有 Reset all 可用；最后保存文档字号 20 与 Appearance {18, 15}，供重新启动后检查。
 * Text Editor settings: the settings page shows both settings, choices are written to settings.json, Edit, Preview and
 * Code opened afterwards use the chosen sizes, Reset returns to following, an invalid settings file makes the viewers
 * show the error and leaves only Reset all working on the settings page; document size 20 and Appearance
 * {18, 15} stay saved for the check after the restart.
 */
async function textEditorSettings(): Promise<void> {
  // 基础字号 16：跟随值为文档 16 px，代码 14 px。Base 16: following gives document 16 px and code 14 px.
  let page = await openTextEditorSettings();
  // 设置页的标签、下拉框与按钮不小于 14px。The labels, select and buttons of the settings page are 14px.
  assert.deepEqual(
    await evalFrame<string[]>(
      page,
      `(() => {
        const size = (element) => getComputedStyle(element).fontSize;
        const label = [...document.querySelectorAll("label")].find((item) => item.textContent === "Document font size");
        return [
          size(label),
          size(document.getElementById(${JSON.stringify(DOCUMENT_SIZE_SELECT)})),
          size(${settingsButton("Reset document font size")}),
          size(${settingsButton("Reset all")}),
        ];
      })()`,
    ),
    ["14px", "14px", "14px", "14px"],
    "the settings page controls compute 14px at the default appearance",
  );
  const state = await evalFrame<Record<string, string | string[]>>(
    page,
    `(() => {
      const select = (id) => document.getElementById(id);
      return {
        document: select(${JSON.stringify(DOCUMENT_SIZE_SELECT)}).value,
        documentText: select(${JSON.stringify(DOCUMENT_SIZE_SELECT)}).selectedOptions[0].textContent,
        code: select(${JSON.stringify(CODE_SIZE_SELECT)}).value,
        codeText: select(${JSON.stringify(CODE_SIZE_SELECT)}).selectedOptions[0].textContent,
        labels: [...document.querySelectorAll("label")].map((label) => label.textContent),
      };
    })()`,
  );
  assert.deepEqual(state, {
    document: "follow",
    documentText: "Follow base size (16 px)",
    code: "follow",
    codeText: "Follow document size (14 px)",
    labels: ["Document font size", "Code editor font size"],
  });
  assert.equal(
    existsSync(TEXT_EDITOR_SETTINGS_FILE),
    false,
    "no settings file before the first change",
  );
  pass(
    "the Text Editor settings page shows Document font size and Code editor font size following the base size",
  );

  // 文档字号 20、代码字号 15。Document size 20, code size 15.
  await chooseInSettings(page, DOCUMENT_SIZE_SELECT, "20");
  await untilTextEditorSettings(
    { schemaVersion: 1, documentFontSize: 20, codeFontSize: null },
    "document size 20 in settings.json",
  );
  await chooseInSettings(page, CODE_SIZE_SELECT, "15");
  await untilTextEditorSettings(
    { schemaVersion: 1, documentFontSize: 20, codeFontSize: 15 },
    "code size 15 in settings.json",
  );
  await backToTestProjectChat();
  let sizes = await openedViewSizes();
  assert.equal(sizes.edit, "20px");
  assert.equal(sizes.preview, "20px");
  assert.equal(sizes.code, "15px");
  pass(
    "document size 20 and code size 15 are saved and Edit, Preview and Code opened afterwards use them",
  );

  // 逐项 Reset 与 Reset all。Reset per row and Reset all.
  page = await openTextEditorSettings();
  await clickInFrame(page, settingsButton("Reset document font size"));
  await untilTextEditorSettings(
    { schemaVersion: 1, documentFontSize: null, codeFontSize: 15 },
    "Reset document font size in settings.json",
  );
  await clickInFrame(page, settingsButton("Reset code editor font size"));
  await untilTextEditorSettings(
    TEXT_EDITOR_DEFAULTS,
    "Reset code editor font size in settings.json",
  );
  sizes = await openedViewSizes();
  assert.equal(sizes.edit, "16px");
  assert.equal(sizes.preview, "16px");
  assert.equal(sizes.code, "14px");
  page = await openTextEditorSettings();
  await chooseInSettings(page, DOCUMENT_SIZE_SELECT, "24");
  await chooseInSettings(page, CODE_SIZE_SELECT, "14");
  await untilTextEditorSettings(
    { schemaVersion: 1, documentFontSize: 24, codeFontSize: 14 },
    "document size 24 and code size 14 in settings.json",
  );
  await clickInFrame(page, settingsButton("Reset all"));
  await untilTextEditorSettings(TEXT_EDITOR_DEFAULTS, "Reset all in settings.json");
  pass(
    "Reset document font size, Reset code editor font size and Reset all return Edit to 16px and Code to 14px",
  );

  // 设置文件不合法：Edit 显示错误，设置页只有 Reset all 可用。An invalid settings file: Edit shows the error and only Reset all works.
  await writeFile(TEXT_EDITOR_SETTINGS_FILE, "{ not json");
  await backToTestProjectChat();
  await openFileInTree("notes/typography.md");
  await selectViewer(EDIT_VIEWER);
  const edit = await untilViewerFrame("text-editor", "markdown");
  await until(
    () =>
      evalFrame<boolean>(
        edit,
        `document.body.textContent.includes("Text Editor settings are invalid:") && document.body.textContent.includes("Open Text Editor settings to reset them.")`,
      ),
    "the invalid settings error in Edit",
  );
  assert.equal(
    await evalFrame<boolean>(edit, `Boolean(document.querySelector(".ProseMirror"))`),
    false,
  );
  page = await openTextEditorSettings();
  await until(
    () =>
      evalFrame<boolean>(
        page,
        `document.body.textContent.includes("Text Editor settings are invalid:") && document.body.textContent.includes("Use Reset all to restore the defaults.")`,
      ),
    "the invalid settings error on the settings page",
  );
  assert.deepEqual(
    await evalFrame<string[]>(
      page,
      `[...document.querySelectorAll("select, button")].filter((element) => !element.disabled).map((element) => element.textContent.trim())`,
    ),
    ["Reset all"],
  );
  await clickInFrame(page, settingsButton("Reset all"));
  await untilTextEditorSettings(TEXT_EDITOR_DEFAULTS, "Reset all after the invalid file");
  await until(
    () =>
      evalFrame<boolean>(
        page,
        `!document.body.textContent.includes("Text Editor settings are invalid:") && !document.getElementById(${JSON.stringify(DOCUMENT_SIZE_SELECT)}).disabled`,
      ),
    "the settings page to recover",
  );
  sizes = await openedViewSizes();
  assert.equal(sizes.edit, "16px");
  pass(
    "an invalid settings file shows the error in Edit and leaves only Reset all on the settings page, which restores the defaults",
  );

  // 保存文档字号 20 与 Appearance {18, 15}，供重新启动后检查；文档字号与基础字号 18 不同。Saves document size 20 and
  // Appearance {18, 15} for the check after the restart; the document size differs from the base size 18.
  page = await openTextEditorSettings();
  await chooseInSettings(page, DOCUMENT_SIZE_SELECT, "20");
  await untilTextEditorSettings(
    { schemaVersion: 1, documentFontSize: 20, codeFontSize: null },
    "document size 20 in settings.json",
  );
  await saveAppearance(
    { ...DEFAULT_APPEARANCE, baseFontSize: 18 },
    chooseSize("appearance-base-font-size", "18"),
  );
  await saveAppearance(
    { baseFontSize: 18, chatFontSize: 15 },
    chooseSize("appearance-chat-font-size", "15"),
  );
  assert.equal(await chatParagraphSize(), "15px");
  pass("document size 20 and Appearance {18, 15} stay saved for the restart check");
}

async function editInteractions(): Promise<void> {
  const edit = await editShowing(`Boolean(${editBlock("Code line.", "p")})`, "edit.md in Edit");
  const codeBlock = editBlock("const x = 1;", "div.code-block");

  // 代码块的 Copy 在没有剪贴板权限时通过 execCommand("copy") 复制。Copy works through execCommand("copy") without a
  // clipboard permission.
  await clipboard.writeText("");
  await clickInFrame(edit, `${codeBlock}.querySelector(".code-block-copy")`);
  await until(async () => (await clipboard.readText()) === "const x = 1;", "the copied code");
  pass("the code block Copy button puts the code on the clipboard");

  // 语言选择框在 pointerdown 时得到全部选项，早于原生列表打开（原生列表在 mousedown 的默认行为中打开）。
  // The language select gets every option on pointerdown, before the native list opens on mousedown's default action.
  const options = await evalFrame<{ before: number; after: number; value: string }>(
    edit,
    `(() => {
      const select = ${codeBlock}.querySelector("select.code-block-language");
      const before = select.options.length;
      select.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, button: 0, pointerType: "mouse" }));
      return { before, after: select.options.length, value: select.value };
    })()`,
  );
  assert.equal(options.before, 2);
  assert.ok(options.after > 20, `the select lists every language: ${options.after}`);
  assert.equal(options.value, "typescript");
  pass("the code block language select gets its options on pointerdown");

  // Mod+点击链接只调用 ui.openExternal 或 workspace.openFile，不打开其他窗口。Mod+click on a link only calls
  // ui.openExternal or workspace.openFile and opens no other window.
  const windows = BrowserWindow.getAllWindows().length;
  const created = createdWebContents;
  const editUrl = edit.view.webContents.getURL();
  const link = (text: string) =>
    `[...document.querySelectorAll(".ProseMirror a")].find((element) => element.textContent === ${JSON.stringify(text)})`;
  await clickInFrame(edit, link("example"), [MOD]);
  await untilHost(
    `${OPEN_CONFIRMATION}?.getAttribute("data-host-confirmation") === "open-link"
      && ${OPEN_CONFIRMATION}.textContent.includes("https://example.com/related")
      && ${OPEN_CONFIRMATION}.textContent.includes("Text Editor")`,
    "the Open link dialog from Edit",
  );
  await clickText("Cancel", OPEN_CONFIRMATION);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the Open link dialog to close");
  await clickInFrame(edit, link("readme"), [MOD]);
  await untilHost(shownFileIs("readme.md"), "readme.md opened from the Edit link");
  assert.equal(BrowserWindow.getAllWindows().length, windows);
  assert.equal(createdWebContents, created);
  assert.equal(edit.view.webContents.getURL(), editUrl);
  pass(
    "Mod+click on links asks before opening the browser or opens the project file, and opens no extra window",
  );

  await openFileInTree("notes/edit.md");
  await untilHost(selectedViewerIs(EDIT_VIEWER), "the Edit tab");
  await editShowing(`Boolean(${editBlock("Related:", "p")})`, "edit.md in Edit again");

  // 格式工具条中的 Turn into 弹出菜单获得焦点时工具条保持显示。The formatting toolbar stays while its Turn into popup
  // has focus.
  await selectTextIn(edit, editBlock("Related:", "p"), "Related");
  const toolbar = `document.querySelector(".formatting-menu")`;
  const toolbarButton = (text: string) =>
    `[...(${toolbar}?.querySelectorAll("button") ?? [])].find((button) => button.textContent === ${JSON.stringify(text)})`;
  await clickInFrame(edit, toolbarButton("Turn into"));
  const popupItem = (text: string) =>
    `[...(${toolbar}?.querySelectorAll('[role="menuitem"]') ?? [])].find((item) => item.textContent === ${JSON.stringify(text)})`;
  await until(
    async () => (await centerOf(edit, popupItem("Heading 3")))?.hit,
    "the Turn into menu",
  );
  assert.equal((await centerOf(edit, toolbarButton("Bold")))?.hit, true);
  await clickInFrame(edit, popupItem("Heading 3"));
  await until(
    () => evalFrame<boolean>(edit, `Boolean(${editBlock("Related:", "h3")})`),
    "the paragraph to become Heading 3",
  );
  await untilHost(unsavedIn(EDIT_VIEWER), "the unsaved marker after Turn into");
  pass(
    "the formatting toolbar keeps focus with its Turn into popup and turns the block into Heading 3",
  );

  // 拖动手柄移动块与列表项。The drag handle moves blocks and list items.
  const order = (selector: string) =>
    evalFrame<string[]>(
      edit,
      `[...document.querySelectorAll(${JSON.stringify(selector)})].map((element) => element.textContent)`,
    );
  const blockHandle = await dragBlock(
    edit,
    editBlock("Setext heading", "h1"),
    editBlock("First paragraph", "p"),
  );
  await until(async () => {
    const blocks = await order(".ProseMirror > *");
    const moved = blocks.findIndex((text) => text === "Setext heading");
    return moved >= 0 && blocks[moved + 1]?.startsWith("First paragraph");
  }, "the heading to move above the first paragraph");
  const items = `.ProseMirror > ul > li`;
  const starItem = (text: string) =>
    `[...document.querySelectorAll(${JSON.stringify(items)})].find((item) => item.textContent === ${JSON.stringify(text)})`;
  const itemHandle = await dragBlock(edit, starItem("star item two"), starItem("star item one"));
  await until(async () => {
    const listed = await order(items);
    return (
      listed.indexOf("star item two") >= 0 &&
      listed.indexOf("star item two") < listed.indexOf("star item one")
    );
  }, "the list item to move up");
  // 列表项的手柄与顶层块的手柄在同一列，不随列表缩进移动。A list item's handle shares the top-level blocks' column.
  assert.ok(
    Math.abs(itemHandle.x - blockHandle.x) <= 1,
    `the list item's handle (x ${itemHandle.x}) is in the top-level column (x ${blockHandle.x})`,
  );
  pass("the drag handle moves a block and a list item, in one column left of the content");

  // `/` 菜单的 Image：地址输入框打开期间在它前面输入文字，确认后图片仍插入在输入 `/` 的 paragraph 中。
  // The `/` menu's Image: text typed before the place while the address input is open still leaves the image in the
  // paragraph where `/` was typed.
  await placeCaret(edit, editBlock("Code line.", "p"));
  pressKey(edit, "Enter");
  const lastBlock = `${EDITOR}.lastElementChild`;
  await until(
    () =>
      evalFrame<boolean>(
        edit,
        `${lastBlock}.tagName === "P" && ${lastBlock}.textContent === "" && ${lastBlock}.previousElementSibling.textContent === "Code line."`,
      ),
    "an empty paragraph after Code line.",
  );
  await typeIn(edit, "/");
  await clickInFrame(
    edit,
    `[...document.querySelectorAll('.slash-menu [role="option"]')].find((option) => option.textContent === "Image")`,
  );
  const addressInput = `document.querySelector('.image-insert input[aria-label="Image address"]')`;
  await until(
    () => evalFrame<boolean>(edit, `document.activeElement === ${addressInput}`),
    "the image address input",
  );
  await placeCaret(edit, editBlock("Code line.", "p"));
  await typeIn(edit, " Typed before the image.");
  await until(
    () =>
      evalFrame<boolean>(edit, `Boolean(${editBlock("Code line. Typed before the image.", "p")})`),
    "the text typed before the image",
  );
  await evalFrame(edit, `${addressInput}.focus(); true`);
  await typeIn(edit, "https://example.com/later.png");
  pressKey(edit, "Enter");
  const laterImage = `document.querySelector('.ProseMirror .edit-image .image-placeholder[title="https://example.com/later.png"]')`;
  await until(
    () => evalFrame<boolean>(edit, `!${addressInput} && Boolean(${laterImage})`),
    "the inserted image",
  );
  assert.deepEqual(
    await evalFrame(
      edit,
      `(() => {
        const block = ${laterImage}.closest(".ProseMirror > *");
        return { last: block === ${lastBlock}, tag: block.tagName, before: block.previousElementSibling.textContent };
      })()`,
    ),
    { last: true, tag: "P", before: "Code line. Typed before the image." },
  );
  pass(
    "the / menu's image address input follows text typed before it and inserts where / was typed",
  );

  // "Reload view" 在有未保存修改时先确认。"Reload view" asks first when there are unsaved changes.
  const reload = `.agw-main-workspace [aria-label="Reload view"]`;
  await clickHost(reload);
  await untilHost(
    `${DISCARD_DIALOG}?.textContent.includes("Discard unsaved changes?") && ${DISCARD_DIALOG}.textContent.includes("edit.md")`,
    "the discard dialog for Reload view",
  );
  await clickText("Cancel", DISCARD_DIALOG);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the discard dialog to close");
  assert.equal(viewerFrameOf("text-editor", "markdown")?.frameId, edit.frameId);
  assert.equal(await evalFrame<boolean>(edit, `Boolean(${editBlock("Related:", "h3")})`), true);
  assert.equal(await evalHost<boolean>(unsavedIn(EDIT_VIEWER)), true);
  await clickHost(reload);
  await untilHost(`Boolean(${DISCARD_DIALOG})`, "the discard dialog for Reload view");
  await clickText("Discard", DISCARD_DIALOG);
  const reloaded = await until(() => {
    const frame = viewerFrameOf("text-editor", "markdown");
    return frame && frame.frameId !== edit.frameId && runtime().registry.get(frame.frameId)?.pageId
      ? frame
      : null;
  }, "the reloaded Edit viewer");
  await until(
    () =>
      evalFrame<boolean>(
        reloaded,
        `${SAVED} && Boolean(${editBlock("Related:", "p")}) && !${editBlock("Related:", "h3")}`,
      ),
    "the file content after Reload view",
  );
  assert.equal(await evalHost<boolean>(unsavedIn(EDIT_VIEWER)), false);
  pass(
    "Reload view asks before discarding Edit changes: Cancel keeps them, Discard reloads the viewer",
  );
}

async function textEditorCode(): Promise<void> {
  // 1：Shift+Alt+F 用 Prettier 格式化，保存后磁盘内容与格式化结果相同。Shift+Alt+F formats with Prettier; saving writes
  // the formatted text.
  const appPath = projectPath("src/app.ts");
  await openFileInTree("src/app.ts");
  await untilHost(selectedViewerIs(CODE_VIEWER), "the Code tab for app.ts");
  // 上一步打开 Settings 时 view 关闭，Code 在这里新建；快捷键要等 Monaco 获得焦点且 Prettier 注册完成（Format 按钮可用）后再按下。Opening Settings in the previous
  // step closed the views, so Code is created here; the shortcut waits until Monaco has focus and Prettier is registered
  // (the Format button is enabled once it is).
  const code = await codeShowing(
    `${CODE_TEXT}.includes("function   add") && Boolean(document.querySelector(".code-editor .monaco-editor")?.contains(document.activeElement)) && [...document.querySelectorAll(".code-toolbar button")].some((button) => button.textContent === "Format" && !button.disabled)`,
    "app.ts in Code",
  );
  const formatted =
    "function add(a: number, b: number) {\n    return a + b;\n}\nconst total = add(1, 2);\n";
  pressKey(code, "F", ["shift", "alt"]);
  await until(
    async () => (await evalFrame<string>(code, CODE_TEXT)) === formatted,
    "the Prettier output",
    20_000,
    async () => JSON.stringify(await evalFrame<string>(code, CODE_TEXT)),
  );
  await untilHost(unsavedIn(CODE_VIEWER), "the unsaved marker after formatting");
  saveIn(code);
  await untilHost(`!${unsavedIn(CODE_VIEWER)}`, "the unsaved marker to clear");
  await untilFileText(appPath, formatted, "the formatted app.ts on disk");
  pass("Shift+Alt+F formats TypeScript with Prettier and Cmd/Ctrl+S writes the formatted text");

  // 语法高亮、worker 与 codicon 字体都从 extension 自身加载。Highlighting, workers and the codicon font load from the
  // extension itself.
  const assets = `(() => ({
    tokens: new Set([...document.querySelectorAll(".code-editor .view-line span span")].map((span) => span.className)).size,
    codicon: [...document.fonts].some((font) => font.family.replaceAll('"', "") === "codicon" && font.status === "loaded"),
  }))()`;
  await until(
    async () => {
      const loaded = await evalFrame<{ tokens: number; codicon: boolean }>(code, assets);
      return loaded.tokens >= 4 && loaded.codicon;
    },
    "highlighting and the codicon font",
    20_000,
    async () => JSON.stringify(await evalFrame(code, assets)),
  );
  const editorWorker = /^agw-extension:\/\/text-editor\/assets\/editor\.worker-[^/]+\.js$/u;
  await until(
    async () => (await workerUrls(code)).some((url) => editorWorker.test(url)),
    "the editor worker",
    20_000,
    async () => JSON.stringify(await workerUrls(code)),
  );

  // 2：带注释的 JSON 没有错误标记；无效内容出现标记，说明 JSON 语言服务在运行。Commented JSON has no error marks; an
  // invalid edit gets one, so the JSON language service runs.
  await openFileInTree("config.json");
  await untilHost(selectedViewerIs(CODE_VIEWER), "the Code tab for config.json");
  await codeShowing(`${CODE_TEXT}.includes('"port": 8080')`, "config.json in Code");
  await delay(2000);
  const squiggles = `document.querySelectorAll('.code-editor [class*="squiggly-"]').length`;
  assert.equal(await evalFrame<number>(code, squiggles), 0);
  moveCodeCursor(code, "start");
  await typeIn(code, "x");
  await until(() => evalFrame<number>(code, squiggles), "an error mark for invalid JSON");
  pressKey(code, "z", [MOD]);
  await until(async () => (await evalFrame<number>(code, squiggles)) === 0, "the mark to go away");
  await untilHost(`!${unsavedIn(CODE_VIEWER)}`, "undo to clear the unsaved marker");
  const jsonWorker = /^agw-extension:\/\/text-editor\/assets\/json\.worker-[^/]+\.js$/u;
  assert.ok((await workerUrls(code)).some((url) => jsonWorker.test(url)));
  pass("JSON with comments shows no error marks while the JSON worker validates edits");

  // 3：CRLF 与 UTF-8 BOM 按原样写回。CRLF and the UTF-8 BOM are written back.
  await openFileInTree("crlf.txt");
  await codeShowing(`${CODE_TEXT}.includes("first line")`, "crlf.txt in Code");
  const crlfStatus = await evalFrame<string>(code, CODE_STATUS);
  assert.ok(crlfStatus.includes("CRLF") && crlfStatus.includes("UTF-8 with BOM"), crlfStatus);
  moveCodeCursor(code, "end");
  await typeIn(code, "third line");
  saveIn(code);
  await untilFileBytes(
    projectPath("crlf.txt"),
    Buffer.concat([
      Buffer.from([0xef, 0xbb, 0xbf]),
      Buffer.from("first line\r\nsecond line\r\nthird line"),
    ]),
    "crlf.txt with its BOM and CRLF",
  );
  pass("crlf.txt shows CRLF and UTF-8 with BOM and keeps both when saved");

  // 4：UTF-16LE 与 GB18030 按原编码写回；GBK 不能编码的字符报错，按 UTF-8 保存成功。UTF-16LE and GB18030 are written
  // back in their encodings; a character GBK cannot encode fails, and saving as UTF-8 succeeds.
  await openFileInTree("utf16.txt");
  await codeShowing(`${CODE_TEXT}.includes("UTF-16 text: héllo wörld")`, "utf16.txt in Code");
  assert.ok((await evalFrame<string>(code, CODE_STATUS)).includes("UTF-16LE with BOM"));
  moveCodeCursor(code, "end");
  await typeIn(code, "third ✓");
  saveIn(code);
  await untilFileBytes(
    projectPath("utf16.txt"),
    Buffer.concat([
      Buffer.from([0xff, 0xfe]),
      Buffer.from("UTF-16 text: héllo wörld\nsecond line\nthird ✓", "utf16le"),
    ]),
    "utf16.txt in UTF-16LE with BOM",
  );
  const gbkPath = projectPath("gbk.txt");
  const chinese = iconv.decode(await readFile(gbkPath), "gb18030");
  await openFileInTree("gbk.txt");
  await codeShowing(
    `${CODE_TEXT}.includes(${JSON.stringify(chinese.split("\n")[0])})`,
    "gbk.txt in Code",
  );
  assert.ok((await evalFrame<string>(code, CODE_STATUS)).includes("GB18030"));
  moveCodeCursor(code, "end");
  await typeIn(code, "第三行");
  saveIn(code);
  await untilFileBytes(gbkPath, iconv.encode(`${chinese}第三行`, "gb18030"), "gbk.txt in GB18030");
  pass("UTF-16LE and GB18030 files show their encodings and are saved in them");

  const encodingButton = `document.querySelector('.code-status-bar .popup > button[aria-haspopup="menu"]')`;
  const menuItem = (text: string) =>
    `[...document.querySelectorAll('.code-status-bar [role="menuitem"]')].find((item) => item.textContent === ${JSON.stringify(text)})`;
  await until(
    async () => (await evalFrame<string>(code, `${encodingButton}?.textContent`)) === "GB18030",
    "the GB18030 encoding button",
  );
  await clickInFrame(code, encodingButton);
  await clickInFrame(code, menuItem("Reopen with Encoding…"));
  await clickInFrame(code, `document.querySelector('.code-status-bar [data-encoding="gbk"]')`);
  await until(
    async () => (await evalFrame<string>(code, `${encodingButton}?.textContent`)) === "GBK",
    "gbk.txt reopened as GBK",
  );
  moveCodeCursor(code, "end");
  await typeIn(code, "😀");
  await untilHost(unsavedIn(CODE_VIEWER), "the unsaved marker for the emoji");
  saveIn(code);
  await untilHost(
    `[...document.querySelectorAll('[data-sonner-toast]')].some((toast) => toast.textContent.includes("cannot be encoded as gbk"))`,
    "the unencodable error",
  );
  await closeToasts();
  assert.equal(await evalHost<boolean>(unsavedIn(CODE_VIEWER)), true);
  await untilFileBytes(gbkPath, iconv.encode(`${chinese}第三行`, "gb18030"), "gbk.txt unchanged");
  await clickInFrame(code, encodingButton);
  await clickInFrame(code, menuItem("Save with Encoding…"));
  await clickInFrame(
    code,
    `document.querySelector('.code-status-bar [data-encoding="utf-8"][data-bom="false"]')`,
  );
  await untilHost(
    `!${unsavedIn(CODE_VIEWER)}`,
    "the unsaved marker to clear after saving as UTF-8",
  );
  await untilFileBytes(gbkPath, Buffer.from(`${chinese}第三行😀`, "utf8"), "gbk.txt in UTF-8");
  assert.equal(await evalFrame<string>(code, `${encodingButton}?.textContent`), "UTF-8");
  pass(
    "a character GBK cannot encode fails to save and keeps the unsaved marker, and Save with Encoding UTF-8 succeeds",
  );

  // 5：二进制文件与不能识别编码的文件。A binary file and a file whose encoding is not detected.
  await openFileInTree("data.bin");
  await until(
    async () =>
      (await evalFrame<string>(code, DOCUMENT_ERROR)).includes(
        "This file is binary, so it can't be edited as text.",
      ),
    "the binary panel",
  );
  await openFileInTree("unknown.txt");
  await until(
    async () =>
      (await evalFrame<string>(code, DOCUMENT_ERROR)).includes(
        "The encoding of this file couldn't be detected.",
      ),
    "the unknown encoding panel",
  );
  await clickInFrame(
    code,
    `[...document.querySelectorAll(".document-error button")].find((button) => button.textContent === "Reopen with Encoding…")`,
  );
  await clickInFrame(
    code,
    `document.querySelector('.document-error [data-encoding="windows-1252"]')`,
  );
  await codeShowing(`${CODE_TEXT}.includes("Café au lait")`, "unknown.txt read as windows-1252");
  assert.ok((await evalFrame<string>(code, CODE_STATUS)).includes("WINDOWS-1252"));
  pass(
    "binary and undetected files show their panels, and Reopen with windows-1252 shows the text",
  );

  // Text Editor 页面没有 CSP 报错，Extensions 页面没有被拦截的请求。No CSP errors in Text Editor pages and no blocked
  // requests on the Extensions page.
  const cspErrors = consoleMessages.filter(
    (message) =>
      message.url.startsWith("agw-extension://text-editor/") &&
      /Content Security Policy/iu.test(message.message),
  );
  assert.deepEqual(cspErrors, []);
  await goToExtensions();
  await clickText("Details", `document.querySelector('[data-extension-id="text-editor"]')`);
  await untilHost(
    `Boolean(document.querySelector('[data-extension-id="text-editor"] [data-slot="collapsible-content"][data-state="open"]'))`,
    "the Text Editor details",
  );
  assert.equal(
    await evalHost<string | null>(
      `document.querySelector('[data-extension-id="text-editor"] [data-testid="blocked-requests"]')?.textContent ?? null`,
    ),
    null,
  );
  await goToChat();
  pass(
    "Code highlights with its own workers and codicon font, with no CSP errors or blocked requests",
  );
}

async function conflictsAndRefresh(): Promise<void> {
  const appPath = projectPath("src/app.ts");
  await openFileInTree("src/app.ts");
  await untilHost(selectedViewerIs(CODE_VIEWER), "the Code tab for app.ts");
  const code = await codeShowing(`${CODE_TEXT}.includes("function add(a: number")`, "app.ts");
  const saved = await readFile(appPath, "utf8");

  // 1-3：磁盘被修改后保存显示冲突对话框，Cancel 保留修改。Saving after the disk changed shows the conflict dialog;
  // Cancel keeps the changes.
  moveCodeCursor(code, "end");
  await typeIn(code, "// editor change");
  await untilHost(unsavedIn(CODE_VIEWER), "the unsaved marker");
  const changedOnDisk = `${saved}// changed on disk\n`;
  await writeFile(appPath, changedOnDisk);
  saveIn(code);
  await expectConflictDialog("Text Editor", "app.ts", code);
  await clickText("Cancel", OPEN_MESSAGE_BOX);
  await untilHost(`!${OPEN_CONFIRMATION}`, "the conflict dialog to close");
  await until(() => code.view.getVisible(), "Code to show again");
  assert.equal(await readFile(appPath, "utf8"), changedOnDisk);
  assert.equal(await evalHost<boolean>(unsavedIn(CODE_VIEWER)), true);
  pass(
    "saving a file changed on disk shows the Text Editor conflict dialog over a snapshot, and Cancel keeps the changes",
  );

  // 4：Overwrite 写入编辑器内容。Overwrite writes the editor content.
  saveIn(code);
  await expectConflictDialog("Text Editor", "app.ts", code);
  await clickText("Overwrite", OPEN_MESSAGE_BOX);
  await untilHost(`!${unsavedIn(CODE_VIEWER)}`, "the unsaved marker to clear");
  const overwritten = `${saved}// editor change`;
  await untilFileText(appPath, overwritten, "the overwritten app.ts");
  pass("Overwrite writes the editor content and clears the unsaved marker");

  // 在 Code 中保存后 Source 显示保存后的内容。After a Code save, Source shows the saved content.
  await clickHost('[data-file-viewer-tab="source"]');
  await untilHost(
    `document.querySelector('.agw-main-workspace')?.textContent.includes("// editor change")`,
    "the saved content in Source",
  );
  pass("Source shows the content saved in Code");
  await selectViewer(CODE_VIEWER);

  // 5：Reload 显示磁盘上的内容。Reload shows the content on disk.
  moveCodeCursor(code, "end");
  await typeIn(code, "\n// discarded");
  await untilHost(unsavedIn(CODE_VIEWER), "the unsaved marker");
  const reloadedText = `${overwritten}\n// reloaded from disk\n`;
  await writeFile(appPath, reloadedText);
  saveIn(code);
  await expectConflictDialog("Text Editor", "app.ts", code);
  await clickText("Reload", OPEN_MESSAGE_BOX);
  await untilHost(`!${unsavedIn(CODE_VIEWER)}`, "the unsaved marker to clear");
  await until(
    async () => (await evalFrame<string>(code, CODE_TEXT)) === reloadedText,
    "the reloaded content",
  );
  assert.equal(await readFile(appPath, "utf8"), reloadedText);
  pass("Reload shows the content on disk and clears the unsaved marker");

  // 7：没有修改时切到 Source 再切回，Code 显示磁盘上的新内容。Without changes, Source and back shows the new disk content.
  await clickHost('[data-file-viewer-tab="source"]');
  await until(() => !code.view.getVisible(), "Code to hide");
  const refreshed = `${reloadedText}// refreshed\n`;
  await writeFile(appPath, refreshed);
  await selectViewer(CODE_VIEWER);
  await until(
    async () => (await evalFrame<string>(code, CODE_TEXT)) === refreshed,
    "the refreshed content",
  );
  assert.equal(await evalHost<boolean>(unsavedIn(CODE_VIEWER)), false);
  pass("showing an unchanged Code viewer again reads the new content from disk");
}

/** 用 "Install from folder" 安装目录中的 extension。Installs the extension in a folder with "Install from folder". */
async function installFromFolder(directory: string, name: string): Promise<void> {
  await goToExtensions();
  await withPickedPath(directory, async () => {
    await clickText("Install from folder");
    await untilHost(
      `${OPEN_DIALOG}?.textContent.includes(${JSON.stringify(name)})`,
      "the install dialog",
    );
    await clickText("Install", OPEN_DIALOG);
  });
}

async function todoEditorFixture(): Promise<void> {
  await installFromFolder(join(config.fixtures, "todo-editor"), "Todo Editor");
  await untilHost(
    `Boolean(document.querySelector('[data-extension-id="todo-editor"]'))`,
    "todo-editor in the list",
  );
  const dialogText = await enable("todo-editor", "Todo Editor");
  assert.ok(dialogText.includes("fs.cloud.read") && dialogText.includes("fs.cloud.write"));
  await untilStatus("todo-editor", "Active");
  pass(
    "the todo-editor fixture installs from a folder and activates with fs.cloud.read and fs.cloud.write",
  );

  // 1：*.todo 默认由 fixture 的编辑器打开。*.todo opens in the fixture's editor by default.
  const listPath = projectPath("list.todo");
  const TODO_VIEWER = "todo-editor:editor";
  await openFileInTree("list.todo");
  await untilHost(selectedViewerIs(TODO_VIEWER), "the todo-editor tab");
  assert.deepEqual(await evalHost<string[]>(FILE_VIEWER_TABS), [
    TODO_VIEWER,
    CODE_VIEWER,
    "source",
  ]);
  const todo = await untilViewerFrame("todo-editor", "editor");
  const area = `document.querySelector("#todo")`;
  await until(
    () =>
      evalFrame<boolean>(
        todo,
        `${area}.value === "- buy milk\\n- write tests\\n" && document.querySelector("#status").textContent === "clean"`,
      ),
    "list.todo in the fixture editor",
  );
  pass("list.todo opens in todo-editor by default, before Code and Source, showing the file");

  // 2：输入出现 "●"，保存后消失并写入磁盘。Typing shows the marker; saving clears it and writes the file.
  const focusEnd = `(() => { const area = ${area}; area.focus(); area.setSelectionRange(area.value.length, area.value.length); return true; })()`;
  todo.view.webContents.focus();
  await evalFrame(todo, focusEnd);
  await typeIn(todo, "- ship it\n");
  await untilHost(unsavedIn(TODO_VIEWER), "the unsaved marker in todo-editor");
  saveIn(todo);
  await untilHost(`!${unsavedIn(TODO_VIEWER)}`, "the unsaved marker to clear");
  await untilFileText(listPath, "- buy milk\n- write tests\n- ship it\n", "the saved list.todo");
  pass("typing in todo-editor shows the unsaved marker, and Cmd/Ctrl+S saves the file");

  // 3：冲突对话框的标题为 fixture 名称，Reload 显示磁盘内容。The conflict dialog is titled with the fixture name;
  // Reload shows the disk content.
  await writeFile(listPath, "- changed on disk\n");
  await evalFrame(todo, focusEnd);
  await typeIn(todo, "- more\n");
  await untilHost(unsavedIn(TODO_VIEWER), "the unsaved marker in todo-editor");
  saveIn(todo);
  await expectConflictDialog("Todo Editor", "list.todo", todo);
  await clickText("Reload", OPEN_MESSAGE_BOX);
  await until(
    () => evalFrame<boolean>(todo, `${area}.value === "- changed on disk\\n"`),
    "the disk content in todo-editor",
  );
  await untilHost(`!${unsavedIn(TODO_VIEWER)}`, "the unsaved marker to clear");
  pass("todo-editor's conflict dialog is titled Todo Editor, and Reload shows the disk content");

  // 4：Outline 为 *.md 的可选查看器，排在 Text Editor 的查看器之后（option 查看器按 extension id 排序），列出标题。
  // Outline is an option viewer for *.md after Text Editor's viewers (option viewers sort by extension id) and lists
  // the headings.
  const OUTLINE_VIEWER = "todo-editor:outline";
  await openFileInTree("notes/edit.md");
  await untilHost(selectedViewerIs(EDIT_VIEWER), "the Edit tab");
  assert.deepEqual(await evalHost<string[]>(FILE_VIEWER_TABS), [
    EDIT_VIEWER,
    MARKDOWN_VIEWER,
    CODE_VIEWER,
    OUTLINE_VIEWER,
    "source",
  ]);
  await selectViewer(OUTLINE_VIEWER);
  const outline = await untilViewerFrame("todo-editor", "outline");
  const headings = await until(async () => {
    const listed = await evalFrame<string[]>(
      outline,
      `[...document.querySelectorAll("#outline li")].map((item) => item.dataset.depth + " " + item.textContent)`,
    );
    return listed.length > 0 ? listed : null;
  }, "the outline");
  assert.deepEqual(headings, ["1 Edit sample", "1 Setext heading", "2 New section"]);
  pass("todo-editor's Outline is an option viewer for Markdown and lists the document's headings");
}

/* ---------- 性能测试 Performance ---------- */

// 在每个新文档中最先执行：收到 view.onLoad 时记录时间，之后每一帧检查查看器是否可以输入，可以输入时强制完成布局并记录耗时。
// Runs first in every new document: records the time when view.onLoad arrives, then checks every frame whether the
// viewer accepts input, and once it does forces layout and records the duration.
function openProbe(ready: string): string {
  return `(() => {
    const addEventListener = MessagePort.prototype.addEventListener;
    MessagePort.prototype.addEventListener = function (type, listener, options) {
      if (type === "message" && !this.__agwOpenProbe) {
        this.__agwOpenProbe = true;
        addEventListener.call(this, "message", (event) => {
          const data = event.data;
          if (data?.kind !== "request" || data.method !== "view.onLoad" || window.__agwOpenStart !== undefined) return;
          window.__agwOpenStart = performance.now();
          const poll = () => {
            if (!(${ready})) {
              requestAnimationFrame(poll);
              return;
            }
            document.body.getBoundingClientRect();
            window.__agwOpenTime = performance.now() - window.__agwOpenStart;
          };
          requestAnimationFrame(poll);
        });
      }
      return addEventListener.call(this, type, listener, options);
    };
  })();`;
}

/**
 * 重新加载查看器页面，测量从 `onLoad` 到可以输入的时间（毫秒）。
 * Reloads the viewer page and measures the milliseconds from `onLoad` until it accepts input.
 */
async function measureOpen(frame: FrameHandle, ready: string): Promise<number> {
  const debuggerApi = frame.view.webContents.debugger;
  debuggerApi.attach("1.3");
  try {
    await debuggerApi.sendCommand("Page.enable");
    const { identifier } = (await debuggerApi.sendCommand("Page.addScriptToEvaluateOnNewDocument", {
      source: openProbe(ready),
    })) as { identifier: string };
    const previousPage = runtime().registry.get(frame.frameId)!.pageId;
    void evalFrame(frame, "location.reload()").catch(() => undefined);
    const time = await until(
      async () => {
        const page = runtime().registry.get(frame.frameId)?.pageId;
        if (!page || page === previousPage) return null;
        return evalFrame<number | null>(frame, "window.__agwOpenTime ?? null");
      },
      "the reloaded viewer to accept input",
      120_000,
      async () =>
        `page ${runtime().registry.get(frame.frameId)?.pageId} (before ${previousPage}); ${await evalFrame<string>(
          frame,
          `JSON.stringify({ start: window.__agwOpenStart ?? null, time: window.__agwOpenTime ?? null, status: document.querySelector(".save-status")?.textContent ?? null, hidden: document.querySelector(".edit-scroll")?.hidden ?? null, blocks: document.querySelector(".ProseMirror")?.childElementCount ?? null, url: location.href })`,
        )}`,
    );
    await debuggerApi.sendCommand("Page.removeScriptToEvaluateOnNewDocument", { identifier });
    return time;
  } finally {
    debuggerApi.detach();
  }
}

/**
 * 在元素末尾按一个键，测量从 keydown 到该元素的文字在 DOM 中更新、布局完成的时间（毫秒）。
 * Presses one key at the end of an element and measures the milliseconds from keydown until the element's text is
 * updated in the DOM and laid out.
 */
async function measureKeystroke(frame: FrameHandle, element: string): Promise<number> {
  await placeCaret(frame, element);
  await evalFrame(
    frame,
    `(() => {
      const target = () => ${element};
      const before = target().textContent.length;
      window.__agwKeystroke = new Promise((resolve) => {
        let start = null;
        window.addEventListener("keydown", () => { start = performance.now(); }, { capture: true, once: true });
        const timeout = setTimeout(() => resolve({ start, before, after: target().textContent.length, active: document.activeElement?.outerHTML.slice(0, 120) ?? null }), 10000);
        const poll = () => {
          if (start !== null && target().textContent.length > before) {
            document.body.getBoundingClientRect();
            clearTimeout(timeout);
            resolve(performance.now() - start);
            return;
          }
          requestAnimationFrame(poll);
        };
        requestAnimationFrame(poll);
      });
      return true;
    })()`,
  );
  pressKey(frame, "q");
  const result = await evalFrame<number | object>(frame, "window.__agwKeystroke");
  if (typeof result !== "number")
    throw new Error(`The keystroke was not shown: ${JSON.stringify(result)}`);
  return result;
}

/**
 * 按保存快捷键，测量从 keydown 到保存状态显示 "Saved" 的时间，以及其间最长的主线程任务（生成文本在一个任务中完成）。
 * Presses the save shortcut and measures the time from keydown until the save status shows "Saved", plus the longest
 * main-thread task meanwhile (text generation runs in one task).
 */
async function measureSave(
  frame: FrameHandle,
  viewerId: string,
): Promise<{ save: number; longestTask: number }> {
  // 保存前查看器与宿主都显示未保存。Before saving, the viewer and the host both show unsaved changes.
  await until(
    () =>
      evalFrame<boolean>(
        frame,
        `document.querySelector(".save-status")?.textContent === "Unsaved changes"`,
      ),
    "the Unsaved changes status",
  );
  await untilHost(unsavedIn(viewerId), "the unsaved marker before saving");
  await evalFrame(
    frame,
    `(() => {
      window.__agwSave = new Promise((resolve) => {
        const tasks = [];
        const observer = new PerformanceObserver((list) => tasks.push(...list.getEntries()));
        observer.observe({ type: "longtask" });
        let start = null;
        window.addEventListener("keydown", () => { start = performance.now(); }, { capture: true, once: true });
        setTimeout(() => resolve({ start, status: document.querySelector(".save-status")?.textContent ?? null }), 30000);
        const poll = () => {
          if (start !== null && ${SAVED}) {
            document.body.getBoundingClientRect();
            const end = performance.now();
            observer.disconnect();
            const durations = tasks.filter((task) => task.startTime >= start).map((task) => task.duration);
            resolve({ save: end - start, longestTask: Math.max(0, ...durations) });
            return;
          }
          requestAnimationFrame(poll);
        };
        requestAnimationFrame(poll);
      });
      return true;
    })()`,
  );
  saveIn(frame);
  const result = await evalFrame<{ save?: number; longestTask?: number }>(
    frame,
    "window.__agwSave",
  );
  if (result.save === undefined || result.longestTask === undefined) {
    throw new Error(`The save did not finish: ${JSON.stringify(result)}`);
  }
  return { save: result.save, longestTask: result.longestTask };
}

const EDIT_READY = `${SAVED} && !document.querySelector(".edit-scroll").hidden && document.querySelector(".ProseMirror")?.childElementCount > 0`;
const CODE_READY = `${SAVED} && !document.querySelector(".code-editor").hidden && document.querySelectorAll(".code-editor .view-line").length > 0 && Boolean(document.querySelector(".code-editor .monaco-editor")?.contains(document.activeElement))`;

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

async function performancePhase(): Promise<void> {
  assert.equal(
    await evalHost<string>(`getComputedStyle(document.documentElement).fontSize`),
    "16px",
  );
  const inputs = config.performance!;
  const results: Record<string, unknown>[] = [];
  const failures: string[] = [];
  const check = (label: string, value: number, limit: number) => {
    if (value > limit) failures.push(`${label}: ${round(value)} ms > ${limit} ms`);
  };

  for (const input of inputs.markdown) {
    await openFileInTree(input.path);
    await untilHost(selectedViewerIs(EDIT_VIEWER), `the Edit tab for ${input.path}`);
    const edit = await editShowing("true", input.path);
    const opens: number[] = [];
    for (let run = 0; run < 3; run += 1) opens.push(await measureOpen(edit, EDIT_READY));
    // 文档中间的 paragraph；表格组中 paragraph 都在单元格内。The paragraph in the middle of the document; in the tables
    // composition every paragraph is inside a cell.
    const paragraphs = input.kind === "tables" ? ".ProseMirror td" : ".ProseMirror > p";
    const count = await evalFrame<number>(
      edit,
      `document.querySelectorAll("${paragraphs}").length`,
    );
    const middle = `document.querySelectorAll("${paragraphs}")[${Math.floor(count / 2)}]`;
    // 保存后的第一次按键（未保存状态随之出现），以及已有未保存修改时的按键。The first keystroke after a save (the
    // unsaved state appears with it) and keystrokes while changes are already unsaved.
    const keystrokes: number[] = [];
    const unsavedKeystrokes: number[] = [];
    const saves: { save: number; longestTask: number }[] = [];
    for (let run = 0; run < 3; run += 1) {
      keystrokes.push(await measureKeystroke(edit, middle));
      await untilHost(unsavedIn(EDIT_VIEWER), "the unsaved marker after a keystroke");
      unsavedKeystrokes.push(await measureKeystroke(edit, middle));
      saves.push(await measureSave(edit, EDIT_VIEWER));
    }
    // 代码块中输入只作为参考（CodeBlockLowlight 每次重建全部装饰），没有指标。Typing in a code block is for
    // information only (CodeBlockLowlight rebuilds every decoration); no target applies.
    const codeKeystrokes: number[] = [];
    if (input.kind === "spaced" && input.kib === 631) {
      const blocks = await evalFrame<number>(
        edit,
        `document.querySelectorAll(".ProseMirror .code-block code").length`,
      );
      const block = `document.querySelectorAll(".ProseMirror .code-block code")[${Math.floor(blocks / 2)}]`;
      for (let run = 0; run < 3; run += 1) {
        codeKeystrokes.push(await measureKeystroke(edit, block));
        await measureSave(edit, EDIT_VIEWER);
      }
    }
    const result = {
      viewer: "Edit",
      file: input.path,
      kind: input.kind,
      kib: input.kib,
      open: round(Math.min(...opens)),
      keystroke: round(Math.min(...keystrokes)),
      unsavedKeystroke: round(Math.min(...unsavedKeystrokes)),
      save: round(Math.min(...saves.map((save) => save.save))),
      longestSaveTask: round(Math.min(...saves.map((save) => save.longestTask))),
      codeBlockKeystroke: codeKeystrokes.length > 0 ? round(Math.min(...codeKeystrokes)) : null,
      runs: {
        opens: opens.map(round),
        keystrokes: keystrokes.map(round),
        unsavedKeystrokes: unsavedKeystrokes.map(round),
        saves,
        codeKeystrokes: codeKeystrokes.map(round),
      },
    };
    results.push(result);
    console.log(`PERF ${JSON.stringify(result)}`);
    check(`${input.path} open`, result.open, input.kib === 158 ? 1500 : 4000);
    if (input.kib === 631) check(`${input.path} keystroke`, result.keystroke, 50);
    if (input.kib === 631) check(`${input.path} save`, result.save, 1000);
  }

  await openFileInTree(inputs.code);
  await untilHost(selectedViewerIs(CODE_VIEWER), `the Code tab for ${inputs.code}`);
  const code = await codeShowing("true", inputs.code);
  const opens: number[] = [];
  for (let run = 0; run < 3; run += 1) opens.push(await measureOpen(code, CODE_READY));
  // 打开后可以输入。The editor accepts input after opening.
  await typeIn(code, "x");
  await untilHost(unsavedIn(CODE_VIEWER), "typing in the 10 MiB file");
  const codeResult = {
    viewer: "Code",
    file: inputs.code,
    mib: 10,
    open: round(Math.min(...opens)),
    runs: { opens: opens.map(round) },
  };
  results.push(codeResult);
  console.log(`PERF ${JSON.stringify(codeResult)}`);
  check(`${inputs.code} open`, codeResult.open, 3000);

  await writeFile(config.results, `${JSON.stringify(results, null, 2)}\n`);
  assert.deepEqual(failures, []);
  pass("Edit and Code meet the performance targets (results in performance.json)");
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
  // 重新启动后主题与 Mode 保持。The theme and mode persist across the restart.
  const restoredAppearance = await storedAppearance();
  assert.equal(restoredAppearance.theme, SOFT_THEME);
  assert.equal(restoredAppearance.mode, "dark");
  await untilHost(
    `document.documentElement.classList.contains("dark") && getComputedStyle(document.body).backgroundColor === "rgb(33, 33, 33)"`,
    "Soft dark after the restart",
  );
  await openAppearancePage();
  assert.equal(await evalHost<string>(`${THEME_SELECT}.value`), SOFT_THEME);
  assert.equal(
    await evalHost<string | null>(
      `document.querySelector('[data-appearance-mode="dark"]').getAttribute("aria-pressed")`,
    ),
    "true",
  );
  await goToChat();
  pass("after restarting Desktop, Soft and Dark stay applied and selected");

  // 重新启动后保存的字号仍然生效，Reset all 之后恢复默认。The saved sizes still apply after the restart; Reset all restores the defaults.
  assert.equal(
    await evalHost<string>(`getComputedStyle(document.documentElement).fontSize`),
    "18px",
  );
  assert.equal(await chatParagraphSize(), "15px");
  await openSettingsPage("/settings/appearance/");
  await untilHost(`Boolean(document.getElementById("appearance-base-font-size"))`, "Appearance");
  assert.equal(await eyebrowSize(), "14px");
  await saveAppearance(RESET_APPEARANCE, () => clickText("Reset all"));
  pass("after restarting Desktop, the saved sizes {18, 15} apply and Reset all restores 16px");

  // Text Editor 设置在重新启动后保持：文档字号 20 与基础字号 16（重置后）和重启前的基础字号 18 都不同；最后恢复默认，
  // 供 performance 阶段使用。
  // The Text Editor settings persist across the restart: document size 20 differs from the base size 16 (after the
  // reset) and from the base size 18 saved before the restart; the defaults are restored at the end for the
  // performance phase.
  assert.deepEqual(await readJson<unknown>(TEXT_EDITOR_SETTINGS_FILE), {
    schemaVersion: 1,
    documentFontSize: 20,
    codeFontSize: null,
  });
  const restored = await openedViewSizes();
  assert.equal(restored.edit, "20px");
  assert.equal(restored.preview, "20px");
  assert.equal(restored.code, "17.5px");
  const page = await openTextEditorSettings();
  assert.equal(
    await evalFrame<string>(
      page,
      `document.getElementById(${JSON.stringify(DOCUMENT_SIZE_SELECT)}).value`,
    ),
    "20",
  );
  await clickInFrame(page, settingsButton("Reset all"));
  await untilTextEditorSettings(
    TEXT_EDITOR_DEFAULTS,
    "Reset all in settings.json after the restart",
  );
  const defaults = await openedViewSizes();
  assert.equal(defaults.edit, "16px");
  assert.equal(defaults.code, "14px");
  pass(
    "after restarting Desktop, the Text Editor document size 20 applies to Edit and Preview, and Reset all restores 16px and 14px",
  );

  await goToExtensions();
  await untilStatus("text-editor", "Active", 30_000);
  assert.equal(
    await evalHost<boolean>(`Boolean(document.querySelector('[data-extension-id="excalidraw"]'))`),
    false,
  );
  await openFileInTree("diagram.excalidraw");
  // 卸载的 Excalidraw 不再提供查看器，文件由匹配全部文件的 Code 打开，切换按钮依次为 Code、Source。
  // The uninstalled Excalidraw contributes no viewer; Code, which matches every file, opens it with the tabs Code, Source.
  await untilHost(
    `${viewerTab("text-editor:code")}?.getAttribute("aria-selected") === "true"`,
    "the Code tab",
  );
  assert.deepEqual(
    await evalHost<string[]>(
      `[...document.querySelectorAll('[data-file-viewer-tab]')].map((tab) => tab.getAttribute("data-file-viewer-tab"))`,
    ),
    ["text-editor:code", "source"],
  );
  pass(
    "after restarting Desktop, Text Editor activates and the uninstalled Excalidraw stays absent, leaving Code and Source",
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
    () => !existsSync(join(config.userData, "extensions", "packages", "sample")),
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
  if (config.phase === "performance") {
    await performancePhase();
    return;
  }
  await beforeEnabling();
  await textCodecBridge();
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
  await chatMarkdown();
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
  await textEditorPreview();
  await viewerOrderAndDefaults();
  await textEditorMarkdown();
  await editInteractions();
  await textEditorTypography();
  await dragHandleFirstLine();
  await hostScaling();
  await appearanceSettings();
  await themeSelection();
  await sidebarThemes();
  await imagePreviewThemes();
  await memoryPreviewThemes();
  await darkModeAndExtensionViews();
  await agentflowEdgeThemes();
  await textEditorSettings();
  await textEditorCode();
  await codeViewThemeFont();
  await conflictsAndRefresh();
  await excalidrawEditor();
  await excalidrawThemes();
  await todoEditorFixture();
  await viewReloads();
  await hostReloads();
  await rejectedNavigation();
  await safeMode();
  await uninstallBundled();
  await themeExtensionRemoval();
  await themeBeforeRestart();
}

void run().then(
  () => app.exit(0),
  (error: unknown) => {
    console.error(error);
    app.exit(1);
  },
);
