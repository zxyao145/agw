export type DesktopFetcher = (input: string | Request, init?: RequestInit) => Promise<Response>;

type ApiEnvelope<T> = {
  code?: number;
  title?: string;
  data?: T;
};

async function readData<T>(response: Response, operation: string): Promise<T> {
  const body = (await response.json()) as ApiEnvelope<T>;
  if (!response.ok || body.code !== 0 || body.data === undefined) {
    throw new Error(body.title || `Unable to ${operation}.`);
  }
  return body.data;
}

export async function createLocalDesktopToken(
  fetcher: DesktopFetcher,
  baseUrl: string,
  tokenName: string,
): Promise<string> {
  const origin = baseUrl.replace(/\/+$/u, "");
  const antiforgeryResponse = await fetcher(`${origin}/api/auth/antiforgery`, {
    credentials: "include",
  });
  const antiforgery = await readData<{ requestToken?: string }>(
    antiforgeryResponse,
    "obtain an antiforgery token",
  );
  if (!antiforgery.requestToken) throw new Error("Unable to obtain an antiforgery token.");

  const createResponse = await fetcher(`${origin}/api/auth/tokens`, {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      "X-CSRF-TOKEN": antiforgery.requestToken,
    },
    body: JSON.stringify({ name: tokenName }),
  });
  const created = await readData<{ token?: string }>(createResponse, "create a Desktop API token");
  if (!created.token?.startsWith("agw_")) {
    throw new Error("Server returned an invalid Desktop API token.");
  }
  return created.token;
}
