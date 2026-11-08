import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import test, { before, type TestContext } from "node:test";
import { pathToFileURL } from "node:url";
import { MessageChannel } from "node:worker_threads";

import {
  createExtensionApi,
  RpcEndpoint,
  type ExtensionWelcome,
  type HostContext,
  type RpcPort,
} from "../../packages/extension-sdk/src";
import type { DomEnvironment } from "../../packages/test-harness/src";
import { ExtensionAccessGrants } from "../../desktop/src/main/extensions/extension-access-grants";
import { ExtensionFileSystem } from "../../desktop/src/main/extensions/extension-file-system";
import { ReplacedFileRegistry } from "../../desktop/src/main/extensions/replaced-file-registry";
import { createScratchDirectory } from "../../desktop/src/main/scratch-directory.test-helper";
import type { ExtensionFileSystemRequest } from "../../desktop/src/shared/contracts";

const environmentReady = import("../../packages/test-harness/src").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);
let environment: DomEnvironment;
let settings: typeof import("../../../extensions/bare-repository/src/proxy-settings");
let form: typeof import("../../../extensions/bare-repository/src/proxy-settings-form");

before(async () => {
  environment = await environmentReady;
  settings = await import("../../../extensions/bare-repository/src/proxy-settings");
  form = await import("../../../extensions/bare-repository/src/proxy-settings-form");
});

async function createHarness(t: TestContext) {
  const scratch = await createScratchDirectory("repository-proxy");
  const storage = join(scratch.path, "storage");
  await mkdir(storage);
  const filesystem = new ExtensionFileSystem(
    new ExtensionAccessGrants(),
    new ReplacedFileRegistry(),
  );
  const { port1, port2 } = new MessageChannel();
  const router = new Map<string, Set<(value: unknown) => void>>();
  const endpoint = new RpcEndpoint(port1 as unknown as RpcPort, {
    event: (name, value) => {
      for (const listener of router.get(name) ?? []) listener(value);
    },
  });
  const host = new RpcEndpoint(port2 as unknown as RpcPort, {
    request(method, params) {
      const operation =
        method === "fs.readFile" ? "readFile" : method === "fs.writeFile" ? "writeFile" : null;
      assert.ok(operation, method);
      return filesystem.execute(
        { extensionId: "bare-repository", grantedPermissions: [], storageDirectory: storage },
        { ...(params as object), operation } as ExtensionFileSystemRequest,
      );
    },
  });
  const welcome: ExtensionWelcome = {
    frame: { frameId: "repositories", kind: "mainView", viewId: "repositories", params: {} },
    manifest: {
      manifestVersion: 1,
      id: "bare-repository",
      name: "Bare Repository",
      version: "1.0.0",
    },
    context: {
      app: { version: "1.0.0", platform: "darwin", architecture: "arm64" },
      extension: {
        id: "bare-repository",
        version: "1.0.0",
        manifestVersion: 1,
        storageUri: pathToFileURL(storage).href,
      },
      server: { id: "local", name: "Local", kind: "local" },
      project: { id: "project-a", name: "Project A", description: null, directories: [] },
      workspace: { sidebarVisible: true, activeSidebarViewId: "", activeMainViewId: "" },
    },
    theme: { id: "default", title: "Default", kind: "light", tokens: {} },
    themeStyleId: null,
  };
  const api = createExtensionApi(endpoint, welcome, router, "");
  t.after(async () => {
    endpoint.close();
    host.close();
    await scratch.dispose();
  });
  async function updateContext(context: HostContext) {
    const changed = Promise.withResolvers<void>();
    const listener = () => changed.resolve();
    router.get("context.changed")!.add(listener);
    host.emit("context.changed", context);
    await changed.promise;
    router.get("context.changed")!.delete(listener);
  }
  return {
    api,
    selectServer(id: string) {
      return updateContext({
        ...api.context.get(),
        server: { id, name: id, kind: "remote" },
      });
    },
    selectProject(id: string | null) {
      return updateContext({
        ...api.context.get(),
        project: id ? { id, name: id, description: null, directories: [] } : null,
      });
    },
  };
}

