import { parseHostPermissions, toCspSource } from "../../shared/extension-host-permissions";

/** 每个 extension 页面的 CSP，`H` 为已批准的域名。The CSP for every extension page; `H` is the approved hosts. */
export function buildExtensionCsp(grantedHostPermissions: readonly string[]): string {
  const hosts = parseHostPermissions(grantedHostPermissions).map(toCspSource);
  const withHosts = (...sources: string[]) => [...sources, ...hosts].join(" ");
  return [
    "default-src 'none'",
    "script-src 'self' 'wasm-unsafe-eval'",
    "style-src 'self' 'unsafe-inline'",
    `img-src ${withHosts("'self'", "data:", "blob:")}`,
    `media-src ${withHosts("'self'", "data:", "blob:")}`,
    "font-src 'self' data:",
    `connect-src ${withHosts("'self'")}`,
    "worker-src 'self' blob:",
    `frame-src ${hosts.length > 0 ? hosts.join(" ") : "'none'"}`,
    "form-action 'self'",
    "base-uri 'self'",
    "object-src 'none'",
    "frame-ancestors 'none'",
  ].join("; ");
}
