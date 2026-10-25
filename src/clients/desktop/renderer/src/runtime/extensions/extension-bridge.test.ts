import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test, { after } from "node:test";
import { pathToFileURL } from "node:url";

import iconv from "iconv-lite";
import { JSDOM } from "jsdom";

import {
  type AiMessage,
  apiDelete,
  apiGet,
  apiPut,
  configureApiRuntime,
  getApiRuntime,
} from "@agw/api";
import { ChatHttpClient } from "@agw/chat-runtime";
import {
  type ApiRequestRecord,
  type ApiRoutes,
  rawApiResponse,
  replaceExecutionHub,
  startApiServer,
} from "@agw/test-harness";
import {
  type BinaryDocumentAdapter,
  type ChatEvent,
  type ChatEventFilter,
  type ChatRequestEvent,
  type ChatResponseEvent,
  createExtensionApi,
  createExtensionError,
  type DocumentSession,
  type Eol,
  type EventRouter,
  type ExtensionApi,
  ExtensionError,
  type ExtensionFrameKind,
  type ExtensionManifest,
  type ExtensionPermission,
  type ExtensionWelcome,
  type HostContext,
  type PreviewImage,
  RpcEndpoint,
  type RpcPort,
  type TextDocument,
  type TextDocumentAdapter,
  type ThemeChangedNotification,
  type ThemeInfo,
} from "@agw/extension-sdk";
import { ExtensionAccessGrants } from "@desktop/main/extensions/extension-access-grants";
import { ExtensionFileSystem } from "@desktop/main/extensions/extension-file-system";
import { toExtensionIpcResult } from "@desktop/main/extensions/extension-ipc-result";
import { ReplacedFileRegistry } from "@desktop/main/extensions/replaced-file-registry";
import { decodeText, encodeText, listTextEncodings } from "@desktop/main/extensions/text-codec";
import { createScratchDirectory } from "@desktop/main/scratch-directory.test-helper";
import type { AgwDesktopBridge } from "@desktop/shared/contracts";
import { parseHostPermissions } from "@desktop/shared/extension-host-permissions";
import { A2AService } from "./a2a-service";
import { CloudAccessGrants, CloudFileSystem, formatCloudUri } from "./cloud-file-system";
import { ContextService, connectionKeyOf } from "./context-service";
import {
  ExtensionBridgeBroker,
  type ExtensionTextCodec,
  type HostTheme,
  type ImagePreviewRequest,
  MAX_PREVIEW_IMAGE_BYTES,
} from "./extension-bridge-broker";
import { ExtensionHost } from "./extension-host";
import {
  createMainProcess,
  sampleExtension,
  startExtensionHost,
  TEST_THEME_DEFAULTS,
} from "./extension-host.test-helper";
import { ExecutionService } from "./execution-service";
import { WorkspaceLayoutStore } from "./workspace-layout-store";
import { WorkspaceRegistry } from "./workspace-registry";

const PROJECT = "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e";

const ENGLISH = "Hello, world.\nThe quick brown fox jumps over the lazy dog.\n";
const CHINESE =
  "春眠不觉晓，处处闻啼鸟。山中何所有，岭上多白云。床前明月光，疑是地上霜。举头望明月，低头思故乡。\n" +
  "白日依山尽，黄河入海流。欲穷千里目，更上一层楼。中华人民共和国是一个历史悠久的国家，文化源远流长。\n";
const JAPANESE =
  "吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。何でも薄暗いじめじめした所でニャーニャー泣いていた事だけは記憶している。\n" +
  "吾輩はここで始めて人間というものを見た。しかもあとで聞くとそれは書生という人間中で一番獰悪な種族であったそうだ。\n";
const FRENCH =
  "Le café « déjà vu » coûte 3 € — c’est très « chic ». L’été dernier, nous étions à Noël près de la forêt.\n" +
  "Où est la bibliothèque ? Ça dépend… “Smart quotes” and naïve façade.\n";

const sha256 = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");
const crlf = (text: string) => text.replaceAll("\n", "\r\n");

function failure(status: number, code: number, title: string) {
  return rawApiResponse({
    status,
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ code, title }),
  });
}

const manifest: ExtensionManifest = {
  manifestVersion: 1,
  id: "sample",
  name: "Sample",
  version: "1.0.0",
};

/** 用与 SDK 相同的方式连接：等待 welcome 后创建 API。Connects the way the SDK does: create the API after welcome. */
async function connectSdk(port: MessagePort): Promise<{
  api: ExtensionApi;
  welcome: ExtensionWelcome;
  events: [string, unknown][];
  endpoint: RpcEndpoint;
}> {
  const router: EventRouter = new Map();
  const events: [string, unknown][] = [];
  const welcome = new Promise<ExtensionWelcome>((resolve) =>
    router.set("welcome", new Set([(data) => resolve(data as ExtensionWelcome)])),
  );
  const endpoint = new RpcEndpoint(port as unknown as RpcPort, {
    event: (name, data) => {
      events.push([name, data]);
      for (const listener of router.get(name) ?? []) listener(data);
    },
  });
  const welcomeData = await welcome;
  router.delete("welcome");
  return {
    api: createExtensionApi(endpoint, welcomeData, router, TEST_THEME_DEFAULTS),
    welcome: welcomeData,
    events,
    endpoint,
  };
}

type HarnessOptions = {
  kind?: ExtensionFrameKind;
  /** 用户对打开链接的回答。The user's answer to opening a link. */
  openLink?: boolean;
  openFile?(extensionId: string, uri: string): Promise<void>;
  /** 替换默认的 Server 路由。Replaces default Server routes. */
  routes?: ApiRoutes;
  /** 宿主报告 frame 正在关闭。The host reports the frame as closing. */
  frameClosing?: boolean;
  /** 连接时宿主的主题。The host theme at connect time. */
  theme?: HostTheme;
};

const DEFAULT_HOST_THEME: HostTheme = {
  theme: { id: "default", title: "Default", kind: "dark", tokens: { "--background": "#000" } },
  style: null,
};

