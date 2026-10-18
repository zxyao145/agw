import assert from "node:assert/strict";
import test, { after } from "node:test";

import { configureApiRuntime } from "@agw/api";
import { rawApiResponse, startApiServer } from "@agw/test-harness";
import {
  createExtensionApi,
  createExtensionError,
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
} from "@agw/extension-sdk";
import { parseHostPermissions } from "@desktop/shared/extension-host-permissions";
import { A2AService } from "./a2a-service";
import { CloudAccessGrants, CloudFileSystem, formatCloudUri } from "./cloud-file-system";
import { ContextService, connectionKeyOf } from "./context-service";
import { ExtensionBridgeBroker } from "./extension-bridge-broker";
import { ExecutionService } from "./execution-service";
import { WorkspaceLayoutStore } from "./workspace-layout-store";
import { WorkspaceRegistry } from "./workspace-registry";

const PROJECT = "0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e";

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
};

async function createHarness(permissions: ExtensionPermission[], options: HarnessOptions = {}) {
  const linkRequests: string[] = [];
  const unsaved: [string, string, boolean][] = [];
  const api = await startApiServer({
    "GET /api/files/download": rawApiResponse({
      headers: { "content-type": "application/octet-stream" },
      body: new Uint8Array([7, 8, 9]),
    }),
    "PUT /api/files/write": { name: "b.bin", path: "b.bin", type: "file" },
  });
  configureApiRuntime({ baseUrl: api.baseUrl, token: "agw_secret" });
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
  const identity = { profileId: "local", baseUrl: api.baseUrl, token: "agw_secret" };
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
    bridge: () => {
      throw new Error("The main-process bridge is not part of this test.");
    },
    hostInstanceId: "host-1",
    context,
    layout,
    cloud: new CloudFileSystem(context, new CloudAccessGrants()),
    execution,
    a2a: new A2AService(context),
    extension: () => ({
      manifest,
      permissions,
      hostPermissions: parseHostPermissions([]),
      storageUri: "file:///data/sample",
    }),
    theme: () => ({ kind: "dark", tokens: { "--background": "#000" } }),
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
  after(() => {
    broker.disconnect("frame-1");
    sdk.endpoint.close();
  });
  return {
    ...sdk,
    context,
    key,
    broker,
    linkRequests,
    unsaved,
    uri: (path: string) =>
      formatCloudUri({ serverId: "local", projectId: PROJECT, directoryId: null, path }),
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
