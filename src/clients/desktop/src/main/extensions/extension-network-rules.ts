import {
  type HostPermission,
  matchesConnectTarget,
  matchesHostPermissionHttpUrl,
  matchesHostPermissionUrl,
} from "../../shared/extension-host-permissions";
import type { ExtensionPermission } from "../../shared/extension-permissions";

export const EXTENSION_SCHEME = "agw-extension";

/** extension 自身 origin 的地址。A URL on the extension's own origin. */
export function isOwnOrigin(extensionId: string, value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === `${EXTENSION_SCHEME}:` && url.host === extensionId;
  } catch {
    return false;
  }
}

/**
 * `webRequest.onBeforeRequest` 的判断：自身 origin、页面内的 data/blob 资源、已批准的域名，以及 DevTools 前端。
 * DevTools 前端（Inspect）在被检查页面的 session 中加载 `devtools:` 页面；普通页面无法请求该 scheme。
 * Decision for `webRequest.onBeforeRequest`: own origin, in-page data/blob resources, approved hosts, and the
 * DevTools frontend, which Inspect loads as `devtools:` pages in the inspected page's session; web pages cannot request that scheme.
 */
export function isRequestAllowed(
  extensionId: string,
  permissions: readonly HostPermission[],
  value: string,
): boolean {
  if (isOwnOrigin(extensionId, value)) return true;
  if (value.startsWith("devtools:")) return true;
  if (value.startsWith("data:")) return true;
  if (value.startsWith("blob:")) return isOwnOrigin(extensionId, value.slice("blob:".length));
  return matchesHostPermissionUrl(permissions, value);
}

/** 出站代理 `CONNECT host:port`。Egress-proxy `CONNECT host:port`. */
export function isConnectAllowed(
  permissions: readonly HostPermission[],
  host: string,
  port: number,
): boolean {
  return matchesConnectTarget(permissions, host, port);
}

/** 出站代理收到的绝对地址 HTTP 请求只匹配 `http` 项。Absolute-URL HTTP requests at the proxy only match `http` entries. */
export function isProxyHttpRequestAllowed(
  permissions: readonly HostPermission[],
  value: string,
): boolean {
  return value.startsWith("http:") && matchesHostPermissionUrl(permissions, value);
}

/** `will-navigate` / `will-frame-navigate` 的判断。Decision for `will-navigate` / `will-frame-navigate`. */
export function isNavigationAllowed(
  extensionId: string,
  permissions: readonly HostPermission[],
  value: string,
  isMainFrame: boolean,
): boolean {
  if (isOwnOrigin(extensionId, value)) return true;
  return !isMainFrame && matchesHostPermissionHttpUrl(permissions, value);
}

export type UpstreamProxy =
  | { type: "direct" }
  | { type: "http"; host: string; port: number; secure: boolean }
  | { type: "unsupported"; value: string };

/** 解析 `session.resolveProxy()` 的结果，取第一项。Parses `session.resolveProxy()` output, using the first entry. */
export function parseResolveProxyResult(value: string): UpstreamProxy {
  const first = value.split(";")[0].trim();
  if (first === "DIRECT") return { type: "direct" };
  const match = /^(PROXY|HTTPS) (\[[^\]]+\]|[^\s:]+):(\d+)$/u.exec(first);
  if (!match) return { type: "unsupported", value: first };
  return {
    type: "http",
    host: match[2].replace(/^\[|\]$/gu, ""),
    port: Number(match[3]),
    secure: match[1] === "HTTPS",
  };
}

/** Extension session 的 Web 权限：全屏允许，剪贴板按批准的权限，其他拒绝。Web permissions in an extension session. */
export function isWebPermissionAllowed(
  permission: string,
  grantedPermissions: readonly ExtensionPermission[],
): boolean {
  if (permission === "fullscreen") return true;
  if (permission === "clipboard-read") return grantedPermissions.includes("clipboard.read");
  if (permission === "clipboard-sanitized-write")
    return grantedPermissions.includes("clipboard.write");
  return false;
}

/** 获取 Service Worker 主脚本的请求。A request fetching a Service Worker main script. */
export function isServiceWorkerScriptRequest(
  headers: Readonly<Record<string, string | string[] | undefined>>,
): boolean {
  return Object.entries(headers).some(
    ([name, value]) =>
      name.toLowerCase() === "service-worker" &&
      (Array.isArray(value) ? value : [value]).some(
        (item) => item?.trim().toLowerCase() === "script",
      ),
  );
}

export type ServiceWorkerRegistrationOutcome = {
  code: "SERVICE_WORKER_NOT_ALLOWED";
  message: string;
  storagesToClear: ["serviceworkers"];
};

/**
 * 出现注册说明请求头拦截没有生效：该 extension 进入 failed 并清除 Service Worker 存储。
 * A registration means the header block did not take effect: the extension fails and its Service Worker storage is cleared.
 */
export function serviceWorkerRegistrationOutcome(scope: string): ServiceWorkerRegistrationOutcome {
  return {
    code: "SERVICE_WORKER_NOT_ALLOWED",
    message: `A Service Worker was registered for ${scope}. Extensions cannot use Service Workers.`,
    storagesToClear: ["serviceworkers"],
  };
}