async function createHarness(permissions: ExtensionPermission[], options: HarnessOptions = {}) {
  const linkRequests: string[] = [];
  const imagePreviews: ({ frameId: string; pageId: string } & ImagePreviewRequest)[] = [];
  const unsaved: [string, string, boolean][] = [];
  const saveStatuses: [string, string, string | null][] = [];
  const fileHeaderItems: [string, string, unknown][] = [];
  // 云端文件按 Project 主目录中的路径保存。Cloud files by their path in the Project's primary directory.
  const cloudFiles = new Map<string, Uint8Array>([["a.bin", new Uint8Array([7, 8, 9])]]);
  const server = await startApiServer({
    "GET /api/files/download": (request: ApiRequestRecord) => {
      const bytes = cloudFiles.get(request.query.get("path") ?? "");
      return bytes
        ? rawApiResponse({ headers: { "content-type": "application/octet-stream" }, body: bytes })
        : failure(404, 4040001, "File not found.");
    },
    "PUT /api/files/write": { name: "b.bin", path: "b.bin", type: "file" },
    ...options.routes,
  });
  configureApiRuntime({ baseUrl: server.baseUrl, token: "agw_secret" });
  // 本地文件经主进程的 ExtensionFileSystem 读写，存储目录不需要权限。
  // Local files go through the main-process ExtensionFileSystem; the storage directory needs no permission.
  const storage = await createScratchDirectory("extension-bridge");
  const localFiles = new ExtensionFileSystem(
    new ExtensionAccessGrants(),
    new ReplacedFileRegistry(),
  );
  const mainProcess: Partial<AgwDesktopBridge> = {
    extensionFileSystem: (caller, request) =>
      toExtensionIpcResult(() =>
        localFiles.execute(
          {
            extensionId: caller.extensionId,
            grantedPermissions: permissions,
            storageDirectory: storage.path,
          },
          request,
        ),
      ),
  };
  // 主进程的 text-codec，与 IPC handler 一样包成 ExtensionIpcResult。
  // The main-process text codec, wrapped in ExtensionIpcResult the way the IPC handlers wrap it.
  const codec: ExtensionTextCodec = {
    decodeText: (bytes, encoding) => toExtensionIpcResult(async () => decodeText(bytes, encoding)),
    encodeText: (text, encodeOptions) =>
      toExtensionIpcResult(async () => encodeText(text, encodeOptions)),
    listTextEncodings: async () => listTextEncodings(),
  };
  const registry = new WorkspaceRegistry({ views: [], entries: {} });
  const layout = new WorkspaceLayoutStore(registry);
  const context = new ContextService(
    { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    {
      sidebarVisible: true,
      activeSidebarViewId: "agw.conversations",
      activeMainViewId: "agw.chat",
    },
  );
  const identity = { profileId: "local", baseUrl: server.baseUrl, token: "agw_secret" };
  context.setConnection(identity, { id: "local", name: "Local", kind: "local" });
  const key = connectionKeyOf(identity);
  context.setUser(key, { id: "user-1", displayName: "Ada", loginProvider: null, isAdmin: true });
  context.setSelection(key, {
    project: { id: PROJECT, name: "Demo", description: null, directories: [] },
    conversation: { id: "conversation-1" },
    target: null,
  });
  // broker 与 ExecutionService 共用的 ChatHttpClient，跟随全局的 API 配置；`holdHttp` 之后发出的 GET 请求等到释放才返回。
  // The ChatHttpClient shared by the broker and ExecutionService follows the global API configuration; GET requests
  // sent after `holdHttp` return only once released.
  let httpHold: Promise<void> | null = null;
  const chat = new ChatHttpClient({
    api: {
      apiGet: (async (...args: Parameters<typeof apiGet>) => {
        const hold = httpHold;
        const value = await apiGet(...args);
        await hold;
        return value;
      }) as typeof apiGet,
      apiPut,
      apiDelete,
    },
    identity: () => {
      const { baseUrl, token } = getApiRuntime();
      return { baseUrl, token };
    },
    execution: () => {
      const { baseUrl, token } = getApiRuntime();
      return { baseUrl, token, attachmentStore: null };
    },
  });
  // 统计 broker 尚未取消的订阅。Counts the broker's subscriptions that are not cancelled yet.
  let activeChatSubscriptions = 0;
  const brokerChat: Pick<ChatHttpClient, "subscribe"> = {
    subscribe: (filter, listener) => {
      const unsubscribe = chat.subscribe(filter, listener);
      activeChatSubscriptions += 1;
      let active = true;
      return () => {
        if (active) activeChatSubscriptions -= 1;
        active = false;
        unsubscribe();
      };
    },
  };
  const execution = new ExecutionService({
    context,
    deliver: (owner, event, data) => broker.deliverExecution(owner, event, data),
    refreshConversations: () => undefined,
    chat,
  });
  const broker = new ExtensionBridgeBroker({
    bridge: () => mainProcess as AgwDesktopBridge,
    codec: () => codec,
    hostInstanceId: "host-1",
    context,
    layout,
    cloud: new CloudFileSystem(context, new CloudAccessGrants(), () => undefined),
    execution,
    a2a: new A2AService(context),
    chat: brokerChat,
    extension: () => ({
      manifest,
      permissions,
      hostPermissions: parseHostPermissions([]),
      storageUri: "file:///data/sample",
    }),
    theme: () => options.theme ?? DEFAULT_HOST_THEME,
    isFrameClosing: () => options.frameClosing === true,
    insertComposerText: () => undefined,
    notify: () => undefined,
    onPong: () => undefined,
    openFile: async (extensionId, uri) => {
      if (!options.openFile) throw new Error("openFile is not part of this test.");
      await options.openFile(extensionId, uri);
    },
    setUnsavedChanges: (frameId, pageId, value) => unsaved.push([frameId, pageId, value]),
    setSaveStatus: (frameId, pageId, status) => saveStatuses.push([frameId, pageId, status]),
    setFileHeaderItems: (frameId, pageId, items) => fileHeaderItems.push([frameId, pageId, items]),
    confirmOpenExternal: async (_extensionId, url) => {
      linkRequests.push(url);
      return options.openLink === true;
    },
    // 确认队列属于 ExtensionHost，在 extension-host.test.ts 中测试。The queue belongs to ExtensionHost and is tested there.
    showMessageBox: () => {
      throw new Error("The confirmation queue is not part of this test.");
    },
    // 预览的显示与关闭属于 ExtensionHost，这里只记录检查后的请求。Showing and closing previews belong to
    // ExtensionHost; this records the checked requests only.
    previewImage: async (owner, request) => {
      imagePreviews.push({ frameId: owner.frameId, pageId: owner.pageId, ...request });
    },
  });
  const channel = new MessageChannel();
  broker.connect(
    {
      extensionId: "sample",
      frameId: "frame-1",
      pageId: "page-1",
      kind: options.kind ?? "mainView",
      viewId: "app",
      params: { a: 1 },
    },
    channel.port1,
  );
  const sdk = await connectSdk(channel.port2);
  // 端口保持打开会让测试进程无法退出。Open ports keep the test process alive.
  after(async () => {
    broker.disconnect("frame-1");
    sdk.endpoint.close();
    await storage.dispose();
  });
  return {
    ...sdk,
    server,
    cloudFiles,
    storage: storage.path,
    context,
    identity,
    key,
    broker,
    chat,
    activeChatSubscriptions: () => activeChatSubscriptions,
    /** 之后发出的 GET 请求等到调用返回的函数才返回。Later GET requests return only once the returned function is called. */
    holdHttp: () => {
      let release!: () => void;
      httpHold = new Promise<void>((resolve) => (release = resolve));
      return () => {
        httpHold = null;
        release();
      };
    },
    linkRequests,
    imagePreviews,
    unsaved,
    saveStatuses,
    fileHeaderItems,
    uri: (path: string) =>
      formatCloudUri({ serverId: "local", projectId: PROJECT, directoryId: null, path }),
    localUri: (path: string) => pathToFileURL(join(storage.path, path)).toString(),
  };
}

test("requests, responses, events and cancel work across the port", async () => {
  const channel = new MessageChannel();
  let observedAbort = false;
  const host = new RpcEndpoint(channel.port1 as unknown as RpcPort, {
    request: async (method, params, signal) => {
      if (method === "echo") return params;
      return new Promise((_, reject) =>
        signal.addEventListener("abort", () => {
          observedAbort = true;
          reject(new Error("aborted"));
        }),
      );
    },
  });
  const received: unknown[] = [];
  const page = new RpcEndpoint(channel.port2 as unknown as RpcPort, {
    event: (name, data) => received.push([name, data]),
  });

  assert.deepEqual(await page.request("echo", { value: 1 }), { value: 1 });
  host.emit("hello", { from: "host" });
  const controller = new AbortController();
  const pending = page.request("wait", {}, { signal: controller.signal });
  await new Promise((resolve) => setTimeout(resolve, 10));
  controller.abort();
  await assert.rejects(pending, { code: "ABORT_ERR" });
  await new Promise((resolve) => setTimeout(resolve, 10));
  assert.equal(observedAbort, true);
  assert.deepEqual(received, [["hello", { from: "host" }]]);

  page.close();
  host.close();
});

test("Uint8Array contents are transferred in both directions", async () => {
  const { api, uri } = await createHarness(["fs.cloud.read", "fs.cloud.write"]);
  const data = await api.fs.readFile(uri("a.bin"));
  assert.ok(data instanceof Uint8Array);
  assert.deepEqual([...data], [7, 8, 9]);

  const outgoing = new Uint8Array([1, 2, 3]);
  await api.fs.writeFile(uri("b.bin"), outgoing);
  assert.equal(outgoing.byteLength, 0);
});

test("failures are rebuilt as ExtensionError and missing permissions are denied", async () => {
  const { api, uri } = await createHarness([]);
  const error = await api.fs.readFile(uri("a.bin")).catch((caught: unknown) => caught);
  assert.ok(error instanceof ExtensionError);
  assert.equal(error.name, "ExtensionError");
  assert.equal(error.code, "ERR_PERMISSION_DENIED");
  await assert.rejects(api.execution.createSession({ projectId: PROJECT }), {
    code: "ERR_PERMISSION_DENIED",
  });
  await assert.rejects(api.context.listProjects(), { code: "ERR_PERMISSION_DENIED" });
  await assert.rejects(api.chat.composer.insertText("hi"), { code: "ERR_PERMISSION_DENIED" });
  await assert.rejects(
    api.chat.observe({}, () => undefined),
    { code: "ERR_PERMISSION_DENIED" },
  );
});

test("the context snapshot and change events only carry permitted fields", async () => {
  const withoutPermissions = await createHarness([]);
  const context: HostContext = withoutPermissions.welcome.context;
  assert.equal("project" in context, false);
  assert.equal("user" in context, false);
  assert.deepEqual(context.extension, {
    id: "sample",
    version: "1.0.0",
    manifestVersion: 1,
    storageUri: "file:///data/sample",
  });
  assert.deepEqual(withoutPermissions.welcome.frame, {
    frameId: "frame-1",
    kind: "mainView",
    viewId: "app",
    params: { a: 1 },
  });
  assert.deepEqual(withoutPermissions.welcome.theme, {
    id: "default",
    title: "Default",
    kind: "dark",
    tokens: { "--background": "#000" },
  });
  assert.equal(withoutPermissions.welcome.themeStyleId, null);

  const withProject = await createHarness(["context.project", "context.user"]);
  assert.equal(withProject.api.context.get().project?.id, PROJECT);
  assert.equal(withProject.api.context.get().user?.displayName, "Ada");

  const restrictedChanges: HostContext[] = [];
  const projectChanges: HostContext[] = [];
  withoutPermissions.api.context.onDidChange((next) => restrictedChanges.push(next));
  withProject.api.context.onDidChange((next) => projectChanges.push(next));
  withoutPermissions.context.setSelection(withoutPermissions.key, {
    project: { id: "next", name: "Next", description: null, directories: [] },
    conversation: null,
    target: null,
  });
  withProject.context.setSelection(withProject.key, {
    project: { id: "next", name: "Next", description: null, directories: [] },
    conversation: null,
    target: null,
  });
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.equal(restrictedChanges.length, 1);
  assert.equal(projectChanges.length, 1);
  assert.equal("project" in restrictedChanges[0], false);
  assert.equal(projectChanges[0].project?.id, "next");
  assert.equal(withProject.api.context.get().project?.id, "next");
});

const CSS_THEME: HostTheme = {
  theme: {
    id: "css-theme:paper",
    title: "Paper",
    kind: "dark",
    tokens: { "--background": "#111" },
  },
  style: { styleId: "a".repeat(64), cssText: ":root.dark { --background: #111; }" },
};

for (const kind of ["mainView", "background"] as const) {
  test(`a ${kind} connection gets the complete theme snapshot and per-connection style operations`, async () => {
    const harness = await createHarness([], { kind, theme: CSS_THEME });
    assert.deepEqual(harness.welcome.theme, {
      ...CSS_THEME.theme,
      cssText: CSS_THEME.style!.cssText,
    });
    assert.equal(harness.welcome.themeStyleId, CSS_THEME.style!.styleId);
    assert.equal(harness.api.theme.get().cssText, CSS_THEME.style!.cssText);
    const received: ThemeInfo[] = [];
    harness.api.theme.onDidChange((theme) => received.push(theme));
    const operations = () =>
      harness.events
        .filter(([name]) => name === "theme.changed")
        .map(([, data]) => (data as ThemeChangedNotification).style);

    // Mode 变化、样式相同：unchanged，公开事件仍带完整文本。A mode change with the same style: unchanged, and the public
    // event still carries the complete text.
    const light = {
      ...CSS_THEME.theme,
      kind: "light" as const,
      tokens: { "--background": "#fff" },
    };
    harness.broker.broadcastTheme({ theme: light, style: CSS_THEME.style });
    await until(() => received.length === 1, "the unchanged push");
    assert.deepEqual(operations(), [{ operation: "unchanged", styleId: CSS_THEME.style!.styleId }]);
    assert.deepEqual(received[0], { ...light, cssText: CSS_THEME.style!.cssText });

    // 同一主题 id 的内容更新：replace。A content update under the same theme id: replace.
    const updated = { styleId: "b".repeat(64), cssText: ":root { --background: #222; }" };
    harness.broker.broadcastTheme({ theme: light, style: updated });
    await until(() => received.length === 2, "the replace push");
    assert.deepEqual(operations()[1], { operation: "replace", ...updated });
    assert.equal(harness.api.theme.get().cssText, updated.cssText);

    // 转入 JSON 或 Default：clear，之后再次推送为 unchanged(null)。Moving to JSON or Default: clear, then unchanged(null).
    const plain = { id: "default", title: "Default", kind: "light" as const, tokens: {} };
    harness.broker.broadcastTheme({ theme: plain, style: null });
    harness.broker.broadcastTheme({ theme: plain, style: null });
    await until(() => received.length === 4, "the clear pushes");
    assert.deepEqual(operations().slice(2), [
      { operation: "clear", styleId: null },
      { operation: "unchanged", styleId: null },
    ]);
    assert.deepEqual(received[3], plain);
    assert.equal("cssText" in harness.api.theme.get(), false);
  });
}

test("a reconnected page gets the current style again in its welcome", async () => {
  const first = await createHarness([], { theme: CSS_THEME });
  first.broker.broadcastTheme(DEFAULT_HOST_THEME);
  const second = await createHarness([], { theme: CSS_THEME });
  assert.equal(second.welcome.themeStyleId, CSS_THEME.style!.styleId);
  assert.equal(second.welcome.theme.cssText, CSS_THEME.style!.cssText);
});

test("only file viewers report unsaved changes", async () => {
  const view = await createHarness([]);
  await assert.rejects(view.api.workspace.setUnsavedChanges(true), {
    code: "ERR_UNSUPPORTED_OPERATION",
  });
  assert.deepEqual(view.unsaved, []);

  const viewer = await createHarness([], { kind: "fileViewer" });
  await viewer.api.workspace.setUnsavedChanges(true);
  await viewer.api.workspace.setUnsavedChanges(false);
  assert.deepEqual(viewer.unsaved, [
    ["frame-1", "page-1", true],
    ["frame-1", "page-1", false],
  ]);
});

test("only file viewers report a save status, and only the known statuses", async () => {
  const view = await createHarness([]);
  await assert.rejects(view.endpoint.request("documents.setSaveStatus", { status: "saved" }), {
    code: "ERR_UNSUPPORTED_OPERATION",
  });
  assert.deepEqual(view.saveStatuses, []);

  const viewer = await createHarness([], { kind: "fileViewer" });
  for (const status of ["saved", "unsaved", "saving", "changed-on-disk", null]) {
    await viewer.endpoint.request("documents.setSaveStatus", { status });
  }
  await assert.rejects(viewer.endpoint.request("documents.setSaveStatus", { status: "clean" }), {
    code: "ERR_INVALID_ARGUMENT",
  });
  assert.deepEqual(viewer.saveStatuses, [
    ["frame-1", "page-1", "saved"],
    ["frame-1", "page-1", "unsaved"],
    ["frame-1", "page-1", "saving"],
    ["frame-1", "page-1", "changed-on-disk"],
    ["frame-1", "page-1", null],
  ]);
});

test("only file viewers set file header items, and the host receives only the known fields", async () => {
  const view = await createHarness([]);
  await assert.rejects(view.api.workspace.setFileHeaderItems([]), {
    code: "ERR_UNSUPPORTED_OPERATION",
  });
  assert.deepEqual(view.fileHeaderItems, []);

  const viewer = await createHarness([], { kind: "fileViewer" });
  await viewer.endpoint.request("workspace.setFileHeaderItems", {
    items: [
      {
        id: "encoding",
        placement: "top",
        label: "UTF-8",
        icon: "Languages",
        menu: [
          {
            id: "reopen",
            label: "Reopen with Encoding",
            tooltip: "Read again",
            disabled: true,
            menu: [{ id: "reopen:gbk", label: "GBK" }],
          },
        ],
      },
      { id: "wrap", placement: "top", label: "Word wrap", disabled: false, checked: false },
      // 菜单项 id 只需在同一条目中唯一。Entry ids need to be unique within one item only.
      {
        id: "export",
        placement: "bottom",
        label: "Export",
        menu: [{ id: "reopen", label: "Again", checked: true }],
      },
    ],
  });
  await viewer.api.workspace.setFileHeaderItems([]);
  assert.deepEqual(viewer.fileHeaderItems, [
    [
      "frame-1",
      "page-1",
      [
        {
          id: "encoding",
          placement: "top",
          label: "UTF-8",
          menu: [
            {
              id: "reopen",
              label: "Reopen with Encoding",
              disabled: true,
              menu: [{ id: "reopen:gbk", label: "GBK" }],
            },
          ],
        },
        { id: "wrap", placement: "top", label: "Word wrap", disabled: false, checked: false },
        {
          id: "export",
          placement: "bottom",
          label: "Export",
          menu: [{ id: "reopen", label: "Again", checked: true }],
        },
      ],
    ],
    ["frame-1", "page-1", []],
  ]);
});

test("file header items outside the limits are rejected, and the limits themselves are accepted", async () => {
  const viewer = await createHarness([], { kind: "fileViewer" });
  const item = (id: string, fields: Record<string, unknown> = {}) => ({
    id,
    placement: "bottom",
    label: "Item",
    ...fields,
  });
  const entries = (count: number) =>
    Array.from({ length: count }, (_, index) => ({ id: `e${index}`, label: "Entry" }));
  const invalid: unknown[] = [
    null,
    "items",
    Array.from({ length: 17 }, (_, index) => item(`i${index}`)),
    [item("")],
    [item("x".repeat(65))],
    [item("a", { label: "" })],
    [item("a", { label: "x".repeat(81) })],
    [item("a"), item("a")],
    [item("a", { placement: "toolbar" })],
    [item("a", { disabled: "yes" })],
    [item("a", { checked: 1 })],
    // 勾选项不能带菜单。A checkbox item cannot hold a menu.
    [item("a", { checked: true, menu: entries(1) })],
    [item("a", { menu: [{ id: "b", label: "B", checked: false, menu: entries(1) }] })],
    [item("a", { menu: [] })],
    [item("a", { menu: entries(101) })],
    [item("a", { menu: ["entry"] })],
    // 第三层菜单与同一条目中重复的菜单项 id。A third menu level, and an entry id repeated within one item.
    [
      item("a", {
        menu: [{ id: "b", label: "B", menu: [{ id: "c", label: "C", menu: entries(1) }] }],
      }),
    ],
    [item("a", { menu: [{ id: "b", label: "B", menu: [{ id: "b", label: "Again" }] }] })],
    // 菜单项合计 600 个。600 menu entries in total.
    Array.from({ length: 6 }, (_, index) => item(`i${index}`, { menu: entries(100) })),
  ];
  for (const items of invalid) {
    await assert.rejects(
      viewer.endpoint.request("workspace.setFileHeaderItems", { items }),
      { code: "ERR_INVALID_ARGUMENT" },
      JSON.stringify(items)?.slice(0, 120),
    );
  }
  assert.deepEqual(viewer.fileHeaderItems, []);

  // 16 个条目、合计 500 个菜单项、64 个字符的 id 与 80 个字符的文字。16 items, 500 menu entries in total, 64-character
  // ids and 80-character labels.
  const atLimits = [
    ...Array.from({ length: 5 }, (_, index) => item(`i${index}`, { menu: entries(100) })),
    ...Array.from({ length: 11 }, (_, index) =>
      item(`j${index}`.padEnd(64, "x"), { label: "x".repeat(80) }),
    ),
  ];
  await viewer.endpoint.request("workspace.setFileHeaderItems", { items: atLimits });
  assert.deepEqual(viewer.fileHeaderItems, [["frame-1", "page-1", atLimits]]);
});

test("openFile passes the URI to the host and returns its rejection", async () => {
  const opened: [string, string][] = [];
  const { api, uri } = await createHarness([], {
    openFile: async (extensionId, target) => {
      opened.push([extensionId, target]);
      if (target.endsWith("cancel.md")) throw createExtensionError("ERR_CANCELED", "Canceled.");
    },
  });
  await api.workspace.openFile(uri("docs/notes.md"));
  await assert.rejects(api.workspace.openFile(uri("cancel.md")), { code: "ERR_CANCELED" });
  assert.deepEqual(opened, [
    ["sample", uri("docs/notes.md")],
    ["sample", uri("cancel.md")],
  ]);
});

test("openExternal accepts only HTTP(S) and asks the user about unapproved hosts", async () => {
  const declined = await createHarness([], { openLink: false });
  await assert.rejects(declined.api.ui.openExternal("mailto:someone@example.com"), {
    code: "ERR_INVALID_URL",
  });
  await assert.rejects(declined.api.ui.openExternal("not a url"), { code: "ERR_INVALID_URL" });
  await assert.rejects(declined.api.ui.openExternal("https://example.com/docs"), {
    code: "ERR_CANCELED",
  });
  assert.deepEqual(declined.linkRequests, ["https://example.com/docs"]);
});

test("previewImage checks the image, transfers its bytes to the host, and is for views only", async () => {
  const harness = await createHarness([]);
  const bytes = [...new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"/>')];
  const data = new Uint8Array(bytes);
  await harness.api.ui.previewImage({ data, type: "image/svg+xml", name: "diagram.svg" });
  assert.equal(data.byteLength, 0, "the ArrayBuffer was transferred");
  assert.deepEqual(
    harness.imagePreviews.map(({ data: received, ...rest }) => ({ ...rest, data: [...received] })),
    [
      {
        frameId: "frame-1",
        pageId: "page-1",
        type: "image/svg+xml",
        name: "diagram.svg",
        data: bytes,
      },
    ],
  );

  const invalid: [unknown, RegExp][] = [
    ["diagram.svg", /must be an object/],
    [{ data: "<svg/>", type: "image/svg+xml", name: "a.svg" }, /data must be/],
    [{ data: new Uint8Array(), type: "image/png", name: "a.png" }, /data must be/],
    [
      { data: new Uint8Array(MAX_PREVIEW_IMAGE_BYTES + 1), type: "image/png", name: "a.png" },
      /data must be/,
    ],
    [{ data: new Uint8Array([1]), type: "image/tiff", name: "a.tiff" }, /type must be one of/],
    [{ data: new Uint8Array([1]), type: "image/png", name: "" }, /name must have/],
    [{ data: new Uint8Array([1]), type: "image/png", name: "a".repeat(256) }, /name must have/],
  ];
  for (const [image, message] of invalid) {
    await assert.rejects(harness.api.ui.previewImage(image as PreviewImage), {
      code: "ERR_INVALID_ARGUMENT",
      message,
    });
  }
  assert.equal(harness.imagePreviews.length, 1);

  const background = await createHarness([], { kind: "background" });
  await assert.rejects(
    background.api.ui.previewImage({
      data: new Uint8Array([1]),
      type: "image/png",
      name: "a.png",
    }),
    { code: "ERR_UNSUPPORTED_OPERATION" },
  );
  assert.deepEqual(background.imagePreviews, []);
});

test("readTextDocument detects encodings, BOMs and line endings of cloud files", async () => {
  const { api, cloudFiles, uri } = await createHarness(["fs.cloud.read"]);
  const cases: Array<[string, Uint8Array, Omit<TextDocument, "sha256">]> = [
    [
      "utf-8.md",
      iconv.encode(`${ENGLISH}${CHINESE}`, "utf-8"),
      { text: `${ENGLISH}${CHINESE}`, encoding: "utf-8", bom: false, eol: "\n" },
    ],
    [
      "utf-8-bom.md",
      iconv.encode(crlf(CHINESE), "utf-8", { addBOM: true }),
      { text: crlf(CHINESE), encoding: "utf-8", bom: true, eol: "\r\n" },
    ],
    [
      "utf-16le-bom.txt",
      iconv.encode(ENGLISH, "utf-16le", { addBOM: true }),
      { text: ENGLISH, encoding: "utf-16le", bom: true, eol: "\n" },
    ],
    [
      "utf-16be-bom.txt",
      iconv.encode(CHINESE, "utf-16be", { addBOM: true }),
      { text: CHINESE, encoding: "utf-16be", bom: true, eol: "\n" },
    ],
    [
      "utf-16le.txt",
      iconv.encode(JAPANESE, "utf-16le"),
      { text: JAPANESE, encoding: "utf-16le", bom: false, eol: "\n" },
    ],
    [
      "utf-16be.txt",
      iconv.encode(JAPANESE, "utf-16be"),
      { text: JAPANESE, encoding: "utf-16be", bom: false, eol: "\n" },
    ],
    [
      "gb18030.txt",
      iconv.encode(CHINESE, "gb18030"),
      { text: CHINESE, encoding: "gb18030", bom: false, eol: "\n" },
    ],
    [
      "shift_jis.txt",
      iconv.encode(JAPANESE, "shift_jis"),
      { text: JAPANESE, encoding: "shift_jis", bom: false, eol: "\n" },
    ],
    [
      "windows-1252.txt",
      iconv.encode(FRENCH, "windows-1252"),
      { text: FRENCH, encoding: "windows-1252", bom: false, eol: "\n" },
    ],
    [
      "mostly-crlf.txt",
      iconv.encode("a\r\nb\r\nc\n", "utf-8"),
      { text: "a\r\nb\r\nc\n", encoding: "utf-8", bom: false, eol: "\r\n" },
    ],
    [
      "mostly-lf.txt",
      iconv.encode("a\r\nb\nc\n", "utf-8"),
      { text: "a\r\nb\nc\n", encoding: "utf-8", bom: false, eol: "\n" },
    ],
  ];
  for (const [name, bytes, expected] of cases) {
    cloudFiles.set(name, bytes);
    assert.deepEqual(
      await api.fs.readTextDocument(uri(name)),
      { ...expected, sha256: sha256(bytes) },
      name,
    );
  }
});

test("readTextDocument reports binary files, unknown encodings and encodings chosen by the caller", async () => {
  const { api, cloudFiles, uri } = await createHarness(["fs.cloud.read"]);
  const gb18030 = iconv.encode(CHINESE, "gb18030");
  cloudFiles.set(
    "binary.bin",
    Buffer.concat([iconv.encode("PK", "utf-8"), Buffer.from([3, 4, 0, 0, 20, 0, 8, 0])]),
  );
  cloudFiles.set(
    "unknown.txt",
    Buffer.concat([iconv.encode("abc", "utf-8"), Buffer.from([1, 0xc3])]),
  );
  cloudFiles.set("chinese.txt", gb18030);

  await assert.rejects(api.fs.readTextDocument(uri("binary.bin")), { code: "ERR_NOT_TEXT" });
  await assert.rejects(api.fs.readTextDocument(uri("unknown.txt")), {
    code: "ERR_UNKNOWN_ENCODING",
  });
  assert.deepEqual(await api.fs.readTextDocument(uri("chinese.txt"), { encoding: "gbk" }), {
    text: CHINESE,
    encoding: "gbk",
    bom: false,
    eol: "\n",
    sha256: sha256(gb18030),
  });
  await assert.rejects(api.fs.readTextDocument(uri("chinese.txt"), { encoding: "utf-8" }), {
    code: "ERR_UNKNOWN_ENCODING",
  });
  await assert.rejects(api.fs.readTextDocument(uri("chinese.txt"), { encoding: "utf-32le" }), {
    code: "ERR_UNSUPPORTED_ENCODING",
  });
  await assert.rejects(api.fs.readTextDocument(uri("missing.txt")), { code: "ENOENT" });
  const aborted = new AbortController();
  aborted.abort();
  await assert.rejects(api.fs.readTextDocument(uri("chinese.txt"), { signal: aborted.signal }), {
    code: "ABORT_ERR",
  });

  const denied = await createHarness([]);
  await assert.rejects(denied.api.fs.readTextDocument(denied.uri("a.bin")), {
    code: "ERR_PERMISSION_DENIED",
  });
});

test("writeTextDocument encodes on the host and writes cloud files with the precondition", async () => {
  const { api, cloudFiles, server, uri } = await createHarness(["fs.cloud.read", "fs.cloud.write"]);
  const lastWrite = () =>
    server.requests.filter((request) => request.path === "/api/files/write").at(-1)!;
  // 不修改内容时按读取的编码写回，内容哈希与读取时相同。Written back unchanged, the content hash equals the one read.
  const files: Array<[string, Uint8Array]> = [
    ["utf-8-bom.md", iconv.encode(crlf(CHINESE), "utf-8", { addBOM: true })],
    ["utf-16be-bom.txt", iconv.encode(JAPANESE, "utf-16be", { addBOM: true })],
    ["gb18030.txt", iconv.encode(crlf(CHINESE), "gb18030")],
    ["shift_jis.txt", iconv.encode(JAPANESE, "shift_jis")],
    ["windows-1252.txt", iconv.encode(FRENCH, "windows-1252")],
  ];
  for (const [name, bytes] of files) {
    cloudFiles.set(name, bytes);
    const { text, sha256: expectedSha256, ...format } = await api.fs.readTextDocument(uri(name));
    assert.deepEqual(
      await api.fs.writeTextDocument(uri(name), text, { ...format, expectedSha256 }),
      { sha256: sha256(bytes) },
      name,
    );
    assert.deepEqual(
      Object.fromEntries(lastWrite().query),
      { projectId: PROJECT, path: name, overwrite: "true", createParents: "true", expectedSha256 },
      name,
    );
  }

  // expectedSha256 为 null 时不覆盖；换行统一为 eol。A null expectedSha256 never overwrites; line breaks become eol.
  const created = await api.fs.writeTextDocument(uri("new.md"), "a\nb\r\nc\rd", {
    encoding: "utf-8",
    bom: false,
    eol: "\r\n",
    expectedSha256: null,
    createParents: false,
  });
  assert.deepEqual(created, { sha256: sha256(new TextEncoder().encode("a\r\nb\r\nc\r\nd")) });
  assert.deepEqual(Object.fromEntries(lastWrite().query), {
    projectId: PROJECT,
    path: "new.md",
    overwrite: "false",
    createParents: "false",
  });
  assert.match(String(lastWrite().body), /a\r\nb\r\nc\r\nd/u);
});

test("readTextDocument and writeTextDocument read and write local files through the main process", async () => {
  const { api, storage, localUri } = await createHarness([]);
  const files: Array<[string, Uint8Array]> = [
    ["utf-16le.txt", iconv.encode(crlf(CHINESE), "utf-16le", { addBOM: true })],
    ["gb18030.txt", iconv.encode(crlf(CHINESE), "gb18030")],
    ["windows-1252.txt", iconv.encode(FRENCH, "windows-1252")],
  ];
  for (const [name, bytes] of files) {
    await writeFile(join(storage, name), bytes);
    const {
      text,
      sha256: expectedSha256,
      ...format
    } = await api.fs.readTextDocument(localUri(name));
    assert.equal(expectedSha256, sha256(bytes), name);
    const written = await api.fs.writeTextDocument(localUri(name), text, {
      ...format,
      expectedSha256,
    });
    assert.deepEqual(await readFile(join(storage, name)), Buffer.from(bytes), name);
    assert.deepEqual(written, { sha256: expectedSha256 }, name);
  }

  // 换行替换与 BOM；返回的内容哈希与写入的字节一致。Line breaks and the BOM; the returned hash matches the bytes written.
  const read = sha256(await readFile(join(storage, "utf-16le.txt")));
  const changed = await api.fs.writeTextDocument(localUri("utf-16le.txt"), "x\ny\rz", {
    encoding: "utf-16le",
    bom: true,
    eol: "\r\n",
    expectedSha256: read,
  });
  const changedBytes = await readFile(join(storage, "utf-16le.txt"));
  assert.deepEqual(
    changedBytes,
    Buffer.concat([Buffer.from([0xff, 0xfe]), iconv.encode("x\r\ny\r\nz", "utf-16le")]),
  );
  assert.deepEqual(changed, { sha256: sha256(changedBytes) });

  // 过期的内容哈希为 ERR_FILE_CHANGED，null 不覆盖已有文件；两者都不改变文件。
  // A stale hash is ERR_FILE_CHANGED and null never overwrites an existing file; neither changes it.
  await assert.rejects(
    api.fs.writeTextDocument(localUri("utf-16le.txt"), "lost", {
      encoding: "utf-16le",
      bom: true,
      eol: "\r\n",
      expectedSha256: read,
    }),
    { code: "ERR_FILE_CHANGED" },
  );
  await assert.rejects(
    api.fs.writeTextDocument(localUri("utf-16le.txt"), "lost", {
      encoding: "utf-8",
      bom: false,
      eol: "\n",
      expectedSha256: null,
    }),
    { code: "EEXIST" },
  );
  assert.deepEqual(await readFile(join(storage, "utf-16le.txt")), changedBytes);

  const created = await api.fs.writeTextDocument(localUri("new/dir/note.md"), "# Title\n", {
    encoding: "utf-8",
    bom: true,
    eol: "\n",
    expectedSha256: null,
  });
  const createdBytes = await readFile(join(storage, "new/dir/note.md"));
  assert.deepEqual(
    createdBytes,
    Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from("# Title\n")]),
  );
  assert.deepEqual(created, { sha256: sha256(createdBytes) });
});

