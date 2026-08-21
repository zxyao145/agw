import type { paths } from "./openapi";
import {
  ApiError,
  appendQuery,
  compilePath,
  readResponseBody,
  unwrapApiResultEnvelope,
  type ApiResultEnvelope,
} from "@agw/http-client";

export { ApiError } from "@agw/http-client";

export type ApiMethod = "get" | "post" | "put" | "delete";

export type ApiRuntimeConfig = {
  baseUrl: string;
  token: string | null;
};

export type BearerApiClientConfig = {
  baseUrl: string;
  token: string;
  onUnauthorized?: () => void;
};

const browserApiRuntime: ApiRuntimeConfig = { baseUrl: "", token: null };
let apiRuntime = browserApiRuntime;

let antiforgeryToken: string | null = null;

export function clearAntiforgeryToken(): void {
  antiforgeryToken = null;
}

export function configureApiRuntime(config: ApiRuntimeConfig): void {
  apiRuntime = {
    baseUrl: config.baseUrl.trim().replace(/\/+$/u, ""),
    token: config.token,
  };
  clearAntiforgeryToken();
}

export function resetApiRuntime(): void {
  apiRuntime = browserApiRuntime;
  clearAntiforgeryToken();
}

export function getApiRuntime(): ApiRuntimeConfig {
  return apiRuntime;
}

function resolveApiUrl(path: string): string {
  return apiRuntime.baseUrl ? `${apiRuntime.baseUrl}${path}` : path;
}

async function getAntiforgeryToken(): Promise<string> {
  if (antiforgeryToken) return antiforgeryToken;
  const response = await fetch(resolveApiUrl("/api/auth/antiforgery"), {
    credentials:
      apiRuntime.baseUrl && apiRuntime.token
        ? "omit"
        : apiRuntime.baseUrl
          ? "include"
          : "same-origin",
  });
  const body = await readResponseBody(response);
  const value = unwrapApiResultEnvelope(body) as { requestToken?: unknown } | undefined;
  if (!response.ok || typeof value?.requestToken !== "string") {
    throw new Error("Unable to obtain antiforgery token.");
  }
  antiforgeryToken = value.requestToken;
  return antiforgeryToken;
}

function isAntiforgeryValidationFailure(response: Response, body: unknown): boolean {
  return (
    response.status === 403 &&
    typeof body === "object" &&
    body !== null &&
    "code" in body &&
    body.code === 4030003
  );
}

export type PathsWith<M extends ApiMethod> = {
  [P in keyof paths]-?: M extends keyof paths[P] ? P : never;
}[keyof paths];

type Operation<P extends keyof paths, M extends keyof paths[P]> = paths[P][M];

type OperationParams<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { parameters: infer T } ? T : never;

type PathParams<P extends keyof paths, M extends keyof paths[P]> =
  OperationParams<P, M> extends { path?: infer T } ? T : never;

type QueryParams<P extends keyof paths, M extends keyof paths[P]> =
  OperationParams<P, M> extends { query?: infer T } ? T : never;

type RequestContent<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { requestBody: { content: infer C } } ? C : never;

type JsonRequestBody<C> = C extends { "application/json": infer B } ? B : never;

type MultipartRequestBody<C> = C extends { "multipart/form-data": unknown } ? FormData : never;

type RequestBody<P extends keyof paths, M extends keyof paths[P]> =
  | JsonRequestBody<RequestContent<P, M>>
  | MultipartRequestBody<RequestContent<P, M>>;

type HasRequestBody<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { requestBody: unknown } ? true : false;

type ParamsOption<P extends keyof paths, M extends keyof paths[P]> = [
  OperationParams<P, M>,
] extends [never]
  ? Record<never, never>
  : {
      params?: {
        path?: PathParams<P, M>;
        query?: QueryParams<P, M>;
      };
    };

type BodyOption<P extends keyof paths, M extends keyof paths[P]> =
  HasRequestBody<P, M> extends true ? { body: RequestBody<P, M> } : Record<never, never>;

