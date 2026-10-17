import { net, session, type Session } from "electron";

import type { ExtensionEvent } from "../../shared/contracts";
import type { HostPermission } from "../../shared/extension-host-permissions";
import type { ExtensionPermission } from "../../shared/extension-permissions";
import type { ExtensionHttpTransport, HttpHop } from "./extension-http";
import {
  EXTENSION_SCHEME,
  isRequestAllowed,
  isServiceWorkerScriptRequest,
  isWebPermissionAllowed,
} from "./extension-network-rules";
import { createExtensionProtocolHandler, type ExtensionProtocolState } from "./extension-protocol";

export type ExtensionSessionHooks = {
  sdkDirectory: string;
  protocolState(extensionId: string): ExtensionProtocolState | null;
  grantedPermissions(extensionId: string): readonly ExtensionPermission[];
  hostPermissions(extensionId: string): readonly HostPermission[];
  onRequestBlocked(event: Extract<ExtensionEvent, { type: "request-blocked" }>): void;
  onServiceWorkerRegistered(extensionId: string, scope: string): void;
};

export function extensionPartition(extensionId: string): string {
  return `persist:agw-extension-${extensionId}`;
}

/**
 * 每个 extension 独享的 session：协议、请求检查、Web 权限与 Service Worker 拦截；default session 不做修改。
 * One session per extension with the protocol, request checks, web permissions and Service Worker blocking.
 */
export class ExtensionSessionFactory {
  private readonly sessions = new Map<string, Session>();

  public constructor(private readonly hooks: ExtensionSessionHooks) {}

  public get(extensionId: string): Session {
    const existing = this.sessions.get(extensionId);
    if (existing) return existing;
    const extensionSession = session.fromPartition(extensionPartition(extensionId));
    this.configure(extensionId, extensionSession);
    this.sessions.set(extensionId, extensionSession);
    return extensionSession;
  }

  /**
   * 用该 extension 的 session 发出一次请求；`redirect: "manual"` 下不调用 followRedirect，重定向交给调用方检查。
   * Sends one request with the extension's session; under `redirect: "manual"` the redirect is not followed and the
   * caller checks it.
   */
  public httpTransportFor(extensionId: string): ExtensionHttpTransport {
    const extensionSession = this.get(extensionId);
    return ({ url, method, headers, body, signal }) =>
      new Promise<HttpHop>((resolve, reject) => {
        let redirect: { status: number; location: string } | null = null;
        const request = net.request({
          url,
          method,
          session: extensionSession,
          redirect: "manual",
          credentials: "omit",
        });
        for (const [name, value] of Object.entries(headers ?? {})) request.setHeader(name, value);
        const abort = () => request.abort();
        signal.addEventListener("abort", abort, { once: true });
        request.on("redirect", (status, _method, location) => {
          redirect = { status, location };
        });
        request.on("response", (response) => {
          resolve({
            kind: "response",
            status: response.statusCode,
            headers: Object.fromEntries(
              Object.entries(response.headers).map(([name, value]) => [
                name,
                Array.isArray(value) ? value.join(", ") : value,
              ]),
            ),
            body: response as unknown as AsyncIterable<Uint8Array>,
            cancel: abort,
          });
        });
        request.on("error", (error) => {
          signal.removeEventListener("abort", abort);
          if (redirect) resolve({ kind: "redirect", ...redirect });
          else reject(error);
        });
        request.on("abort", () => reject(new Error("The request was aborted.")));
        if (body !== undefined) request.write(typeof body === "string" ? body : Buffer.from(body));
        request.end();
      });
  }

  /** 每次激活、创建 frame 之前指向该次激活的出站代理。Points the session at this activation's egress proxy before any frame exists. */
  public async setProxy(extensionId: string, port: number): Promise<void> {
    await this.get(extensionId).setProxy({
      proxyRules: `http://127.0.0.1:${port}`,
      proxyBypassRules: "<-loopback>",
    });
    await this.get(extensionId).closeAllConnections();
  }

  /** 卸载并删除数据时清除 session 数据。Clears session data when uninstalling with data deletion. */
  public async clearData(extensionId: string): Promise<void> {
    const extensionSession =
      this.sessions.get(extensionId) ?? session.fromPartition(extensionPartition(extensionId));
    await extensionSession.clearStorageData();
    await extensionSession.clearCache();
  }

  public async clearServiceWorkers(extensionId: string): Promise<void> {
    await this.get(extensionId).clearStorageData({ storages: ["serviceworkers"] });
  }

  private configure(extensionId: string, extensionSession: Session): void {
    const { hooks } = this;
    extensionSession.protocol.handle(
      EXTENSION_SCHEME,
      createExtensionProtocolHandler({
        extensionId,
        getState: () => hooks.protocolState(extensionId),
        sdkDirectory: hooks.sdkDirectory,
        fetchFile: (fileUrl) => net.fetch(fileUrl),
      }),
    );

    extensionSession.webRequest.onBeforeRequest({ urls: ["<all_urls>"] }, (details, callback) => {
      if (isRequestAllowed(extensionId, hooks.hostPermissions(extensionId), details.url)) {
        callback({});
        return;
      }
      hooks.onRequestBlocked({
        type: "request-blocked",
        extensionId,
        url: details.url,
        resourceType: details.resourceType,
        reason: "host-not-permitted",
      });
      callback({ cancel: true });
    });

    extensionSession.webRequest.onBeforeSendHeaders(
      { urls: ["<all_urls>"] },
      (details, callback) => {
        if (!isServiceWorkerScriptRequest(details.requestHeaders)) {
          callback({ requestHeaders: details.requestHeaders });
          return;
        }
        hooks.onRequestBlocked({
          type: "request-blocked",
          extensionId,
          url: details.url,
          resourceType: details.resourceType,
          reason: "service-worker-blocked",
        });
        callback({ cancel: true });
      },
    );

    extensionSession.serviceWorkers.on("registration-completed", (_event, details) => {
      hooks.onServiceWorkerRegistered(extensionId, details.scope);
    });

    extensionSession.setPermissionRequestHandler((_contents, permission, callback) => {
      callback(isWebPermissionAllowed(permission, hooks.grantedPermissions(extensionId)));
    });
    extensionSession.setPermissionCheckHandler((_contents, permission) =>
      isWebPermissionAllowed(permission, hooks.grantedPermissions(extensionId)),
    );
    extensionSession.setDevicePermissionHandler(() => false);
    extensionSession.setDisplayMediaRequestHandler((_request, callback) => callback({}));
  }
}
