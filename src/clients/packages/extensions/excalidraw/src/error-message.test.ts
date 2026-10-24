import assert from "node:assert/strict";
import test from "node:test";

import { createExtensionApi, createExtensionError, RpcEndpoint } from "@agw/extension-sdk";

import { notifyFailure } from "./error-message";

// SDK 的 createExtensionApi 经 Node 的 MessageChannel 调用宿主端的 RPC 处理函数，宿主端记录通知。The SDK's
// createExtensionApi calls a host-side RPC handler over a Node MessageChannel; the host records notifications.
function connectHost(notifications: unknown[]) {
  const channel = new MessageChannel();
  const host = new RpcEndpoint(channel.port2, {
    request: async (method, params) => {
      if (method !== "ui.notify") throw new Error(`Unexpected RPC ${method}`);
      notifications.push(params);
      return undefined;
    },
  });
  const page = new RpcEndpoint(channel.port1, {});
  const api = createExtensionApi(
    page,
    {
      frame: { frameId: "frame-1", kind: "fileViewer", viewId: "excalidraw:editor", params: null },
      manifest: { manifestVersion: 1, id: "excalidraw", name: "Excalidraw", version: "1.0.0" },
      context: {
        app: { version: "0.1.0", platform: "darwin", architecture: "arm64" },
        extension: {
          id: "excalidraw",
          version: "1.0.0",
          manifestVersion: 1,
          storageUri: "file:///storage/excalidraw/",
        },
        server: { id: "local", name: "Local", kind: "local" },
        workspace: {
          sidebarVisible: true,
          activeSidebarViewId: "agw.files",
          activeMainViewId: "agw.chat",
        },
      },
      theme: { id: "default", title: "Default", kind: "light", tokens: {} },
      themeStyleId: null,
    },
    new Map(),
    ":root { --spacing: 0.25rem; }",
  );
  return {
    api,
    close() {
      page.close();
      host.close();
    },
  };
}

test("notifyFailure shows a rejected operation as an error notification and stays silent on a cancel", async () => {
  const notifications: unknown[] = [];
  const { api, close } = connectHost(notifications);
  notifyFailure(api, Promise.reject(new Error("The document session is disposed.")));
  notifyFailure(api, Promise.reject(createExtensionError("ERR_CANCELED", "Canceled.")));
  notifyFailure(api, Promise.resolve());
  // 让 RPC 往返完成。Lets the RPC round trips finish.
  for (let round = 0; round < 5; round += 1) await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(notifications, [
    { type: "error", message: "The document session is disposed." },
  ]);
  close();
});