test("writeTextDocument reports unencodable characters, BOMs on other encodings and invalid preconditions before writing", async () => {
  const { api, server, uri } = await createHarness(["fs.cloud.read", "fs.cloud.write"]);
  const sent = server.requests.length;
  await assert.rejects(
    api.fs.writeTextDocument(uri("gbk.txt"), "first line\nab😀cd", {
      encoding: "gbk",
      bom: false,
      eol: "\r\n",
      expectedSha256: null,
    }),
    { code: "ERR_UNENCODABLE", message: /"😀" \(U\+1F600\) at line 2, column 3 /u },
  );
  await assert.rejects(
    api.fs.writeTextDocument(uri("gb18030.txt"), "A", {
      encoding: "gb18030",
      bom: true,
      eol: "\n",
      expectedSha256: null,
    }),
    { code: "ERR_INVALID_ARGUMENT" },
  );
  for (const expectedSha256 of ["A".repeat(64), "a".repeat(63), 42, undefined]) {
    await assert.rejects(
      api.fs.writeTextDocument(uri("a.txt"), "A", {
        encoding: "utf-8",
        bom: false,
        eol: "\n",
        expectedSha256: expectedSha256 as string,
      }),
      { code: "ERR_INVALID_ARGUMENT" },
      String(expectedSha256),
    );
  }
  const aborted = new AbortController();
  aborted.abort();
  await assert.rejects(
    api.fs.writeTextDocument(uri("a.txt"), "A", {
      encoding: "utf-8",
      bom: false,
      eol: "\n",
      expectedSha256: null,
      signal: aborted.signal,
    }),
    { code: "ABORT_ERR" },
  );
  assert.equal(server.requests.length, sent);
});

