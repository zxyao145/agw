export type ApiResultEnvelope<T = unknown> = {
  code: number;
  title: string;
  statusCode?: number;
  detail?: string | null;
  data?: T;
};

export class ApiError extends Error {
  public readonly status: number;
  public readonly statusText: string;
  public readonly url: string;
  public readonly body: unknown;

  public constructor(args: { status: number; statusText: string; url: string; body: unknown }) {
    super(`Request failed: ${args.status} ${args.statusText}`);
    this.name = "ApiError";
    this.status = args.status;
    this.statusText = args.statusText;
    this.url = args.url;
    this.body = args.body;
  }
}

export function compilePath(pathTemplate: string, pathParams?: Record<string, unknown>): string {
  if (!pathParams) return pathTemplate;
  return pathTemplate.replace(/\{(\w+)\}/g, (_, key: string) => {
    const value = pathParams[key];
    if (value === undefined || value === null) {
      throw new Error(`Missing path param: ${key}`);
    }
    return encodeURIComponent(String(value));
  });
}

export function appendQuery(url: string, query?: Record<string, unknown>): string {
  if (!query) return url;
  const searchParams = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null) continue;
    if (Array.isArray(value)) {
      for (const item of value) {
        if (item === undefined || item === null) continue;
        searchParams.append(key, String(item));
      }
      continue;
    }
    searchParams.set(key, String(value));
  }
  const queryString = searchParams.toString();
  if (!queryString) return url;
  return url.includes("?") ? `${url}&${queryString}` : `${url}?${queryString}`;
}

export async function readResponseBody(response: Response): Promise<unknown> {
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) {
    try {
      return await response.json();
    } catch {
      return undefined;
    }
  }

  try {
    return await response.text();
  } catch {
    return undefined;
  }
}

export function isApiResultEnvelope(body: unknown): body is ApiResultEnvelope {
  return (
    typeof body === "object" &&
    body !== null &&
    "code" in body &&
    typeof body.code === "number" &&
    "title" in body &&
    typeof body.title === "string"
  );
}

export function unwrapApiResultEnvelope(body: unknown): unknown {
  if (!isApiResultEnvelope(body)) return body;
  return "data" in body ? body.data : undefined;
}
