// 测试用：SDK 的 `createExtensionApi` 与 `RpcEndpoint` 原样经 Node 的 MessageChannel 调用测试给出的宿主端 RPC 处理函数。
// For tests: the real SDK `createExtensionApi` and `RpcEndpoint` call a host-side RPC handler given by the test over a
// Node MessageChannel.
import {
  createExtensionApi,
  RpcEndpoint,
  type ExtensionApi,
  type RpcRequestHandler,
} from "@agw/extension-sdk";

export type TestHost = {
  api: ExtensionApi;
  /** 页面一端的端口。The page end of the channel. */
  pagePort: MessagePort;
  close(): void;
};

export function connectTestHost(request: RpcRequestHandler): TestHost {
  const channel = new MessageChannel();
  const host = new RpcEndpoint(channel.port2, { request });
  const page = new RpcEndpoint(channel.port1, {});
  const api = createExtensionApi(
    page,
    {
      frame: {
        frameId: "frame-1",
        kind: "fileViewer",
        viewId: "text-editor:preview",
        params: null,
      },
      manifest: { manifestVersion: 1, id: "text-editor", name: "Text Editor", version: "1.0.0" },
      context: {
        app: { version: "0.1.0", platform: "darwin", architecture: "arm64" },
        extension: {
          id: "text-editor",
          version: "1.0.0",
          manifestVersion: 1,
          storageUri: "file:///storage/text-editor/",
        },
        server: { id: "local", name: "Local", kind: "local" },
        workspace: {
          sidebarVisible: true,
          activeSidebarViewId: "agw.files",
          activeMainViewId: "agw.chat",
        },
      },
      theme: { kind: "light", tokens: {} },
    },
    new Map(),
  );
  return {
    api,
    pagePort: channel.port1,
    close() {
      page.close();
      host.close();
    },
  };
}
