import {
  AgentType,
  ExternalAgentKind,
  type A2AEndpointDto,
  type A2AOriginCredentialsRequest,
} from "./types";

export interface A2ACredentialHeaderRow {
  origin: string;
  name: string;
  value: string;
  saved: boolean;
}

export interface A2ACredentialGroup {
  origin: string;
  rows: A2ACredentialHeaderRow[];
}

export interface A2AEndpointDraft {
  cardUrl: string;
  groups: A2ACredentialGroup[];
}

export const A2A_ENDPOINT_LIMITS = {
  cardUrlLength: 2048,
  origins: 2,
  headersPerOrigin: 20,
  headerValueLength: 8192,
} as const;

const reservedNames = new Set([
  "host",
  "content-length",
  "content-type",
  "transfer-encoding",
  "connection",
  "accept",
  "a2a-version",
  "a2a-extensions",
]);
const headerName = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/;

export function isA2AAgent(
  type: AgentType | undefined,
  kind: ExternalAgentKind | undefined,
): boolean {
  return type === AgentType.External && kind === ExternalAgentKind.A2A;
}

function parseEndpointUrl(value: string): URL | null {
  if (value.length > A2A_ENDPOINT_LIMITS.cardUrlLength) return null;
  const url = URL.parse(value);
  if (!url) return null;

  const isHttp = url.protocol === "http:" || url.protocol === "https:";
  const authority = value.slice(url.protocol.length);
  const hasAuthority = authority.startsWith("//") && !authority.startsWith("///");
  const hasUserInfo =
    url.username || url.password || authority.startsWith("//@") || authority.startsWith("//:@");
  const hasInvalidCharacters = /[\s\\]|%(?![0-9a-f]{2})/i.test(value);
  return isHttp && hasAuthority && !hasUserInfo && !hasInvalidCharacters ? url : null;
}

export function getA2ACardUrlError(cardUrl: string): string | null {
  if (parseEndpointUrl(cardUrl)) return null;
  return (
    "Enter an absolute HTTP or HTTPS Agent Card URL without user info " +
    `(up to ${A2A_ENDPOINT_LIMITS.cardUrlLength} characters).`
  );
}

function formatOrigin(url: URL): string {
  const port = url.port || (url.protocol === "https:" ? "443" : "80");
  return `${url.protocol}//${url.hostname}:${port}`;
}

export function normalizeA2AOrigin(value: string): string | null {
  const url = parseEndpointUrl(value);
  if (!url || url.pathname !== "/" || value.includes("?") || value.includes("#")) return null;
  const firstPathSlash = value.indexOf("/", url.protocol.length + 2);
  return firstPathSlash === -1 || firstPathSlash === value.length - 1 ? formatOrigin(url) : null;
}

function usesSavedValue(row: A2ACredentialHeaderRow, origin: string): boolean {
  return row.saved && !row.value && normalizeA2AOrigin(row.origin) === origin;
}

export function getA2ACredentialsError(groups: A2ACredentialGroup[]): string | null {
  if (groups.length > A2A_ENDPOINT_LIMITS.origins)
    return `Use at most ${A2A_ENDPOINT_LIMITS.origins} credential origins.`;
  const origins = new Set<string>();
  for (const group of groups) {
    const origin = normalizeA2AOrigin(group.origin);
    if (!origin) return "Each credential group needs a valid HTTP or HTTPS origin.";
    if (origins.has(origin)) return "Credential origins must be unique.";
    origins.add(origin);
    if (group.rows.length > A2A_ENDPOINT_LIMITS.headersPerOrigin)
      return `An origin supports up to ${A2A_ENDPOINT_LIMITS.headersPerOrigin} credential headers.`;
    const names = new Set<string>();
    for (const row of group.rows) {
      const name = row.name.toLowerCase();
      if (!headerName.test(row.name) || reservedNames.has(name)) {
        return "Enter a valid credential header name. Reserved HTTP and A2A headers are unavailable.";
      }
      if (names.has(name)) return "Header names within an origin must be unique.";
      names.add(name);
      if (usesSavedValue(row, origin)) continue;
      const invalidValue =
        !row.value ||
        row.value.length > A2A_ENDPOINT_LIMITS.headerValueLength ||
        /[\r\n]/.test(row.value);
      if (invalidValue) {
        return `Enter a header value of 1 to ${A2A_ENDPOINT_LIMITS.headerValueLength} characters without line breaks.`;
      }
    }
  }
  return null;
}

export function getA2AEndpointError(draft: A2AEndpointDraft): string | null {
  return getA2ACardUrlError(draft.cardUrl) ?? getA2ACredentialsError(draft.groups);
}

export function getA2ACreateSettings() {
  return {
    modelProviderId: null,
    environmentVariables: {},
    extra: null,
    responseSchema: null,
  };
}

export function toA2AEndpointRequest({ cardUrl, groups }: A2AEndpointDraft) {
  const credentials: A2AOriginCredentialsRequest[] = groups
    .filter((group) => group.rows.length > 0)
    .map((group) => {
      const origin = normalizeA2AOrigin(group.origin) ?? group.origin;
      return {
        origin,
        headers: Object.fromEntries(
          group.rows.map((row) => [row.name, usesSavedValue(row, origin) ? null : row.value]),
        ),
      };
    });
  return { cardUrl, credentials };
}

export function isA2AOriginWarningVisible(origin: string, rows: A2ACredentialHeaderRow[]): boolean {
  return rows.length > 0 && normalizeA2AOrigin(origin)?.startsWith("http://") === true;
}

export function getA2ACredentialGroups(
  cardUrl: string,
  interfaceUrl: string | undefined,
  groups: A2ACredentialGroup[],
): A2ACredentialGroup[] {
  const byOrigin = new Map<string, A2ACredentialGroup>();
  for (const url of [cardUrl, interfaceUrl]) {
    const parsed = url ? parseEndpointUrl(url) : null;
    if (parsed) {
      const origin = formatOrigin(parsed);
      byOrigin.set(origin, { origin, rows: [] });
    }
  }
  for (const group of groups) {
    byOrigin.set(group.origin, group);
  }
  return [...byOrigin.values()];
}

export function createA2AEndpointDraft(
  endpoint?: A2AEndpointDto | null,
  saved = true,
): A2AEndpointDraft {
  return {
    cardUrl: endpoint?.cardUrl ?? "",
    groups: (endpoint?.credentialHeaderNames ?? [])
      .filter(({ names }) => names.length > 0)
      .map(({ origin, names }) => ({
        origin,
        rows: names.map((name) => ({ origin, name, value: "", saved })),
      })),
  };
}
