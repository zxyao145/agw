import assert from "node:assert/strict";
import { MessageChannel } from "node:worker_threads";
import test from "node:test";

import type { ExtensionWelcome } from "../api";
import { RpcEndpoint } from "../rpc";
import { createExtensionApi } from "./api";

const welcome = {
  frame: { frameId: "frame-a", kind: "mainView", viewId: "calendar:app", params: null },
  manifest: {
    manifestVersion: 2,
    id: "01a173c6-4933-766f-8182-77f77ba5b946",
    name: "calendar",
    displayName: "Calendar",
    version: "1.0.0",
  },
  context: {
    app: { version: "1.0.0", platform: "darwin", architecture: "arm64" },
    extension: {
      id: "01a173c6-4933-766f-8182-77f77ba5b946",
      version: "1.0.0",
      manifestVersion: 2,
      storageUri: "file:///extensions/calendar",
    },
    server: { id: "profile-a", serverId: "server-a", name: "Server A", kind: "remote" },
    user: null,
    project: null,
    conversation: null,
    target: null,
    workspace: {
      sidebarVisible: true,
      activeSidebarViewId: "agw.conversations",
      activeMainViewId: "agw.chat",
    },
  },
  theme: { id: "default", title: "Default", kind: "light", tokens: {} },
  themeStyleId: null,
} satisfies ExtensionWelcome;

test("workspace calls send tab options and default file options", async (t) => {
  const channel = new MessageChannel();
  const requests: unknown[] = [];
  const host = new RpcEndpoint(channel.port1 as never, {
    request: (method, params) => {
      requests.push({ method, params });
      return null;
    },
  });
  const client = new RpcEndpoint(channel.port2 as never, { request: () => null });
  t.after(() => {
    client.close();
    host.close();
  });
  const api = createExtensionApi(client, welcome, new Map(), "");
  for (const params of [() => "value", Symbol("value"), { [Symbol()]: 1 }]) {
    await assert.rejects(api.workspace.openMainView("file", params), {
      code: "ERR_INVALID_ARGUMENT",
    });
  }
  await assert.rejects(
    api.workspace.openFile("agw-cloud://local/a", { preview: (() => true) as never }),
    { code: "ERR_INVALID_ARGUMENT" },
  );
  await api.workspace.openMainView("file", { a: 1 }, { key: "k", preview: true });
  await api.workspace.openFile("agw-cloud://local/a");
  await api.workspace.openMainView("agw.jobs", null, {
    key: undefined,
    title: undefined,
    preview: undefined,
  });
  await api.workspace.openFile("agw-cloud://local/b", { preview: undefined });
  assert.deepEqual(requests, [
    {
      method: "workspace.openMainView",
      params: { id: "file", params: { a: 1 }, options: { key: "k", preview: true } },
    },
    { method: "workspace.openFile", params: { uri: "agw-cloud://local/a", options: {} } },
    { method: "workspace.openMainView", params: { id: "agw.jobs", params: null, options: {} } },
    { method: "workspace.openFile", params: { uri: "agw-cloud://local/b", options: {} } },
  ]);
});

test("server.request reaches the Desktop host with method, query, body and abort signal", async () => {
  const channel = new MessageChannel();
  let request: { method: string; params: unknown } | null = null;
  const host = new RpcEndpoint(channel.port1 as never, {
    request: (method, params) => {
      request = { method, params };
      return { saved: true };
    },
  });
  const client = new RpcEndpoint(channel.port2 as never, { request: () => null });
  const api = createExtensionApi(client, welcome, new Map(), "");

  const result = await api.server.request("appointments", {
    method: "POST",
    query: { date: "2026-11-06" },
    body: { title: "Meeting" },
  });

  assert.deepEqual(result, { saved: true });
  assert.deepEqual(request, {
    method: "server.request",
    params: {
      path: "appointments",
      method: "POST",
      query: { date: "2026-11-06" },
      body: { title: "Meeting" },
    },
  });
  client.close();
  host.close();
});