test("writeFile checks expectedSha256 and passes it to the Server and to the main process", async () => {
  const hash = sha256(new Uint8Array([1]));
  const cloud = await createHarness(["fs.cloud.write"], {
    routes: {
      "PUT /api/files/write": (request: ApiRequestRecord) =>
        request.query.get("path") === "changed.bin"
          ? failure(409, 4090024, "The file was changed after it was read.")
          : { name: "b.bin", path: "b.bin", type: "file" },
    },
  });
  const writes = () =>
    cloud.server.requests.filter((request) => request.path === "/api/files/write");
  await cloud.api.fs.writeFile(cloud.uri("b.bin"), new Uint8Array([2]), { expectedSha256: hash });
  assert.equal(writes().at(-1)!.query.get("expectedSha256"), hash);
  assert.equal(writes().at(-1)!.query.get("overwrite"), "true");
  await assert.rejects(
    cloud.api.fs.writeFile(cloud.uri("changed.bin"), new Uint8Array([2]), { expectedSha256: hash }),
    { code: "ERR_FILE_CHANGED" },
  );

  type WriteOptions = Parameters<ExtensionApi["fs"]["writeFile"]>[2];
  const invalid = [
    { expectedSha256: hash.toUpperCase() },
    { expectedSha256: hash.slice(1) },
    { expectedSha256: 1 },
    { expectedSha256: null },
    { overwrite: false, expectedSha256: hash },
  ] as unknown as WriteOptions[];
  const count = writes().length;
  for (const options of invalid) {
    await assert.rejects(
      cloud.api.fs.writeFile(cloud.uri("b.bin"), new Uint8Array([3]), options),
      { code: "ERR_INVALID_ARGUMENT" },
      JSON.stringify(options),
    );
  }
  assert.equal(writes().length, count);

  const local = await createHarness([]);
  const path = join(local.storage, "note.txt");
  await writeFile(path, "one");
  for (const options of invalid) {
    await assert.rejects(
      local.api.fs.writeFile(local.localUri("note.txt"), "two", options),
      { code: "ERR_INVALID_ARGUMENT" },
      JSON.stringify(options),
    );
  }
  await assert.rejects(
    local.api.fs.writeFile(local.localUri("note.txt"), "two", { expectedSha256: hash }),
    { code: "ERR_FILE_CHANGED" },
  );
  await local.api.fs.writeFile(local.localUri("note.txt"), "two", {
    expectedSha256: sha256(new TextEncoder().encode("one")),
  });
  assert.equal(await readFile(path, "utf8"), "two");
});

test("listTextEncodings returns the codec's encodings without any permission", async () => {
  const { api } = await createHarness([]);
  const encodings = await api.fs.listTextEncodings();
  assert.equal(encodings.length, 38);
  assert.deepEqual(encodings, listTextEncodings());
});

test("showMessageBox checks its options and refuses background frames", async () => {
  const view = await createHarness([]);
  const valid = { message: "Saved.", buttons: ["OK", "Cancel"] };
  type MessageBoxOptions = Parameters<ExtensionApi["dialog"]["showMessageBox"]>[0];
  const invalid = [
    undefined,
    "Saved.",
    { buttons: ["OK"] },
    { ...valid, message: "" },
    { ...valid, message: "m".repeat(501) },
    { ...valid, detail: "d".repeat(2001) },
    { ...valid, detail: null },
    { ...valid, buttons: [] },
    { ...valid, buttons: ["1", "2", "3", "4", "5"] },
    { ...valid, buttons: ["OK", ""] },
    { ...valid, buttons: ["b".repeat(41)] },
    { ...valid, buttons: "OK" },
    { ...valid, defaultId: 2 },
    { ...valid, defaultId: -1 },
    { ...valid, cancelId: 0.5 },
    { ...valid, cancelId: 2 },
    { ...valid, type: "question" },
  ] as unknown as MessageBoxOptions[];
  for (const options of invalid) {
    await assert.rejects(
      view.api.dialog.showMessageBox(options),
      { code: "ERR_INVALID_ARGUMENT" },
      JSON.stringify(options),
    );
  }

  const background = await createHarness([], { kind: "background" });
  await assert.rejects(background.api.dialog.showMessageBox(valid), {
    code: "ERR_UNSUPPORTED_OPERATION",
  });
});

