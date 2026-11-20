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
