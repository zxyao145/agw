export type HostPermissionScheme = "https" | "http" | "wss" | "ws";

/** 解析后的域名权限。A parsed host permission. */
export interface HostPermission {
  value: string;
  scheme: HostPermissionScheme;
  /** 小写主机名，通配项不含 `*.`。Lowercase host name; wildcard entries omit `*.`. */
  host: string;
  wildcard: boolean;
  port: number | "*" | null;
}

const LABEL = String.raw`[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?`;
// 最后一段以字母开头，纯数字的主机只能按 IPv4 解析，IP 地址因此不能使用通配符。
// The last label starts with a letter, so numeric hosts only parse as IPv4 and IP addresses cannot use wildcards.
const TOP_LABEL = String.raw`[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?`;
const OCTET = String.raw`(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)`;
const PORT = String.raw`\*|6553[0-5]|655[0-2]\d|65[0-4]\d{2}|6[0-4]\d{3}|[1-5]\d{4}|[1-9]\d{0,3}`;

/** manifest `hostPermissions` 每一项的格式。The format of each manifest `hostPermissions` entry. */
export const HOST_PERMISSION_PATTERN = new RegExp(
  String.raw`^(https|http|wss|ws)://(\*\.(?:${LABEL}\.)+${TOP_LABEL}|(?:${OCTET}\.){3}${OCTET}|(?:${LABEL}\.)*${TOP_LABEL})(?::(${PORT}))?$`,
  "u",
);

export function parseHostPermission(value: string): HostPermission | null {
  const match = HOST_PERMISSION_PATTERN.exec(value);
  if (!match) return null;
  const wildcard = match[2].startsWith("*.");
  return {
    value,
    scheme: match[1] as HostPermissionScheme,
    host: wildcard ? match[2].slice(2) : match[2],
    wildcard,
    port: match[3] === undefined ? null : match[3] === "*" ? "*" : Number(match[3]),
  };
}

/** 解析已经校验过的域名权限。Parses host permissions that were already validated. */
export function parseHostPermissions(values: readonly string[]): HostPermission[] {
  return values.map((value) => {
    const permission = parseHostPermission(value);
    if (!permission) throw new Error(`Invalid host permission: ${value}.`);
    return permission;
  });
}

export function defaultPort(scheme: string): number | null {
  if (scheme === "http" || scheme === "ws") return 80;
  if (scheme === "https" || scheme === "wss") return 443;
  return null;
}

export function isUnencryptedHostPermission(permission: HostPermission): boolean {
  return permission.scheme === "http" || permission.scheme === "ws";
}

function hostMatches(permission: HostPermission, host: string): boolean {
  const normalized = host.toLowerCase();
  return permission.wildcard
    ? normalized.endsWith(`.${permission.host}`)
    : normalized === permission.host;
}

function portMatches(permission: HostPermission, port: number): boolean {
  return (
    permission.port === "*" ||
    permission.port === port ||
    (permission.port === null && port === defaultPort(permission.scheme))
  );
}

/** URL 的 scheme、主机与端口匹配某个域名权限。The URL's scheme, host and port match a host permission. */
export function matchesHostPermissionUrl(
  permissions: readonly HostPermission[],
  value: string | URL,
): boolean {
  let url: URL;
  try {
    url = typeof value === "string" ? new URL(value) : value;
  } catch {
    return false;
  }
  const scheme = url.protocol.slice(0, -1);
  const port = url.port === "" ? defaultPort(scheme) : Number(url.port);
  if (port === null) return false;
  return permissions.some(
    (permission) =>
      permission.scheme === scheme &&
      hostMatches(permission, url.hostname) &&
      portMatches(permission, port),
  );
}

/** 只接受 `http:` / `https:` 地址。Accepts `http:` / `https:` URLs only. */
export function matchesHostPermissionHttpUrl(
  permissions: readonly HostPermission[],
  value: string | URL,
): boolean {
  let url: URL;
  try {
    url = typeof value === "string" ? new URL(value) : value;
  } catch {
    return false;
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") return false;
  return matchesHostPermissionUrl(permissions, url);
}

/** 出站代理 `CONNECT host:port` 的目标判断。Target check for an egress-proxy `CONNECT host:port`. */
export function matchesConnectTarget(
  permissions: readonly HostPermission[],
  host: string,
  port: number,
): boolean {
  return permissions.some(
    (permission) => hostMatches(permission, host) && portMatches(permission, port),
  );
}

/** CSP 来源表达式。CSP source expression. */
export function toCspSource(permission: HostPermission): string {
  const host = permission.wildcard ? `*.${permission.host}` : permission.host;
  const port = permission.port === null ? "" : `:${permission.port}`;
  return `${permission.scheme}://${host}${port}`;
}