/* ---------- 通信事件 chat.observe ---------- */

type Harness = Awaited<ReturnType<typeof createHarness>>;

const CHAT_SCOPE = { serverId: "local", projectId: PROJECT, conversationId: "conversation-1" };
/** extension 会话使用的对话；broker 只接受 UUID。The conversation extension sessions use; the broker accepts only UUIDs. */
const SESSION_CONVERSATION = "0192f0c5-4b1d-7a10-8f2e-1c2d3e4f5a6b";
const SESSION_SCOPE = { ...CHAT_SCOPE, conversationId: SESSION_CONVERSATION };
const MESSAGES_PATH = `/api/projects/${PROJECT}/conversations/conversation-1/messages`;
const OLDER_PAGE = { direction: "older", pageSize: 50 } as const;

function chatText(messageId: string): AiMessage {
  return {
    messageId,
    role: "assistant",
    author: "agent",
    contents: [{ type: "TextContent", content: messageId }],
  };
}

function messagePage(...messageIds: string[]) {
  return { items: messageIds.map(chatText), nextCursor: null, hasMore: false, turns: [] };
}

/** 消息事件取消息 id，其他事件取 kind。A message event's message id, or the kind of any other event. */
function eventNames(events: readonly ChatEvent[]): string[] {
  return events.map((event) =>
    event.kind === "message" ? String(event.message.messageId) : event.kind,
  );
}

/**
 * 页面的一次往返请求：回复到达时，宿主此前发出的事件都已送达页面。
 * A page round trip: by the time its reply arrives, every event the host emitted earlier has reached the page.
 */
async function flushEvents(api: ExtensionApi): Promise<void> {
  await api.fs.listTextEncodings();
}

/** 同一 profile 改用新的地址或 token。The same profile switches to another address or token. */
function switchConnection(harness: Harness, next: { baseUrl?: string; token?: string }): void {
  const identity = { ...harness.identity, ...next };
  configureApiRuntime({ baseUrl: identity.baseUrl, token: identity.token });
  harness.context.setConnection(identity, { id: "local", name: "Local", kind: "local" });
}

test("chat.observe rejects invalid filters and subscription ids", async () => {
  const { api, endpoint } = await createHarness(["chat.observe"]);
  await assert.rejects(
    api.chat.observe({ kinds: [] } as unknown as ChatEventFilter, () => undefined),
    { code: "ERR_INVALID_ARGUMENT", message: /kinds/ },
  );
  await assert.rejects(
    api.chat.observe({ unknown: true } as unknown as ChatEventFilter, () => undefined),
    { code: "ERR_INVALID_ARGUMENT", message: /unknown/ },
  );
  assert.equal(
    await endpoint.request("chat.observe", { subscriptionId: "same", filter: {} }),
    null,
  );
  for (const subscriptionId of ["same", "", "s".repeat(65), 7, undefined]) {
    await assert.rejects(
      endpoint.request("chat.observe", { subscriptionId, filter: {} }),
      { code: "ERR_INVALID_ARGUMENT" },
      String(subscriptionId),
    );
  }
});

test("a history subscription receives each history message in order and nothing else", async () => {
  const harness = await createHarness(["chat.observe"], {
    routes: { [`GET ${MESSAGES_PATH}`]: messagePage("history-1", "history-2") },
  });
  const history: ChatEvent[] = [];
  const live: ChatEvent[] = [];
  await harness.api.chat.observe({ sources: ["history"] }, (event) => history.push(event));
  await harness.api.chat.observe({ sources: ["live"] }, (event) => live.push(event));

  await harness.chat.getConversationMessages(CHAT_SCOPE, OLDER_PAGE);
  await flushEvents(harness.api);

  assert.deepEqual(eventNames(history), ["history-1", "history-2"]);
  assert.deepEqual(
    history.map((event) => event.scope),
    [CHAT_SCOPE, CHAT_SCOPE],
  );
  assert.deepEqual(live, []);
});

test("events of another profile, or of an earlier address or token of the profile, are not sent", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const second = await startApiServer({});
  const harness = await createHarness(["chat.observe"], {
    routes: { [`GET ${MESSAGES_PATH}`]: messagePage("history-1") },
  });
  const events: ChatEvent[] = [];
  await harness.api.chat.observe({ kinds: ["message"] }, (event) => events.push(event));
  const setting = { projectId: PROJECT, conversationId: "conversation-1" };
  const sessions = [];

  await harness.chat.getConversationMessages({ ...CHAT_SCOPE, serverId: "other" }, OLDER_PAGE);
  sessions.push(harness.chat.createExecutionSession(CHAT_SCOPE, { onMessage: () => undefined }));
  await sessions[0]!.configure(setting);
  hubs[0]!.receive(chatText("before"));

  switchConnection(harness, { baseUrl: second.baseUrl });
  hubs[0]!.receive(chatText("old-address"));
  sessions.push(harness.chat.createExecutionSession(CHAT_SCOPE, { onMessage: () => undefined }));
  await sessions[1]!.configure(setting);
  hubs[1]!.receive(chatText("new-address"));

  switchConnection(harness, { baseUrl: second.baseUrl, token: "agw_rotated" });
  hubs[1]!.receive(chatText("old-token"));
  sessions.push(harness.chat.createExecutionSession(CHAT_SCOPE, { onMessage: () => undefined }));
  await sessions[2]!.configure(setting);
  hubs[2]!.receive(chatText("new-token"));
  await flushEvents(harness.api);

  assert.deepEqual(eventNames(events), ["before", "new-address", "new-token"]);
  for (const session of sessions) await session.dispose();
});

test("the response and history messages of a request that returns after a connection change are not sent", async () => {
  const harness = await createHarness(["chat.observe"], {
    routes: { [`GET ${MESSAGES_PATH}`]: messagePage("history-1") },
  });
  const events: ChatEvent[] = [];
  await harness.api.chat.observe({ channels: ["http"] }, (event) => events.push(event));

  const release = harness.holdHttp();
  const page = harness.chat.getConversationMessages(CHAT_SCOPE, OLDER_PAGE);
  switchConnection(harness, { token: "agw_rotated" });
  release();
  await page;
  await flushEvents(harness.api);

  assert.deepEqual(eventNames(events), ["request"]);
});

test("SettingCommand requests and responses reach extensions without environmentVariables", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["chat.observe"]);
  const filter: ChatEventFilter = { kinds: ["request", "response"], channels: ["execution"] };
  const hostEvents: ChatEvent[] = [];
  harness.chat.subscribe(filter, (event) => hostEvents.push(event));
  const events: ChatEvent[] = [];
  await harness.api.chat.observe(filter, (event) => events.push(event));

  const session = harness.chat.createExecutionSession(CHAT_SCOPE, { onMessage: () => undefined });
  await session.configure({
    projectId: PROJECT,
    conversationId: "conversation-1",
    environmentVariables: { API_KEY: "secret" },
    permissionMode: "fullAccess",
  });
  await session.dispose();
  await flushEvents(harness.api);

  const isSetting = (event: ChatEvent): event is ChatRequestEvent | ChatResponseEvent =>
    event.kind !== "message" &&
    event.channel === "execution" &&
    event.request.method === "DispatchCommand" &&
    (event.request.args[0] as { type?: unknown }).type === "SettingCommand";
  const command = {
    type: "SettingCommand",
    projectId: PROJECT,
    conversationId: "conversation-1",
    permissionMode: "fullAccess",
  };
  assert.deepEqual(eventNames(events.filter(isSetting)), ["request", "response"]);
  assert.deepEqual(
    events.filter(isSetting),
    hostEvents.filter(isSetting).map((event) => ({
      ...event,
      request: { ...event.request, args: [command] },
    })),
  );
  assert.deepEqual(
    events.filter((event) => !isSetting(event)),
    hostEvents.filter((event) => !isSetting(event)),
  );
});

test("a disposed chat subscription receives nothing more and can be disposed again", async (t) => {
  const errors = t.mock.method(console, "error");
  const harness = await createHarness(["chat.observe"], {
    routes: { [`GET ${MESSAGES_PATH}`]: messagePage("history-1") },
  });
  const events: ChatEvent[] = [];
  const subscription = await harness.api.chat.observe({ kinds: ["request"] }, (event) =>
    events.push(event),
  );
  await harness.chat.getConversationMessages(CHAT_SCOPE, OLDER_PAGE);
  await flushEvents(harness.api);
  assert.deepEqual(eventNames(events), ["request"]);

  subscription.dispose();
  subscription.dispose();
  await flushEvents(harness.api);
  assert.equal(harness.activeChatSubscriptions(), 0);
  await harness.chat.getConversationMessages(CHAT_SCOPE, OLDER_PAGE);
  await flushEvents(harness.api);

  assert.deepEqual(eventNames(events), ["request"]);
  assert.equal(errors.mock.callCount(), 0);
});

test("an extension holds at most 16 chat subscriptions across its frames", async () => {
  const harness = await createHarness(["chat.observe"]);
  const channel = new MessageChannel();
  harness.broker.connect(
    {
      extensionId: "sample",
      frameId: "frame-2",
      pageId: "page-2",
      kind: "sidebarView",
      viewId: "list",
      params: {},
    },
    channel.port1,
  );
  const sidebar = await connectSdk(channel.port2);
  after(() => {
    harness.broker.disconnect("frame-2");
    sidebar.endpoint.close();
  });
  const subscriptions = [];
  for (let index = 0; index < 10; index++) {
    subscriptions.push(await harness.api.chat.observe({}, () => undefined));
  }
  for (let index = 0; index < 6; index++) {
    subscriptions.push(await sidebar.api.chat.observe({}, () => undefined));
  }

  await assert.rejects(
    sidebar.api.chat.observe({}, () => undefined),
    { code: "ERR_TOO_MANY_SUBSCRIPTIONS" },
  );
  subscriptions[0]!.dispose();
  await flushEvents(harness.api);
  await sidebar.api.chat.observe({}, () => undefined);
  assert.equal(harness.activeChatSubscriptions(), 16);
});

test("disconnecting a frame cancels its chat subscriptions", async () => {
  const harness = await createHarness(["chat.observe"]);
  await harness.api.chat.observe({}, () => undefined);
  await harness.api.chat.observe({ kinds: ["message"] }, () => undefined);
  assert.equal(harness.activeChatSubscriptions(), 2);

  harness.broker.disconnect("frame-1");

  assert.equal(harness.activeChatSubscriptions(), 0);
});

test("chat.observe fails while the frame is closing", async () => {
  const { api } = await createHarness(["chat.observe"], { frameClosing: true });
  await assert.rejects(
    api.chat.observe({}, () => undefined),
    { code: "ERR_FRAME_CLOSING" },
  );
});

test("hub calls of a session an extension creates carry the extension origin and the current profile", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["chat.observe", "execution"], {
    routes: { [CONVERSATION_ROUTE]: conversationDetails("sample", false) },
  });
  const events: ChatEvent[] = [];
  await harness.api.chat.observe({ kinds: ["request"], channels: ["execution"] }, (event) =>
    events.push(event),
  );

  const session = await harness.api.execution.createSession({
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
  });
  await flushEvents(harness.api);
  await session.dispose();

  assert.ok(events.length > 0);
  for (const event of events) {
    assert.deepEqual(event.origin, { kind: "extension", extensionId: "sample" });
    assert.deepEqual(event.scope, SESSION_SCOPE);
  }
});

