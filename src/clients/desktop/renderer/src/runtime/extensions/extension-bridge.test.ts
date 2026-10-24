import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test, { after } from "node:test";
import { pathToFileURL } from "node:url";

import iconv from "iconv-lite";
import { JSDOM } from "jsdom";

import { configureApiRuntime } from "@agw/api";
import {
  type ApiRequestRecord,
  type ApiRoutes,
  rawApiResponse,
  startApiServer,
} from "@agw/test-harness";
import {
  type BinaryDocumentAdapter,
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
  RpcEndpoint,
  type RpcPort,
  type TextDocument,
  type TextDocumentAdapter,
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
import { ExtensionBridgeBroker, type ExtensionTextCodec } from "./extension-bridge-broker";
import { ExtensionHost, type HostConfirmation } from "./extension-host";
import {
  createMainProcess,
  sampleExtension,
  startExtensionHost,
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
    api: createExtensionApi(endpoint, welcomeData, router),
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
};

async function createHarness(permissions: ExtensionPermission[], options: HarnessOptions = {}) {
  const linkRequests: string[] = [];
  const unsaved: [string, string, boolean][] = [];
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
  const execution = new ExecutionService({
    context,
    deliver: () => undefined,
    refreshConversations: () => undefined,
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
    extension: () => ({
      manifest,
      permissions,
      hostPermissions: parseHostPermissions([]),
      storageUri: "file:///data/sample",
    }),
    theme: () => ({
      id: "default",
      title: "Default",
      kind: "dark",
      tokens: { "--background": "#000" },
    }),
    isFrameClosing: () => false,
    insertComposerText: () => undefined,
    notify: () => undefined,
    onPong: () => undefined,
    openFile: async (extensionId, uri) => {
      if (!options.openFile) throw new Error("openFile is not part of this test.");
      await options.openFile(extensionId, uri);
    },
    setUnsavedChanges: (frameId, pageId, value) => unsaved.push([frameId, pageId, value]),
    confirmOpenExternal: async (_extensionId, url) => {
      linkRequests.push(url);
      return options.openLink === true;
    },
    // 确认队列属于 ExtensionHost，在 extension-host.test.ts 中测试。The queue belongs to ExtensionHost and is tested there.
    showMessageBox: () => {
      throw new Error("The confirmation queue is not part of this test.");
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
    key,
    broker,
    linkRequests,
    unsaved,
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
    /** 等到宿主显示消息框，返回去掉 id 的内容。Waits for the host's message box and returns it without its id. */
    messageBox: async () => {
      const confirmation = await until(
        () => host.getSnapshot().confirmation ?? undefined,
        "the host's message box",
      );
      assert.equal(confirmation.kind, "message");
      const { id, ...rest } = confirmation as Extract<HostConfirmation, { kind: "message" }>;
      assert.ok(Number.isInteger(id));
      return rest;
    },
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
  extensionName: "Sample",
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
    harness.host.resolveConfirmation(2);
    await canceled;
    assert.equal(session.state, "dirty");
    assert.equal(await readFile(path, "utf8"), "theirs\n");
    await harness.flush();
    assert.equal(harness.unsaved(), true);

    // Overwrite：以重新读取的内容哈希写入同一份内容。Overwrite writes the same content under the hash read again.
    const overwritten = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    harness.host.resolveConfirmation(0);
    await overwritten;
    assert.equal(await readFile(path, "utf8"), "mine\n");
    assert.equal(session.state, "clean");

    // 文件已被删除：Overwrite 重新创建它。The file was deleted: Overwrite creates it again.
    editor.type("mine again\n");
    await rm(path);
    const recreated = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    harness.host.resolveConfirmation(0);
    await recreated;
    assert.equal(await readFile(path, "utf8"), "mine again\n");
    assert.equal(session.state, "clean");

    // Reload：读取磁盘上的内容，未保存标记清除。Reload reads the disk content and clears the flag.
    editor.type("discarded\n");
    await writeFile(path, "from disk\n");
    const reloaded = session.save();
    assert.deepEqual(await harness.messageBox(), CONFLICT_BOX);
    harness.host.resolveConfirmation(1);
    await reloaded;
    assert.equal(editor.text, "from disk\n");
    assert.equal(session.state, "clean");
    await harness.flush();
    assert.equal(harness.unsaved(), false);
    assert.equal(harness.host.getSnapshot().confirmation, null);
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
    harness.host.resolveConfirmation(2);
    await saving;
    assert.equal(await readFile(path, "utf8"), "three\n");
    assert.equal(session.changedOnDisk, true);
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
  assert.equal(harness.host.getSnapshot().confirmation, null);
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
      extensionName: "Sample",
      type: "warning",
      message: "Discard unsaved changes to notes.txt?",
      detail: null,
      buttons: ["Discard", "Cancel"],
      defaultId: 1,
      cancelId: 1,
    });
    harness.host.resolveConfirmation(1);
    await canceled;
    assert.equal(editor.text, "unsaved\n");
    assert.equal(session.format?.encoding, "utf-8");
    const discarded = session.reopenWithEncoding("gbk");
    await harness.messageBox();
    harness.host.resolveConfirmation(0);
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
    harness.host.resolveConfirmation(0);
    await harness.flush();
    assert.equal(asking.session.state, "conflict");
    assert.equal(asking.editor.text, "mine\n");
    assert.equal(askingChanges, 0);
    assert.equal(harness.fileOperations.length, operations);
    assert.equal(await readFile(path, "utf8"), "theirs\n");
    assert.deepEqual(harness.notifications, []);
  },
);