export type ApiRequestOptions<P extends keyof paths, M extends keyof paths[P]> = ParamsOption<
  P,
  M
> &
  BodyOption<P, M> & {
    headers?: HeadersInit;
    signal?: AbortSignal;
  };

type OperationResponses<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { responses: infer R } ? R : never;

type Response200<R> = R extends { 200: infer T } ? T : R extends { "200": infer T } ? T : unknown;

type UnwrapApiResult<T> = T extends ApiResultEnvelope<infer D> ? D : T;

export type ApiResponse<P extends keyof paths, M extends keyof paths[P]> =
  Response200<OperationResponses<P, M>> extends { content: infer C }
    ? C extends { "application/json": infer T }
      ? UnwrapApiResult<T>
      : unknown
    : unknown;

export function apiRequest<P extends PathsWith<"get">>(
  path: P,
  method: "get",
  options?: ApiRequestOptions<P, "get">,
): Promise<ApiResponse<P, "get">>;
export function apiRequest<P extends PathsWith<"post">>(
  path: P,
  method: "post",
  options?: ApiRequestOptions<P, "post">,
): Promise<ApiResponse<P, "post">>;
export function apiRequest<P extends PathsWith<"put">>(
  path: P,
  method: "put",
  options?: ApiRequestOptions<P, "put">,
): Promise<ApiResponse<P, "put">>;
export function apiRequest<P extends PathsWith<"delete">>(
  path: P,
  method: "delete",
  options?: ApiRequestOptions<P, "delete">,
): Promise<ApiResponse<P, "delete">>;
export async function apiRequest(
  path: keyof paths,
  method: ApiMethod,
  options?: {
    params?: {
      path?: Record<string, unknown>;
      query?: Record<string, unknown>;
    };
    body?: unknown;
    headers?: HeadersInit;
    signal?: AbortSignal;
  },
): Promise<unknown> {
  const opts = options ?? {};

  const urlWithPath = compilePath(String(path), opts.params?.path);
  const url = resolveApiUrl(appendQuery(urlWithPath, opts.params?.query));
  let retriedAntiforgery = false;

  while (true) {
    const headers: HeadersInit = { ...opts.headers };

    if (apiRuntime.token) {
      (headers as Record<string, string>).Authorization = `Bearer ${apiRuntime.token}`;
    }

    if (method !== "get") {
      (headers as Record<string, string>)["X-CSRF-TOKEN"] = await getAntiforgeryToken();
    }

    const init: RequestInit = {
      method: method.toUpperCase(),
      headers,
      signal: opts.signal,
      credentials:
        apiRuntime.baseUrl && apiRuntime.token
          ? "omit"
          : apiRuntime.baseUrl
            ? "include"
            : "same-origin",
    };

    if (opts.body !== undefined) {
      if (opts.body instanceof FormData) {
        delete (headers as Record<string, string>)["content-type"];
        delete (headers as Record<string, string>)["Content-Type"];
        init.body = opts.body;
      } else {
        (headers as Record<string, string>)["content-type"] ??= "application/json";
        init.body = JSON.stringify(opts.body);
      }
    }

    const response = await fetch(url, init);

    if (!response.ok) {
      const errBody = await readResponseBody(response);
      if (
        method !== "get" &&
        !retriedAntiforgery &&
        isAntiforgeryValidationFailure(response, errBody)
      ) {
        clearAntiforgeryToken();
        retriedAntiforgery = true;
        continue;
      }
      if (
        response.status === 401 &&
        typeof window !== "undefined" &&
        !String(path).startsWith("/api/auth/") &&
        !apiRuntime.baseUrl
      ) {
        const returnUrl = `${window.location.pathname}${window.location.search}`;
        window.location.assign(`/login/?returnUrl=${encodeURIComponent(returnUrl)}`);
      }
      throw new ApiError({
        status: response.status,
        statusText: response.statusText,
        url,
        body: errBody,
      });
    }

    // Some endpoints return 200 with no response body.
    return unwrapApiResultEnvelope(await readResponseBody(response));
  }
}