/* ---------- 执行会话的只读规则 Read-only execution sessions ---------- */

const CONVERSATION_ROUTE = `GET /api/projects/${PROJECT}/conversations/${SESSION_CONVERSATION}`;
const READ_ONLY_HUB_ERROR =
  "HubException: 4030008: The conversation is read-only for this extension.";

function conversationDetails(extensionId: string, openToExtensions: boolean) {
  return {
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
    contextId: "context-1",
    title: "Chat",
    executionCount: 1,
    messageCount: 1,
    createTime: "2026-10-24T00:00:00Z",
    extensionId,
    openToExtensions,
    usage: {
      inputTokenCount: 0,
      outputTokenCount: 0,
      totalTokenCount: 0,
      cachedInputTokenCount: 0,
      reasoningTokenCount: 0,
      unsettledTurnIds: [],
    },
    resumeState: null,
  };
}

/**
 * 服务端以只读请求方收到的错误拒绝写入命令；Distributed 模式下随后确认这一轮的 Turn 不存在。
 * The server refuses write commands with the error a read-only requester gets; in Distributed mode it then confirms the turn of this send does not exist.
 */
function refuseWrites({ method, args }: { method: string; args: unknown[] }): unknown {
  if (method === "DispatchCommand") {
    const type = (args[0] as { type?: string }).type;
    if (type === "SubscribeTurnCommand")
      throw new Error("HubException: 4040011: Durable execution was not found.");
    if (type !== "SettingCommand") throw new Error(READ_ONLY_HUB_ERROR);
  }
  if (method === "RecoverInProcessExecution" && args[1] === true)
    throw new Error(READ_ONLY_HUB_ERROR);
  return undefined;
}

/** 以另一个 frame 连接，可以是另一个 extension。Connects another frame, possibly of another extension. */
async function connectFrame(harness: Harness, extensionId: string, frameId: string) {
  const channel = new MessageChannel();
  harness.broker.connect(
    {
      extensionId,
      frameId,
      pageId: `${frameId}-page`,
      kind: "mainView",
      viewId: "app",
      params: {},
    },
    channel.port1,
  );
  const sdk = await connectSdk(channel.port2);
  after(() => {
    harness.broker.disconnect(frameId);
    sdk.endpoint.close();
  });
  return sdk;
}

for (const provider of ["InProcess", "Distributed"] as const) {
  test(`a session on a host conversation is read-only and every write fails with ERR_CONVERSATION_READ_ONLY (${provider})`, async (t) => {
    replaceExecutionHub(t, { provider, onInvoke: refuseWrites });
    const harness = await createHarness(["execution"], {
      routes: { [CONVERSATION_ROUTE]: conversationDetails("host", false) },
    });

    const session = await harness.api.execution.createSession({
      projectId: PROJECT,
      conversationId: SESSION_CONVERSATION,
    });
    try {
      assert.equal(session.readOnly, true);
      for (const write of [
        () => session.send({ target: { type: "agent", id: "agent-1" }, text: "hi" }),
        () => session.interrupt(),
        () => session.respond({ kind: "workflow-gate", interactionId: "gate", approved: true }),
        () => session.setPermissionMode("fullAccess"),
      ]) {
        await assert.rejects(write(), { code: "ERR_CONVERSATION_READ_ONLY" });
      }
    } finally {
      await session.dispose();
    }
  });
}

test("sessions on their own, open, draft and new conversations are not read-only", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  let details: unknown = null;
  const harness = await createHarness(["execution"], {
    routes: { [CONVERSATION_ROUTE]: () => details },
  });
  for (const [label, value] of [
    ["own", conversationDetails("sample", false)],
    ["open", conversationDetails("host", true)],
    ["draft", failure(404, 4040007, "Resource was not found.")],
  ] as const) {
    details = value;
    const session = await harness.api.execution.createSession({
      projectId: PROJECT,
      conversationId: SESSION_CONVERSATION,
    });
    assert.equal(session.readOnly, false, label);
    await session.dispose();
  }
  const requests = harness.server.requests.length;

  const created = await harness.api.execution.createSession({ projectId: PROJECT });

  assert.equal(created.readOnly, false);
  assert.equal(harness.server.requests.length, requests, "a new conversation needs no request");
  await created.dispose();
});

test("another extension's session on an extension's conversation is read-only", async (t) => {
  replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["execution"], {
    routes: { [CONVERSATION_ROUTE]: conversationDetails("sample", false) },
  });
  const other = await connectFrame(harness, "other", "frame-2");

  const mine = await harness.api.execution.createSession({
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
  });
  const theirs = await other.api.execution.createSession({
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
  });

  assert.equal(mine.readOnly, false);
  assert.equal(theirs.readOnly, true);
  await mine.dispose();
  await theirs.dispose();
});

test("a failed details request fails createSession before any hub connection", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["execution"], {
    routes: { [CONVERSATION_ROUTE]: failure(500, 5000000, "Internal error.") },
  });

  await assert.rejects(
    harness.api.execution.createSession({
      projectId: PROJECT,
      conversationId: SESSION_CONVERSATION,
    }),
  );

  assert.equal(hubs.length, 0);
});

test("createSession rejects ids that are not UUIDs before any request is sent", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["execution", "chat.observe"]);
  const events: ChatEvent[] = [];
  await harness.api.chat.observe({}, (event) => events.push(event));
  const requests = harness.server.requests.length;

  // 点段会被 URL 解析去掉，详情请求会变成其他接口。Dot segments are removed by URL parsing, which would turn the details request into another endpoint.
  for (const ids of [
    { projectId: ".", conversationId: ".." },
    { projectId: PROJECT, conversationId: ".." },
    { projectId: ".." },
  ]) {
    await assert.rejects(harness.api.execution.createSession(ids), {
      code: "ERR_INVALID_ARGUMENT",
    });
  }
  await flushEvents(harness.api);

  assert.equal(harness.server.requests.length, requests);
  assert.deepEqual(events, []);
  assert.equal(hubs.length, 0);
});

test("the details request carries the extension origin and the SettingCommand its extension id", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["execution"], {
    routes: { [CONVERSATION_ROUTE]: conversationDetails("sample", false) },
  });
  const requests: ChatRequestEvent[] = [];
  harness.chat.subscribe({ kinds: ["request"], channels: ["http"] }, (event) => {
    if (event.kind === "request") requests.push(event as ChatRequestEvent);
  });

  const session = await harness.api.execution.createSession({
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
  });
  await session.dispose();

  const details = requests.find(
    (event) => event.channel === "http" && event.request.path.endsWith("{conversationId}"),
  );
  assert.deepEqual(details?.origin, { kind: "extension", extensionId: "sample" });
  assert.equal(hubs[0]!.commands("SettingCommand")[0]?.extensionId, "sample");
});

test("live messages of the conversation from the host and other extensions reach the session", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const harness = await createHarness(["execution"], {
    routes: { [CONVERSATION_ROUTE]: conversationDetails("host", false) },
  });
  const other = await connectFrame(harness, "other", "frame-2");
  const received: string[] = [];
  const session = await harness.api.execution.createSession({
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
  });
  session.onMessage((message) => received.push(message.messageId));
  const theirs = await other.api.execution.createSession({
    projectId: PROJECT,
    conversationId: SESSION_CONVERSATION,
  });
  const runtime = getApiRuntime();
  const host = harness.chat.createExecutionSession(SESSION_SCOPE, { onMessage() {} });
  const elsewhere = harness.chat.createExecutionSession(
    { ...SESSION_SCOPE, conversationId: "conversation-2" },
    { onMessage() {} },
  );
  const stale = harness.chat.createExecutionSession(
    SESSION_SCOPE,
    { onMessage() {} },
    { runtime: { baseUrl: runtime.baseUrl, token: "old-token", attachmentStore: null } },
  );
  try {
    await host.configure({ projectId: PROJECT, conversationId: SESSION_CONVERSATION });
    await elsewhere.configure({ projectId: PROJECT, conversationId: "conversation-2" });
    await stale.configure({ projectId: PROJECT, conversationId: SESSION_CONVERSATION });
    const [mineHub, theirsHub, hostHub, elsewhereHub, staleHub] = hubs;

    hostHub!.receive(chatText("from-host"));
    theirsHub!.receive(chatText("from-other-extension"));
    mineHub!.receive(chatText("own"));
    elsewhereHub!.receive(chatText("other-conversation"));
    staleHub!.receive(chatText("old-connection"));
    await flushEvents(harness.api);

    assert.deepEqual(received, ["from-host", "from-other-extension", "own"]);

    await session.dispose();
    hostHub!.receive(chatText("after-dispose"));
    await flushEvents(harness.api);
    assert.deepEqual(received, ["from-host", "from-other-extension", "own"]);
  } finally {
    await theirs.dispose();
    await host.dispose();
    await elsewhere.dispose();
    await stale.dispose();
  }
});

/* ---------- 文档会话 Document sessions ---------- */

const EDITOR = "sample:editor";
const DOCUMENT_TEST = { timeout: 15_000 };

