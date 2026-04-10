export const OAUTH_SERVER_CALLBACK_PATH = "/api/integrations/oauth/callback";

type BuildOAuthServerCallbackUrlArgs = {
  apiBaseUrl?: string;
  currentOrigin?: string;
};

export function buildOAuthServerCallbackUrl({
  apiBaseUrl,
  currentOrigin,
}: BuildOAuthServerCallbackUrlArgs): string {
  const baseUrl = currentOrigin?.trim() || apiBaseUrl?.trim();
  if (!baseUrl) {
    return OAUTH_SERVER_CALLBACK_PATH;
  }

  return new URL(OAUTH_SERVER_CALLBACK_PATH, ensureTrailingSlash(baseUrl)).toString();
}

function ensureTrailingSlash(value: string): string {
  return value.endsWith("/") ? value : `${value}/`;
}