export function apiGet<P extends PathsWith<"get">>(
  path: P,
  options?: ApiRequestOptions<P, "get">,
): Promise<ApiResponse<P, "get">> {
  return apiRequest(path, "get", options) as Promise<ApiResponse<P, "get">>;
}

export function apiPost<P extends PathsWith<"post">>(
  path: P,
  options?: ApiRequestOptions<P, "post">,
): Promise<ApiResponse<P, "post">> {
  return apiRequest(path, "post", options) as Promise<ApiResponse<P, "post">>;
}

export function apiPut<P extends PathsWith<"put">>(
  path: P,
  options?: ApiRequestOptions<P, "put">,
): Promise<ApiResponse<P, "put">> {
  return apiRequest(path, "put", options) as Promise<ApiResponse<P, "put">>;
}

export function apiDelete<P extends PathsWith<"delete">>(
  path: P,
  options?: ApiRequestOptions<P, "delete">,
): Promise<ApiResponse<P, "delete">> {
  return apiRequest(path, "delete", options) as Promise<ApiResponse<P, "delete">>;
}

export type AgwApiClient = {
  apiGet: typeof apiGet;
  apiPost: typeof apiPost;
  apiPut: typeof apiPut;
  apiDelete: typeof apiDelete;
};

/**
 * Creates an isolated API client for Desktop, Mobile, and automation clients that authenticate
 * with a Bearer token. Bearer requests never participate in the browser cookie/CSRF flow.
 */
export function createBearerApiClient(config: BearerApiClientConfig): AgwApiClient {
  const baseUrl = config.baseUrl.trim().replace(/\/+$/u, "");
  const token = config.token.trim();

  if (!baseUrl) throw new Error("API base URL is required.");
  if (!token) throw new Error("Bearer token is required.");

  const request = async (
    path: keyof paths,
    method: ApiMethod,
    options?: {
      params?: {
        path?: Record<string, unknown>;
        query?: Record<string, unknown>;
      };
      body?: unknown;
      headers?: HeadersInit;
      signal?: AbortSignal;
    },
  ): Promise<unknown> => {
    const opts = options ?? {};
    const urlWithPath = compilePath(String(path), opts.params?.path);
    const url = `${baseUrl}${appendQuery(urlWithPath, opts.params?.query)}`;
    const headers: HeadersInit = {
      ...opts.headers,
      Authorization: `Bearer ${token}`,
    };
    const init: RequestInit = {
      method: method.toUpperCase(),
      headers,
      signal: opts.signal,
      credentials: "omit",
    };

    if (opts.body !== undefined) {
      if (typeof FormData !== "undefined" && opts.body instanceof FormData) {
        delete (headers as Record<string, string>)["content-type"];
        delete (headers as Record<string, string>)["Content-Type"];
        init.body = opts.body;
      } else {
        (headers as Record<string, string>)["content-type"] ??= "application/json";
        init.body = JSON.stringify(opts.body);
      }
    }

    const response = await fetch(url, init);
    const responseBody = await readResponseBody(response);
    if (!response.ok) {
      if (response.status === 401) config.onUnauthorized?.();
      throw new ApiError({
        status: response.status,
        statusText: response.statusText,
        url,
        body: responseBody,
      });
    }

    return unwrapApiResultEnvelope(responseBody);
  };

  return {
    apiGet: ((path: keyof paths, options?: unknown) =>
      request(path, "get", options as never)) as AgwApiClient["apiGet"],
    apiPost: ((path: keyof paths, options?: unknown) =>
      request(path, "post", options as never)) as AgwApiClient["apiPost"],
    apiPut: ((path: keyof paths, options?: unknown) =>
      request(path, "put", options as never)) as AgwApiClient["apiPut"],
    apiDelete: ((path: keyof paths, options?: unknown) =>
      request(path, "delete", options as never)) as AgwApiClient["apiDelete"],
  };
}