async function until<T>(read: () => T | undefined | false, what: string): Promise<T> {
  const deadline = Date.now() + 5_000;
  for (;;) {
    const value = read();
    if (value !== undefined && value !== false) return value;
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${what}.`);
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

/** 内容为一个字符串的文本编辑器。A text editor whose content is one string. */
class StringEditor implements TextDocumentAdapter<string> {
  public text = "";
  public session: DocumentSession | null = null;
  /** `matches` 被调用的次数。How many times `matches` was called. */
  public comparisons = 0;
  private edits = 0;

  public load(content: { text: string; eol: Eol }): void {
    this.text = content.text;
  }

  public snapshot(): string {
    return this.text;
  }

  public serialize(snapshot: string): string {
    return snapshot;
  }

  public matches(snapshot: string): boolean {
    this.comparisons += 1;
    return this.text === snapshot;
  }

  public version(): number {
    return this.edits;
  }

  /** 用户输入：替换内容并通知会话。A user edit: replaces the content and tells the session. */
  public type(text: string): void {
    this.text = text;
    this.edits += 1;
    this.session!.edited();
  }
}

/** 内容为一段字节的编辑器。An editor whose content is a byte array. */
class BytesEditor implements BinaryDocumentAdapter<Uint8Array> {
  public bytes: Uint8Array = new Uint8Array();
  public session: DocumentSession | null = null;
  private edits = 0;

  public load(bytes: Uint8Array): void {
    this.bytes = bytes;
  }

  public snapshot(): Uint8Array {
    return this.bytes.slice();
  }

  /** 写入会转移 buffer，每次返回新的副本。Writing transfers the buffer, so every call returns a new copy. */
  public serialize(snapshot: Uint8Array): Uint8Array {
    return snapshot.slice();
  }

  public matches(snapshot: Uint8Array): boolean {
    return Buffer.from(this.bytes).equals(Buffer.from(snapshot));
  }

  public version(): number {
    return this.edits;
  }

  public type(bytes: Uint8Array): void {
    this.bytes = bytes;
    this.edits += 1;
    this.session!.edited();
  }
}

/**
 * 文档会话的测试环境：生产代码的 ExtensionHost（确认队列、未保存状态与通知）及其 broker，主进程一侧在测试进程中实现。
 * 会话读写存储目录中的本地文件，经主进程的 ExtensionFileSystem（写入前提、原子提交）与 text-codec；宿主在 `window`
 * 上接收端口，会话在 `window` 上处理快捷键，这里用 jsdom 的 window。
 * The document session environment: a real ExtensionHost (confirmation queue, unsaved state and notifications) with
 * its broker, and the main-process side inside the test process. Sessions read and write local files in the storage
 * directory through the main-process ExtensionFileSystem (write preconditions, atomic commits) and text codec; the
 * host receives ports and the session handles its shortcut on `window`, a jsdom window here.
 */
async function createDocumentHost() {
  const { window } = new JSDOM("<!doctype html><html><body></body></html>", {
    url: "http://localhost/",
  });
  Object.defineProperty(globalThis, "window", { configurable: true, value: window });
  const storage = await createScratchDirectory("document-session");
  const localFiles = new ExtensionFileSystem(
    new ExtensionAccessGrants(),
    new ReplacedFileRegistry(),
  );
  const notifications: string[] = [];
  /** 主进程收到的本地文件操作。The local file operations the main process received. */
  const fileOperations: string[] = [];
  /** 不为 null 时，写入提交后等到 released 才回复。When set, a committed write replies only after released. */
  let writeHold: { held(): void; released: Promise<void> } | null = null;
  const main = createMainProcess(window, {
    installed: sampleExtension([
      {
        id: "editor",
        title: "Editor",
        icon: "Pencil",
        patterns: ["*.txt", "*.bin"],
        entry: "editor.html",
      },
    ]),
    storageDirectory: storage.path,
    channels: {
      extensionFileSystem: async (caller, request) => {
        fileOperations.push(request.operation);
        const result = await toExtensionIpcResult(() =>
          localFiles.execute(
            {
              extensionId: caller.extensionId,
              grantedPermissions: [],
              storageDirectory: storage.path,
            },
            request,
          ),
        );
        if (request.operation === "writeFile" && writeHold !== null) {
          writeHold.held();
          await writeHold.released;
        }
        return result;
      },
      decodeText: (bytes, encoding) =>
        toExtensionIpcResult(async () => decodeText(bytes, encoding)),
      encodeText: (text, options) => toExtensionIpcResult(async () => encodeText(text, options)),
      listTextEncodings: async () => listTextEncodings(),
    },
  });
  const host = new ExtensionHost({
    bridge: main.bridge,
    app: { version: "0.5.0", platform: "darwin", architecture: "arm64" },
    notify: (extensionName, notification) =>
      notifications.push(`${extensionName} ${notification.type}: ${notification.message}`),
  });
  await startExtensionHost(host, window);
  // 查看器参数只交给 onLoad；会话打开的是存储目录中的本地文件。The viewer params only reach onLoad; sessions open local files.
  host.setFileViewerFile(EDITOR, {
    uri: formatCloudUri({
      serverId: "local",
      projectId: PROJECT,
      directoryId: null,
      path: "a.txt",
    }),
    name: "a.txt",
    projectId: PROJECT,
  });
  host.acquireView(EDITOR);
  const page = await until(() => {
    const frameId = host.frameIdOfView(EDITOR);
    return frameId === null ? undefined : main.pages.get(frameId);
  }, "the editor page");
  const api = await page.api;
  /**
   * 页面发出一个请求并等到回复；回复到达时宿主已经处理并回复了页面此前发来的全部请求。
   * The page sends one request and waits for the reply; by then the host handled and answered every earlier request.
   */
  const flush = async () => {
    await api.fs.listTextEncodings();
  };
  after(async () => {
    await flush();
    host.retainFileViewers([]);
    await until(() => host.frameIdOfView(EDITOR) === null, "the editor to close");
    window.close();
    Reflect.deleteProperty(globalThis, "window");
    await storage.dispose();
  });
  return {
    api,
    host,
    window,
    notifications,
    fileOperations,
    flush,
    /**
     * 之后的写入提交后不回复：`held` 在写入开始等待时完成，`release` 让它回复。
     * Later committed writes do not reply: `held` resolves once a write waits, and `release` lets it reply.
     */
    holdWrites: () => {
      let held!: () => void;
      let release!: () => void;
      const waiting = new Promise<void>((resolve) => (held = resolve));
      writeHold = { held, released: new Promise<void>((resolve) => (release = resolve)) };
      return {
        held: waiting,
        release: () => {
          writeHold = null;
          release();
        },
      };
    },
    path: (name: string) => join(storage.path, name),
    file: (name: string) => ({
      uri: pathToFileURL(join(storage.path, name)).toString(),
      name,
    }),
    /** 宿主是否记录了编辑器的未保存修改。Whether the host recorded unsaved changes of the editor. */
    unsaved: () => host.getSnapshot().unsavedViewIds.includes(EDITOR),
    /** 宿主记录的编辑器保存状态。The editor's save status the host recorded. */
    saveStatus: () => host.getSnapshot().saveStatuses[EDITOR] ?? null,
    /** 等到确认窗口显示消息框，返回它的内容。Waits for the confirmation window's message box and returns it. */
    messageBox: async () => {
      const confirmation = await until(
        () => main.confirmations.current() ?? undefined,
        "the confirmation window's message box",
      );
      assert.equal(confirmation.kind, "message");
      return confirmation;
    },
    /** 确认窗口中显示的内容。What the confirmation window shows. */
    confirmation: () => main.confirmations.current(),
    /** 以确认窗口的身份回答。Answers as the confirmation window. */
    answer: (value: unknown) => main.confirmations.answer(value),
  };
}

/** 打开文本文档并等到读取完成。Opens a text document and waits until it is read. */
async function openText(
  harness: Awaited<ReturnType<typeof createDocumentHost>>,
  name: string,
  editor = new StringEditor(),
) {
  const session = harness.api.documents.openText({ file: harness.file(name), adapter: editor });
  editor.session = session;
  await until(() => session.state !== "loading", `${name} to load`);
  assert.equal(session.state, "clean");
  return { editor, session };
}

const CONFLICT_BOX = {
  kind: "message",
  origin: { kind: "extension", extensionId: "sample", extensionName: "Sample" },
  type: "warning",
  message: "notes.txt was changed on disk after it was opened.",
  detail: "Overwrite it with your version, or reload it and discard your changes.",
  buttons: ["Overwrite", "Reload", "Cancel"],
  defaultId: 2,
  cancelId: 2,
};

test(
  "a document session reads, saves in the original encoding and reports unsaved changes to the host",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    await writeFile(harness.path("notes.txt"), iconv.encode(crlf(CHINESE), "gb18030"));
    const { editor, session } = await openText(harness, "notes.txt");
    const states: string[] = [];
    session.onDidChange(() => states.push(session.state));
    assert.equal(editor.text, crlf(CHINESE));
    assert.deepEqual(session.format, { encoding: "gb18030", bom: false, eol: "\r\n" });
    assert.deepEqual(session.file, harness.file("notes.txt"));

    // 编辑器中的换行写回时统一为基线的换行符。Line breaks are written back as the baseline's.
    editor.type(`${crlf(CHINESE)}新的一行\n`);
    assert.equal(session.state, "dirty");
    await harness.flush();
    assert.equal(harness.unsaved(), true);
    await session.save();
    assert.deepEqual(
      await readFile(harness.path("notes.txt")),
      iconv.encode(`${crlf(CHINESE)}新的一行\r\n`, "gb18030"),
    );
    assert.equal(session.state, "clean");
    await harness.flush();
    assert.equal(harness.unsaved(), false);

    // 撤销到基线后未保存标记消失；不修改直接保存不写入。Undoing to the baseline clears the flag; an unchanged save does not write.
    editor.type("draft");
    await harness.flush();
    assert.equal(harness.unsaved(), true);
    editor.type(`${crlf(CHINESE)}新的一行\n`);
    await session.save();
    await harness.flush();
    assert.equal(harness.unsaved(), false);
    assert.deepEqual(states, ["dirty", "saving", "clean", "dirty", "clean"]);
    assert.deepEqual(harness.notifications, []);
  },
);

test(
  "save conflicts are answered in the host's message box: Cancel, Overwrite, Overwrite of a deleted file and Reload",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "one\n");
    const { editor, session } = await openText(harness, "notes.txt");

    // Cancel：磁盘上的文件不变，未保存修改保留。Cancel keeps the file on disk and the unsaved changes.
    editor.type("mine\n");
    await writeFile(path, "theirs\n");
    const canceled = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    assert.equal(session.state, "conflict");
    await harness.answer(2);
    await canceled;
    assert.equal(session.state, "dirty");
    assert.equal(await readFile(path, "utf8"), "theirs\n");
    await harness.flush();
    assert.equal(harness.unsaved(), true);

    // Overwrite：以重新读取的内容哈希写入同一份内容。Overwrite writes the same content under the hash read again.
    const overwritten = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    await harness.answer(0);
    await overwritten;
    assert.equal(await readFile(path, "utf8"), "mine\n");
    assert.equal(session.state, "clean");

    // 文件已被删除：Overwrite 重新创建它。The file was deleted: Overwrite creates it again.
    editor.type("mine again\n");
    await rm(path);
    const recreated = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    await harness.answer(0);
    await recreated;
    assert.equal(await readFile(path, "utf8"), "mine again\n");
    assert.equal(session.state, "clean");

    // Reload：读取磁盘上的内容，未保存标记清除。Reload reads the disk content and clears the flag.
    editor.type("discarded\n");
    await writeFile(path, "from disk\n");
    const reloaded = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    await harness.answer(1);
    await reloaded;
    assert.equal(editor.text, "from disk\n");
    assert.equal(session.state, "clean");
    await harness.flush();
    assert.equal(harness.unsaved(), false);
    assert.equal(harness.confirmation(), null);
    assert.deepEqual(harness.notifications, []);
  },
);

test(
  "a refresh replaces unedited content but keeps text typed before the read returns",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "one\n");
    const { editor, session } = await openText(harness, "notes.txt");

    await writeFile(path, "two\n");
    await session.show(harness.file("notes.txt"));
    assert.equal(editor.text, "two\n");
    assert.equal(session.state, "clean");
    assert.equal(session.changedOnDisk, false);

    // 调用 show 后、读取返回前输入。Typed after show and before the read returns.
    await writeFile(path, "three\n");
    const refreshing = session.show(harness.file("notes.txt"));
    editor.type("typed\n");
    await refreshing;
    assert.equal(editor.text, "typed\n");
    assert.equal(session.state, "dirty");
    assert.equal(session.changedOnDisk, true);

    // 之后保存时由写入前提发现冲突。A later save meets the conflict through the write precondition.
    const saving = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    await harness.answer(2);
    await saving;
    assert.equal(await readFile(path, "utf8"), "three\n");
    assert.equal(session.changedOnDisk, true);
  },
);

test(
  "a document session reports its save status to the host, and the host's Reload reads a file changed on disk",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "one\n");
    const { editor, session } = await openText(harness, "notes.txt");
    await harness.flush();
    assert.equal(harness.saveStatus(), "saved");

    editor.type("edited\n");
    await harness.flush();
    assert.equal(harness.saveStatus(), "unsaved");
    await session.save();
    await harness.flush();
    assert.equal(harness.saveStatus(), "saved");

    // 读取返回前输入：内容保留，磁盘已变化。Typed before the read returns: the content stays and the disk changed.
    await writeFile(path, "two\n");
    const refreshing = session.show(harness.file("notes.txt"));
    editor.type("typed\n");
    await refreshing;
    await harness.flush();
    assert.equal(harness.saveStatus(), "changed-on-disk");

    // 宿主的 Reload 先确认放弃修改，再读取磁盘内容。The host's Reload confirms discarding first, then reads the disk.
    harness.host.reloadFileViewerDocument(EDITOR);
    const box = await harness.messageBox();
    assert.equal(box.message, "Discard unsaved changes to notes.txt?");
    await harness.answer(0);
    await until(() => session.state === "clean" && editor.text === "two\n", "the reloaded file");
    await harness.flush();
    assert.equal(harness.saveStatus(), "saved");
    assert.equal(session.changedOnDisk, false);

    // 切换文件时宿主清除旧文件的保存状态。Switching files clears the old file's status in the host.
    harness.host.setFileViewerFile(EDITOR, {
      uri: formatCloudUri({
        serverId: "local",
        projectId: PROJECT,
        directoryId: null,
        path: "b.txt",
      }),
      name: "b.txt",
      projectId: PROJECT,
    });
    assert.equal(harness.saveStatus(), null);
  },
);

test(
  "Cmd+S saves through the window's capture-phase listener until the session is disposed",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "one\n");
    const { editor, session } = await openText(harness, "notes.txt");
    const press = (init: KeyboardEventInit) => {
      const event = new harness.window.KeyboardEvent("keydown", {
        key: "s",
        bubbles: true,
        cancelable: true,
        ...init,
      });
      harness.window.document.body.dispatchEvent(event);
      return event.defaultPrevented;
    };

    editor.type("by shortcut\n");
    assert.equal(press({ metaKey: true, shiftKey: true }), false);
    assert.equal(press({ metaKey: true, altKey: true }), false);
    assert.equal(session.state, "dirty");
    assert.equal(press({ metaKey: true }), true);
    await until(() => session.state === "clean", "the shortcut save");
    assert.equal(await readFile(path, "utf8"), "by shortcut\n");

    session.dispose();
    editor.type("after dispose\n");
    assert.equal(press({ metaKey: true }), false);
    assert.equal(await readFile(path, "utf8"), "by shortcut\n");
  },
);

test("binary documents hash the bytes before the write transfers them", DOCUMENT_TEST, async () => {
  const harness = await createDocumentHost();
  const path = harness.path("image.bin");
  await writeFile(path, new Uint8Array([0, 1, 2, 255]));
  const editor = new BytesEditor();
  const session = harness.api.documents.openBinary({
    file: harness.file("image.bin"),
    adapter: editor,
  });
  editor.session = session;
  await until(() => session.state === "clean", "image.bin to load");
  assert.deepEqual([...editor.bytes], [0, 1, 2, 255]);
  assert.equal(session.format, null);

  editor.type(new Uint8Array([3, 4]));
  await session.save();
  assert.deepEqual(await readFile(path), Buffer.from([3, 4]));
  // 第二次保存以第一次写入的内容哈希为写入前提。The second save's precondition is the hash of the first write.
  editor.type(new Uint8Array([5]));
  void session.save();
  await until(() => session.state !== "saving", "the second save");
  assert.equal(harness.confirmation(), null);
  assert.equal(session.state, "clean");
  assert.deepEqual(await readFile(path), Buffer.from([5]));
  await assert.rejects(session.reopenWithEncoding("utf-8"), { code: "ERR_UNSUPPORTED_OPERATION" });
});

test(
  "encoding operations confirm discarding, notify unencodable characters and change the baseline encoding",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "abc\n");
    const { editor, session } = await openText(harness, "notes.txt");
    assert.deepEqual(session.format, { encoding: "utf-8", bom: false, eol: "\n" });

    // 有未保存修改时先确认放弃；Cancel 后不读取。Unsaved changes are confirmed first; Cancel does not read.
    editor.type("unsaved\n");
    const canceled = session.reopenWithEncoding("gbk");
    assert.deepEqual(await harness.messageBox(), {
      kind: "message",
      origin: { kind: "extension", extensionId: "sample", extensionName: "Sample" },
      type: "warning",
      message: "Discard unsaved changes to notes.txt?",
      detail: null,
      buttons: ["Discard", "Cancel"],
      defaultId: 1,
      cancelId: 1,
    });
    await harness.answer(1);
    await canceled;
    assert.equal(editor.text, "unsaved\n");
    assert.equal(session.format?.encoding, "utf-8");
    const discarded = session.reopenWithEncoding("gbk");
    await harness.messageBox();
    await harness.answer(0);
    await discarded;
    assert.equal(editor.text, "abc\n");
    assert.deepEqual(session.format, { encoding: "gbk", bom: false, eol: "\n" });
    await harness.flush();
    assert.equal(harness.unsaved(), false);

    // 不能编码的字符：通知并保留未保存修改。An unencodable character notifies and keeps the changes.
    editor.type("abc😀\n");
    await session.save();
    await harness.flush();
    assert.deepEqual(harness.notifications, [
      'Sample error: Could not save notes.txt: The character "😀" (U+1F600) at line 1, column 4 cannot be encoded as gbk. Use "Save with Encoding…" to choose another encoding.',
    ]);
    assert.equal(session.state, "dirty");
    assert.equal(await readFile(path, "utf8"), "abc\n");

    await session.saveWithEncoding("utf-8", true);
    assert.deepEqual(
      await readFile(path),
      Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from("abc😀\n")]),
    );
    assert.deepEqual(session.format, { encoding: "utf-8", bom: true, eol: "\n" });
    assert.equal(session.state, "clean");
  },
);

/** 内容必须是 JSON 的编辑器：载入时解析，序列化时校验。An editor whose content must be JSON. */
class JsonEditor extends StringEditor {
  public override load(content: { text: string; eol: Eol }): void {
    JSON.parse(content.text);
    super.load(content);
  }

  public override serialize(snapshot: string): string {
    JSON.parse(snapshot);
    return snapshot;
  }
}

/** `JSON.parse` 对这段文字给出的错误信息。The error message `JSON.parse` gives for the text. */
function jsonError(text: string): string {
  try {
    JSON.parse(text);
  } catch (error) {
    return (error as Error).message;
  }
  throw new Error(`${text} is valid JSON.`);
}

test(
  "an adapter that cannot load the file enters Failed, and one that cannot serialize keeps the changes",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    await writeFile(harness.path("broken.txt"), "{broken\n");
    const broken = new JsonEditor();
    const failed = harness.api.documents.openText({
      file: harness.file("broken.txt"),
      adapter: broken,
      saveShortcut: false,
    });
    broken.session = failed;
    await until(() => failed.state !== "loading", "broken.txt to load");
    assert.equal(failed.state, "failed");
    assert.equal(failed.error?.message, jsonError("{broken\n"));
    failed.dispose();

    await writeFile(harness.path("data.txt"), "[1]\n");
    const { editor, session } = await openText(harness, "data.txt", new JsonEditor());
    editor.type("[1,\n");
    await session.save();
    await harness.flush();
    assert.deepEqual(harness.notifications, [
      `Sample error: Could not save data.txt: ${jsonError("[1,\n")}`,
    ]);
    assert.equal(session.state, "dirty");
    assert.equal(await readFile(harness.path("data.txt"), "utf8"), "[1]\n");
  },
);

/** 选定的调用会抛出错误的编辑器。An editor whose chosen calls throw. */
class FailingEditor extends StringEditor {
  public failSnapshot = false;
  public failEol = false;
  /** 与这段文字对比时抛出错误。Comparing with this text throws. */
  public failComparisonWith: string | null = null;

  public override snapshot(): string {
    if (this.failSnapshot) throw new Error("No snapshot.");
    return super.snapshot();
  }

  public eol(): Eol {
    if (this.failEol) throw new Error("No line ending.");
    return "\n";
  }

  public override matches(snapshot: string): boolean {
    if (snapshot === this.failComparisonWith) throw new Error("Cannot compare.");
    return super.matches(snapshot);
  }
}

test(
  "adapter calls that throw after a load, while capturing or after a write end the operation and the session goes on",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "one\n");

    // 载入后取不到快照：进入 Failed 并给出错误。No snapshot after the load: Failed with the error.
    const editor = new FailingEditor();
    editor.failSnapshot = true;
    const session = harness.api.documents.openText({
      file: harness.file("notes.txt"),
      adapter: editor,
      saveShortcut: false,
    });
    editor.session = session;
    await until(() => session.state !== "loading", "notes.txt to load");
    assert.equal(session.state, "failed");
    assert.equal(session.error?.message, "No snapshot.");
    editor.failSnapshot = false;
    await session.reload();
    assert.equal(session.state, "clean");

    // 保存时取不到换行符：保存失败并通知，修改保留，文件不变。No line ending while saving: the save fails and notifies.
    editor.type("two\n");
    editor.failEol = true;
    await session.save();
    await harness.flush();
    assert.deepEqual(harness.notifications, [
      "Sample error: Could not save notes.txt: No line ending.",
    ]);
    assert.equal(session.state, "dirty");
    assert.equal(await readFile(path, "utf8"), "one\n");
    editor.failEol = false;
    await session.save();
    assert.equal(session.state, "clean");
    assert.equal(await readFile(path, "utf8"), "two\n");

    // 写入后不能与写入的快照对比：按保存失败处理，基线不变。The written snapshot cannot be compared: a failed save.
    editor.type("three\n");
    editor.failComparisonWith = "three\n";
    await session.save();
    await harness.flush();
    assert.deepEqual(harness.notifications.slice(1), [
      "Sample error: Could not save notes.txt: Cannot compare.",
    ]);
    assert.equal(session.state, "dirty");
    editor.failComparisonWith = null;
    editor.type("two\n");
    assert.equal(session.state, "clean");
  },
);

test(
  "opening a document and the onShow that follows read the file once",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    await writeFile(harness.path("notes.txt"), "one\n");
    const editor = new StringEditor();
    const session = harness.api.documents.openText({
      file: harness.file("notes.txt"),
      adapter: editor,
    });
    editor.session = session;
    // 宿主在 onLoad 之后发送 onShow，编辑器在其中调用 show。The host sends onShow after onLoad; editors call show there.
    await session.show(harness.file("notes.txt"));
    assert.equal(session.state, "clean");
    assert.equal(editor.text, "one\n");
    assert.deepEqual(harness.fileOperations, ["readFile"]);
  },
);

test(
  "disposing during a write or a message box ends the call and ignores the result",
  DOCUMENT_TEST,
  async () => {
    const harness = await createDocumentHost();
    const path = harness.path("notes.txt");
    await writeFile(path, "one\n");

    // 写入已提交、回复还没有到达时 dispose。Disposed after the write committed and before its reply arrives.
    const writing = await openText(harness, "notes.txt");
    writing.editor.type("two\n");
    const hold = harness.holdWrites();
    const saving = writing.session.save();
    await hold.held;
    assert.equal(writing.session.state, "saving");
    // 写入期间的输入不对比内容。Edits during the write compare nothing.
    const comparisons = writing.editor.comparisons;
    writing.editor.type("three\n");
    writing.editor.type("two\n");
    assert.equal(writing.editor.comparisons, comparisons);
    let writingChanges = 0;
    writing.session.onDidChange(() => (writingChanges += 1));
    writing.session.dispose();
    await saving;
    hold.release();
    await harness.flush();
    assert.equal(writing.session.state, "saving");
    assert.equal(writing.editor.text, "two\n");
    assert.equal(writingChanges, 0);
    assert.equal(await readFile(path, "utf8"), "two\n");

    // 冲突对话框显示期间 dispose；之后选择 Overwrite 不再读取或写入。Disposed while the conflict dialog shows.
    const asking = await openText(harness, "notes.txt");
    asking.editor.type("mine\n");
    await writeFile(path, "theirs\n");
    const conflicted = asking.session.save();
    await harness.messageBox();
    assert.equal(asking.session.state, "conflict");
    let askingChanges = 0;
    asking.session.onDidChange(() => (askingChanges += 1));
    asking.session.dispose();
    await conflicted;
    const operations = harness.fileOperations.length;
    await harness.answer(0);
    await harness.flush();
    assert.equal(asking.session.state, "conflict");
    assert.equal(asking.editor.text, "mine\n");
    assert.equal(askingChanges, 0);
    assert.equal(harness.fileOperations.length, operations);
    assert.equal(await readFile(path, "utf8"), "theirs\n");
    assert.deepEqual(harness.notifications, []);
  },
);
