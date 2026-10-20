// ExtensionHost 测试共用的主进程一侧：在测试进程中回答宿主用到的 bridge 通道，并像 preload 一样转交端口。
// The main-process side shared by ExtensionHost tests: answers the bridge channels the host uses inside the test
// process and hands ports over the way the preload does.
import {
  createExtensionApi,
  type EventRouter,
  type ExtensionApi,
  type ExtensionFileViewerContribution,
  type ExtensionWelcome,
  RpcEndpoint,
  type RpcPort,
} from "@agw/extension-sdk";
import type {
  AgwDesktopBridge,
  CreateExtensionFrameRequest,
  DesktopRuntimeState,
  ExtensionEvent,
  ExtensionPortMessage,
  InstalledExtension,
} from "@desktop/shared/contracts";
import type { DOMWindow } from "jsdom";
import type { ExtensionHost } from "./extension-host";

/** 测试中的 extension：id 为 `sample`、名称为 `Sample`，提供给定的文件查看器。The sample extension of the tests. */
export function sampleExtension(
  fileViewers: ExtensionFileViewerContribution[],
): InstalledExtension {
  return {
    id: "sample",
    source: "user",
    directory: "/extensions/sample",
    manifest: {
      manifestVersion: 1,
      id: "sample",
      name: "Sample",
      version: "1.0.0",
      contributes: { fileViewers },
    },
    validation: "valid",
    errors: [],
    enabled: true,
    permissionApprovalRequired: false,
    grantedPermissions: [],
    grantedHostPermissions: [],
    lastActivatedVersion: "1.0.0",
    icons: {},
  };
}

/** extension 页面一侧：SDK 的端点与 API，记录宿主调用的钩子。The page side: the SDK endpoint and API, recording host hooks. */
export type ExtensionPage = {
  calls: { method: string; params: unknown }[];
  api: Promise<ExtensionApi>;
  endpoint: RpcEndpoint;
};

function openExtensionPage(port: MessagePort): ExtensionPage {
  const calls: ExtensionPage["calls"] = [];
  const router: EventRouter = new Map();
  const welcome = new Promise<ExtensionWelcome>((resolve) =>
    router.set("welcome", new Set([(data) => resolve(data as ExtensionWelcome)])),
  );
  const endpoint = new RpcEndpoint(port as unknown as RpcPort, {
    request: (method, params) => {
      calls.push({ method, params });
      return null;
    },
    event: (name, data) => {
      for (const listener of router.get(name) ?? []) listener(data);
    },
  });
  const api = welcome.then((data) => {
    router.delete("welcome");
    return createExtensionApi(endpoint, data, router);
  });
  return { calls, api, endpoint };
}

/**
 * 主进程一侧：创建 frame 时打开 MessageChannel，一端连接页面，另一端像 preload 一样以 `agw:extension-port` 消息交给
 * 宿主页面；销毁 frame 时关闭页面。`channels` 补充或替换 bridge 通道。
 * The main-process side: creating a frame opens a MessageChannel, connects one end to a page and hands the other to
 * the host page with an `agw:extension-port` message the way the preload does; destroying a frame closes the page.
 * `channels` adds or replaces bridge channels.
 */
export function createMainProcess(
  window: DOMWindow,
  options: {
    installed: InstalledExtension;
    storageDirectory: string;
    channels?: Partial<AgwDesktopBridge>;
  },
) {
  const frames: CreateExtensionFrameRequest[] = [];
  const pages = new Map<string, ExtensionPage>();
  /** 每个 frame 当前页面的 pageId。The current page id of each frame. */
  const pageIds = new Map<string, string>();
  const destroyed: string[] = [];
  let eventListener: ((event: ExtensionEvent) => void) | null = null;
  const state: DesktopRuntimeState = {
    isDesktop: true,
    platform: "darwin",
    architecture: "arm64",
    appVersion: "0.5.0",
    packageFlavor: "full",
    settings: {
      schemaVersion: 1,
      packageFlavor: "full",
      closeBehavior: "minimize-to-tray",
      profiles: [],
      activeServerId: "local",
      projectTabsByServer: {},
      extensions: {},
      removedBundledExtensions: [],
      fileViewerDefaults: {},
    },
    activeToken: null,
    localServerRuntime: null,
    extensionSafeMode: null,
  };
  const bridge: Partial<AgwDesktopBridge> = {
    getRuntimeState: async () => state,
    attachExtensionHost: async () => undefined,
    onExtensionEvent: (listener) => {
      eventListener = listener;
      return () => undefined;
    },
    listExtensions: async () => [options.installed],
    listExtensionRecoveryNotices: async () => [],
    setExtensionFrameBackground: async () => undefined,
    beginExtensionActivation: async () => ({ storageDirectory: options.storageDirectory }),
    recordExtensionActivation: async () => state,
    endExtensionActivation: async () => undefined,
    createExtensionFrame: async (_hostInstanceId, request) => {
      frames.push(request);
      const channel = new MessageChannel();
      pages.set(request.frameId, openExtensionPage(channel.port2));
      const message: ExtensionPortMessage = {
        type: "agw:extension-port",
        extensionId: request.extensionId,
        frameId: request.frameId,
        pageId: crypto.randomUUID(),
      };
      pageIds.set(request.frameId, message.pageId);
      // 页面加载后 preload 才转交端口。The preload hands the port over once the page has loaded.
      setTimeout(() =>
        window.dispatchEvent(
          new window.MessageEvent("message", {
            data: message,
            source: window as unknown as Window,
            ports: [channel.port1] as unknown as MessagePort[],
          }),
        ),
      );
    },
    destroyExtensionFrame: async (_hostInstanceId, frameId) => {
      destroyed.push(frameId);
      pages.get(frameId)?.endpoint.close();
    },
    releaseExtensionFrameResources: async () => undefined,
    ...options.channels,
  };
  return {
    bridge: bridge as AgwDesktopBridge,
    frames,
    pages,
    pageIds,
    destroyed,
    /** 像主进程一样推送事件。Pushes an event the way the main process does. */
    emit: (event: ExtensionEvent) => eventListener!(event),
  };
}

/**
 * 启动宿主。心跳计时器建在 jsdom window 上，关闭 window 时一并清除，测试进程才能结束。
 * Starts the host. Its heartbeat timer is created on the jsdom window, so closing the window clears it and the test
 * process can exit.
 */
export async function startExtensionHost(host: ExtensionHost, window: DOMWindow): Promise<void> {
  const nodeSetInterval = Object.getOwnPropertyDescriptor(globalThis, "setInterval")!;
  Object.defineProperty(globalThis, "setInterval", {
    configurable: true,
    writable: true,
    value: window.setInterval.bind(window),
  });
  try {
    await host.start();
  } finally {
    Object.defineProperty(globalThis, "setInterval", nodeSetInterval);
  }
}