test("ProxySettingsForm_SaveReloadAndClear_PersistsThroughSdkAndFileSystem", async (t) => {
  const { api } = await createHarness(t);
  const { React, render, screen, fireEvent, waitFor } = environment;
  const project = api.context.get().project!;
  const view = render(React.createElement(form.ProxySettingsForm, { api, project }));
  screen.getByRole("region", { name: "Repository proxy" });
  screen.getByRole("heading", { name: "Repository proxy", level: 2 });
  screen.getByText("Proxy for HTTPS repositories in Project A on Local.");
  const input = screen.getByLabelText<HTMLInputElement>("Proxy URL");
  await waitFor(() => assert.equal(input.disabled, false));
  assert.equal(input.value, "");

  fireEvent.change(input, { target: { value: "socks5h://127.0.0.1:7890" } });
  fireEvent.submit(input.closest("form")!);
  await screen.findByRole("status");
  assert.equal(await settings.readProxyUrl(api), "socks5h://127.0.0.1:7890");
  view.unmount();
  render(React.createElement(form.ProxySettingsForm, { api, project }));
  const reloaded = screen.getByLabelText<HTMLInputElement>("Proxy URL");
  await waitFor(() => assert.equal(reloaded.value, "socks5h://127.0.0.1:7890"));
  fireEvent.change(reloaded, { target: { value: "" } });
  fireEvent.submit(reloaded.closest("form")!);
  await screen.findByRole("status");
  assert.equal(await settings.readProxyUrl(api), undefined);
});

test("writeProxyUrl_DifferentServers_KeepsIndependentSettings", async (t) => {
  const { api, selectServer } = await createHarness(t);
  await settings.writeProxyUrl(api, "http://127.0.0.1:7890");
  await selectServer("remote");
  assert.equal(await settings.readProxyUrl(api), undefined);
  await settings.writeProxyUrl(api, "socks5://127.0.0.1:1080");
  await selectServer("local");
  assert.equal(await settings.readProxyUrl(api), "http://127.0.0.1:7890");
});

test("writeProxyUrl_DifferentProjects_KeepsIndependentSettings", async (t) => {
  const { api, selectProject } = await createHarness(t);
  await settings.writeProxyUrl(api, "http://127.0.0.1:7890");
  await selectProject("project-b");
  assert.equal(await settings.readProxyUrl(api), undefined);
  await settings.writeProxyUrl(api, "socks5://127.0.0.1:1080");
  await selectProject("project-a");
  assert.equal(await settings.readProxyUrl(api), "http://127.0.0.1:7890");
  await selectProject("project-b");
  await settings.writeProxyUrl(api, "");
  assert.equal(await settings.readProxyUrl(api), undefined);
  await selectProject("project-a");
  assert.equal(await settings.readProxyUrl(api), "http://127.0.0.1:7890");
});

test("proxySettings_NoActiveProject_RejectsReadAndWrite", async (t) => {
  const { api, selectProject } = await createHarness(t);
  await selectProject(null);
  await assert.rejects(settings.readProxyUrl(api), /Select a Project/u);
  await assert.rejects(settings.writeProxyUrl(api, "http://127.0.0.1:7890"), /Select a Project/u);
});

test("writeProxyUrl_InvalidUrl_PreservesSavedSettings", async (t) => {
  const { api } = await createHarness(t);
  await settings.writeProxyUrl(api, "http://127.0.0.1:7890");
  for (const value of [
    "ftp://host:7890",
    "host:7890",
    "http://",
    "http://host:7890/?key=value",
    "http://lo\ncalhost:7890",
    "http://lo\u0085calhost:7890",
  ])
    await assert.rejects(
      settings.writeProxyUrl(api, value),
      /valid HTTP, HTTPS or SOCKS proxy URL/u,
    );
  assert.equal(await settings.readProxyUrl(api), "http://127.0.0.1:7890");
});

test("readProxyUrl_ServerChangesDuringRead_RejectsPreviousServerSettings", async (t) => {
  const { api, selectServer } = await createHarness(t);
  await settings.writeProxyUrl(api, "http://127.0.0.1:7890");
  const pending = assert.rejects(settings.readProxyUrl(api), /active Server or Project changed/u);
  await selectServer("remote");
  await pending;
});

test("readProxyUrl_ProjectChangesDuringRead_RejectsPreviousProjectSettings", async (t) => {
  const { api, selectProject } = await createHarness(t);
  await settings.writeProxyUrl(api, "http://127.0.0.1:7890");

  const pending = assert.rejects(settings.readProxyUrl(api), /active Server or Project changed/u);
  await selectProject("project-b");
  await pending;
});
