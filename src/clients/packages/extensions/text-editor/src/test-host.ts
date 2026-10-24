// 测试用：SDK 的 `createExtensionApi` 与 `RpcEndpoint` 原样经 Node 的 MessageChannel 调用测试给出的宿主端 RPC 处理函数。
// For tests: the real SDK `createExtensionApi` and `RpcEndpoint` call a host-side RPC handler given by the test over a
// Node MessageChannel.
import {
  createExtensionApi,
  RpcEndpoint,
  type EventRouter,
  type ExtensionApi,
  type RpcRequestHandler,
  type ThemeChangedNotification,
  type ThemeInfo,
} from "@agw/extension-sdk";

export type TestHost = {
  api: ExtensionApi;
  /** 页面一端的端口。The page end of the channel. */
  pagePort: MessagePort;
  /**
   * 宿主推送主题变化（`theme.changed` 事件），与宿主 broker 相同地比较样式标识；CSS 主题带 `cssText` 与 `styleId`。
   * The host pushes a theme change (the `theme.changed` event), comparing style ids as the host broker does; CSS
   * themes carry `cssText` and `styleId`.
   */
  changeTheme(theme: ThemeInfo, styleId?: string | null): void;
  close(): void;
};

export function connectTestHost(request: RpcRequestHandler): TestHost {
  const channel = new MessageChannel();
  const host = new RpcEndpoint(channel.port2, { request });
  // 页面一端把宿主事件交给 API 的监听器，与 SDK 的 connection 相同。
  // The page end hands host events to the API's listeners, as the SDK connection does.
  const router: EventRouter = new Map();
  const page = new RpcEndpoint(channel.port1, {
    event: (name, data) => {
      for (const listener of router.get(name) ?? []) listener(data);
    },
  });
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
          storageUri: "file:///storage/text-editor",
        },
        server: { id: "local", name: "Local", kind: "local" },
        workspace: {
          sidebarVisible: true,
          activeSidebarViewId: "agw.files",
          activeMainViewId: "agw.chat",
        },
      },
      theme: {
        id: "default",
        title: "Default",
        kind: "light",
        tokens: {
          "--agw-font-size-base": "16px",
        },
      },
      themeStyleId: null,
    },
    router,
    ":root { --spacing: 0.25rem; }",
  );
  let sentStyleId: string | null = null;
  return {
    api,
    pagePort: channel.port1,
    changeTheme(theme, styleId = null) {
      const { cssText, ...rest } = theme;
      const style: ThemeChangedNotification["style"] =
        styleId === sentStyleId
          ? { operation: "unchanged", styleId }
          : styleId !== null && cssText !== undefined
            ? { operation: "replace", styleId, cssText }
            : { operation: "clear", styleId: null };
      sentStyleId = styleId;
      const notification: ThemeChangedNotification = { theme: rest, style };
      host.emit("theme.changed", notification);
    },
    close() {
      page.close();
      host.close();
    },
  };
}
