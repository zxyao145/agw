import { Platform } from "react-native";
import type { AgwLocalConfig } from "../config/agw-config";

export type AgwApiQuery = Record<
  string,
  string | number | boolean | null | undefined
>;

type AgwApiRequestOptions = {
  query?: AgwApiQuery;
};

type AgwApiClientOptions = {
  platform?: typeof Platform.OS;
};

type AgwResultEnvelope<T = unknown> = {
  code: number;
  title: string;
  data?: T;
};

export class AgwApiError extends Error {
  public readonly status: number;
  public readonly statusText: string;
  public readonly url: string;
  public readonly body: unknown;

  constructor({
    body,
    status,
    statusText,
    url,
  }: {
    body: unknown;
    status: number;
    statusText: string;
    url: string;
  }) {
    super(`Request failed: ${status} ${statusText}`);
    this.name = "AgwApiError";
    this.body = body;
    this.status = status;
    this.statusText = statusText;
    this.url = url;
  }
}

export function createAgwApiClient(
  config: AgwLocalConfig,
  options: AgwApiClientOptions = {}
) {
  const platform = options.platform ?? Platform.OS;

  return {
    deleteJson: <T = unknown>(path: string, options?: AgwApiRequestOptions) =>
      requestJson<T>(config, platform, path, "DELETE", undefined, options),
    getJson: <T = unknown>(path: string, options?: AgwApiRequestOptions) =>
      requestJson<T>(config, platform, path, "GET", undefined, options),
    getText: (path: string, options?: AgwApiRequestOptions) =>
      requestText(config, platform, path, "GET", options),
    postJson: <T = unknown>(
      path: string,
      body?: unknown,
      options?: AgwApiRequestOptions
    ) => requestJson<T>(config, platform, path, "POST", body, options),
    putJson: <T = unknown>(
      path: string,
      body?: unknown,
      options?: AgwApiRequestOptions
    ) => requestJson<T>(config, platform, path, "PUT", body, options),
  };
}

async function requestJson<T>(
  config: AgwLocalConfig,
  platform: typeof Platform.OS,
  path: string,
  method: string,
  body?: unknown,
  options?: AgwApiRequestOptions
): Promise<T> {
  const response = await request(config, platform, path, method, body, options);
  const responseBody = await readResponseBody(response);

  if (!response.ok) {
    throw new AgwApiError({
      body: responseBody,
      status: response.status,
      statusText: response.statusText,
      url: response.url,
    });
  }

  return unwrapAgwResult(responseBody) as T;
}

async function requestText(
  config: AgwLocalConfig,
  platform: typeof Platform.OS,
  path: string,
  method: string,
  options?: AgwApiRequestOptions
): Promise<string> {
  const response = await request(config, platform, path, method, undefined, options);
  const body = await response.text();

  if (!response.ok) {
    throw new AgwApiError({
      body,
      status: response.status,
      statusText: response.statusText,
      url: response.url,
    });
  }

  return body;
}

function request(
  config: AgwLocalConfig,
  platform: typeof Platform.OS,
  path: string,
  method: string,
  body?: unknown,
  options?: AgwApiRequestOptions
): Promise<Response> {
  const headers: Record<string, string> = {
    "X-API-Key": config.apiKey,
  };
  const init: RequestInit = {
    headers,
    method,
  };

  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }

  return fetch(buildUrl(config.serverDomain, platform, path, options?.query), init);
}

function buildUrl(
  baseUrl: string,
  platform: typeof Platform.OS,
  path: string,
  query?: AgwApiQuery
): string {
  const normalizedBaseUrl = normalizeBaseUrlForPlatform(
    baseUrl.replace(/\/+$/g, ""),
    platform
  );
  const normalizedPath = path.startsWith("/") ? path : `/${path}`;
  const url = `${normalizedBaseUrl}${normalizedPath}`;

  if (!query) {
    return url;
  }

  const queryString = Object.entries(query)
    .filter(([, value]) => value !== undefined && value !== null)
    .map(
      ([key, value]) =>
        `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`
    )
    .join("&");

  return queryString ? `${url}?${queryString}` : url;
}

function normalizeBaseUrlForPlatform(
  baseUrl: string,
  platform: typeof Platform.OS
): string {
  if (platform !== "android") {
    return baseUrl;
  }

  return baseUrl.replace(
    /^(https?:\/\/)(localhost|127\.0\.0\.1)(?=[:/]|$)/i,
    (_match, protocol: string) => `${protocol}192.168.10.24`
  );
}

async function readResponseBody(response: Response): Promise<unknown> {
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

function unwrapAgwResult(body: unknown): unknown {
  if (!isAgwResultEnvelope(body)) {
    return body;
  }

  return "data" in body ? body.data : undefined;
}

function isAgwResultEnvelope(body: unknown): body is AgwResultEnvelope {
  return (
    typeof body === "object" &&
    body !== null &&
    "code" in body &&
    typeof (body as AgwResultEnvelope).code === "number" &&
    "title" in body &&
    typeof (body as AgwResultEnvelope).title === "string"
  );
}
