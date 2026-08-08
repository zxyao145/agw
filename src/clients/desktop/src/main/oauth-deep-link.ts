export const DESKTOP_OAUTH_PROTOCOL = "agw-desktop";

const DESKTOP_OAUTH_URL_PREFIX = `${DESKTOP_OAUTH_PROTOCOL}://`;
const ERROR_CODES = new Set(["invalid_state", "authorization_denied", "token_exchange_failed"]);

export function parseOAuthDeepLink(value: string): string | null {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }

  if (
    url.protocol !== `${DESKTOP_OAUTH_PROTOCOL}:` ||
    url.host !== "oauth" ||
    url.pathname !== "/complete"
  ) {
    return null;
  }

  const oauth = url.searchParams.get("oauth");
  if (oauth === "authorized") {
    return "/integrations/?oauth=authorized";
  }
  if (oauth !== "error") {
    return null;
  }

  const candidateCode = url.searchParams.get("code") ?? "";
  const code = ERROR_CODES.has(candidateCode) ? candidateCode : "invalid_state";
  return `/integrations/?oauth=error&code=${encodeURIComponent(code)}`;
}

export function findOAuthDeepLink(argv: readonly string[]): string | null {
  return argv.find((value) => value.startsWith(DESKTOP_OAUTH_URL_PREFIX)) ?? null;
}
