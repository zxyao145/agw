import type { BackgroundDefinition, ExtensionApi, ExtensionWelcome, ViewDefinition } from "../api";
import { createExtensionError, RpcEndpoint, type RpcPort } from "../rpc";
import { createExtensionApi, type EventRouter } from "./api";

/** SDK 对应的 Extension API 版本。The Extension API version of this SDK. */
export const SDK_MANIFEST_VERSION = 1;

type Connection = {
  endpoint: RpcEndpoint;
  api: ExtensionApi;
  welcome: ExtensionWelcome;
};

const definitions: { background: BackgroundDefinition | null; view: ViewDefinition | null } = {
  background: null,
  view: null,
};
let connection: Promise<Connection> | null = null;

export function registerBackground(definition: BackgroundDefinition): void {
  definitions.background = definition;
}

export function registerView(definition: ViewDefinition): void {
  definitions.view = definition;
}

/** 等待 preload 转交的端口。Waits for the port the preload hands over. */
function receivePort(): Promise<MessagePort> {
  return new Promise((resolve) => {
    const onMessage = (event: MessageEvent) => {
      const data = event.data as { type?: unknown } | null;
      if (
        event.source !== window ||
        data?.type !== "agw-extension/port" ||
        event.ports.length !== 1
      )
        return;
      window.removeEventListener("message", onMessage);
      resolve(event.ports[0]);
    };
    window.addEventListener("message", onMessage);
    window.postMessage(
      { type: "agw-extension/hello", manifestVersion: SDK_MANIFEST_VERSION },
      location.origin,
    );
  });
}

async function handleHostRequest(
  api: () => ExtensionApi,
  method: string,
  params: unknown,
): Promise<unknown> {
  const { background, view } = definitions;
  switch (method) {
    case "lifecycle.onInstalled":
      await background?.onInstalled?.(
        params as Parameters<NonNullable<BackgroundDefinition["onInstalled"]>>[0],
        api(),
      );
      return null;
    case "lifecycle.activate":
      await background?.activate?.(api());
      return null;
    case "lifecycle.deactivate":
      await background?.deactivate?.();
      return null;
    case "commands.execute": {
      const { command, args } = params as { command: string; args: unknown };
      const handler = background?.commands?.[command];
      if (!handler)
        throw createExtensionError("ERR_UNKNOWN_COMMAND", `Command "${command}" is not defined.`);
      await handler(args, api());
      return null;
    }
    case "view.onLoad":
      await view?.onLoad?.(api(), (params as { params: unknown }).params);
      return null;
    case "view.onShow":
      view?.onShow?.((params as { params: unknown }).params);
      return null;
    case "view.onHide":
      view?.onHide?.();
      return null;
    case "view.onUnload":
      await view?.onUnload?.();
      return null;
    default:
      throw createExtensionError("ERR_UNKNOWN_METHOD", `Unknown method: ${method}`);
  }
}

function forwardUncaughtErrors(endpoint: RpcEndpoint): void {
  const report = (message: string, stack: string | null) => {
    void endpoint.request("log.error", { message, stack }).catch(() => undefined);
  };
  window.addEventListener("error", (event) => {
    report(event.message, event.error instanceof Error ? (event.error.stack ?? null) : null);
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason as unknown;
    report(
      reason instanceof Error ? reason.message : String(reason),
      reason instanceof Error ? (reason.stack ?? null) : null,
    );
  });
}

/** 建立连接并返回 API；同一 frame 中多次调用返回同一个连接。Connects and returns the API; repeated calls share one connection. */
export function connect(): Promise<ExtensionApi> {
  connection ??= (async () => {
    const port = await receivePort();
    const router: EventRouter = new Map();
    let api: ExtensionApi | null = null;
    const welcome = new Promise<ExtensionWelcome>((resolve) => {
      router.set("welcome", new Set([(data) => resolve(data as ExtensionWelcome)]));
    });
    const endpoint = new RpcEndpoint(port as unknown as RpcPort, {
      request: (method, params) => {
        if (!api)
          throw createExtensionError("ERR_NOT_READY", "The extension is not connected yet.");
        const current = api;
        return handleHostRequest(() => current, method, params);
      },
      event: (name, data) => {
        for (const listener of router.get(name) ?? []) listener(data);
      },
    });
    const welcomeData = await welcome;
    router.delete("welcome");
    api = createExtensionApi(endpoint, welcomeData, router);
    forwardUncaughtErrors(endpoint);
    return { endpoint, api, welcome: welcomeData };
  })();
  return connection.then((value) => value.api);
}
