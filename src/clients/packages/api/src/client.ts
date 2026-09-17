import type { paths } from "./openapi";
import {
  ApiError,
  ApiTransportError,
  appendQuery,
  compilePath,
  readResponseBody,
  unwrapApiResultEnvelope,
  type ApiResultEnvelope,
} from "@agw/http-client";

export { ApiError, ApiTransportError } from "@agw/http-client";

export type ApiMethod = "get" | "post" | "put" | "delete";

export type ApiRuntimeConfig = {
  baseUrl: string;
  token: string | null;
  explicitAuth?: boolean;
};

export type BearerApiClientConfig = {
  baseUrl: string;
  token: string;
  onUnauthorized?: () => void;
};

type ApiRuntimeState = ApiRuntimeConfig & { antiforgeryToken: string | null };
let apiRuntime: ApiRuntimeState = { baseUrl: "", token: null, antiforgeryToken: null };

export function clearAntiforgeryToken(): void {
  apiRuntime = { ...apiRuntime, antiforgeryToken: null };
}

export function configureApiRuntime(config: ApiRuntimeConfig): void {
  apiRuntime = {
    baseUrl: config.baseUrl.trim().replace(/\/+$/u, ""),
    token: config.token,
    ...(config.explicitAuth ? { explicitAuth: true } : {}),
    antiforgeryToken: null,
  };
}

export function resetApiRuntime(): void {
  apiRuntime = { baseUrl: "", token: null, antiforgeryToken: null };
}

export function getApiRuntime(): ApiRuntimeConfig {
  return {
    baseUrl: apiRuntime.baseUrl,
    token: apiRuntime.token,
    ...(apiRuntime.explicitAuth ? { explicitAuth: true } : {}),
  };
}

function resolveApiUrl(runtime: ApiRuntimeConfig, path: string): string {
  return runtime.baseUrl ? `${runtime.baseUrl}${path}` : path;
}

async function getAntiforgeryToken(
  runtime: ApiRuntimeState,
  signal?: AbortSignal,
): Promise<string> {
  if (runtime.antiforgeryToken) return runtime.antiforgeryToken;
  const url = resolveApiUrl(runtime, "/api/auth/antiforgery");
  let response: Response;
  try {
    response = await fetch(url, {
      ...(signal ? { signal } : {}),
      ...(runtime.explicitAuth ? { headers: { "X-Agw-Explicit-Auth": "1" } } : {}),
      credentials:
        runtime.explicitAuth || (runtime.baseUrl && runtime.token)
          ? "omit"
          : runtime.baseUrl
            ? "include"
            : "same-origin",
    });
  } catch (caught) {
    throw new ApiTransportError({ url, cause: caught });
  }
  const body = await readResponseBody(response);
  const value = unwrapApiResultEnvelope(body) as { requestToken?: unknown } | undefined;
  if (!response.ok || typeof value?.requestToken !== "string") {
    throw new Error("Unable to obtain antiforgery token.");
  }
  runtime.antiforgeryToken = value.requestToken;
  return runtime.antiforgeryToken;
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
  [P in keyof paths]-?: M extends keyof paths[P]
    ? NonNullable<paths[P][M]> extends never
      ? never
      : P
    : never;
}[keyof paths];

type Operation<P extends keyof paths, M extends keyof paths[P]> = paths[P][M];

type OperationParams<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { parameters: infer T } ? T : never;

type RequestContent<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { requestBody: { content: infer C } } ? C : never;

type JsonRequestBody<C> = C extends { "application/json": infer B } ? B : never;

type MultipartRequestBody<C> = C extends { "multipart/form-data": unknown } ? FormData : never;

type RequestBody<P extends keyof paths, M extends keyof paths[P]> =
  | JsonRequestBody<RequestContent<P, M>>
  | MultipartRequestBody<RequestContent<P, M>>;

type HasRequestBody<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { requestBody: unknown } ? true : false;

type RequestParameters<P extends keyof paths, M extends keyof paths[P]> = Pick<
  OperationParams<P, M>,
  Extract<keyof OperationParams<P, M>, "path" | "query">
>;

type ParamsOption<P extends keyof paths, M extends keyof paths[P]> =
  {} extends RequestParameters<P, M>
    ? { params?: RequestParameters<P, M> }
    : { params: RequestParameters<P, M> };

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

type RequestArguments<P extends keyof paths, M extends keyof paths[P]> =
  {} extends ApiRequestOptions<P, M>
    ? [options?: ApiRequestOptions<P, M>]
    : [options: ApiRequestOptions<P, M>];

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
  ...args: RequestArguments<P, "get">
): Promise<ApiResponse<P, "get">>;
export function apiRequest<P extends PathsWith<"post">>(
  path: P,
  method: "post",
  ...args: RequestArguments<P, "post">
): Promise<ApiResponse<P, "post">>;
export function apiRequest<P extends PathsWith<"put">>(
  path: P,
  method: "put",
  ...args: RequestArguments<P, "put">
): Promise<ApiResponse<P, "put">>;
export function apiRequest<P extends PathsWith<"delete">>(
  path: P,
  method: "delete",
  ...args: RequestArguments<P, "delete">
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
  // In-flight requests and retries retain the identity that started them.
  const runtime = apiRuntime;

  const urlWithPath = compilePath(String(path), opts.params?.path);
  const url = resolveApiUrl(runtime, appendQuery(urlWithPath, opts.params?.query));
  let retriedAntiforgery = false;

  while (true) {
    const headers: HeadersInit = { ...opts.headers };

    if (runtime.explicitAuth) (headers as Record<string, string>)["X-Agw-Explicit-Auth"] = "1";
    if (runtime.token) {
      (headers as Record<string, string>).Authorization = `Bearer ${runtime.token}`;
    }

    if (method !== "get") {
      (headers as Record<string, string>)["X-CSRF-TOKEN"] = await getAntiforgeryToken(
        runtime,
        opts.signal,
      );
    }

    const init: RequestInit = {
      method: method.toUpperCase(),
      headers,
      signal: opts.signal,
      credentials:
        runtime.explicitAuth || (runtime.baseUrl && runtime.token)
          ? "omit"
          : runtime.baseUrl
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

    let response: Response;
    try {
      response = await fetch(url, init);
    } catch (caught) {
      throw new ApiTransportError({ url, cause: caught });
    }

    if (!response.ok) {
      const errBody = await readResponseBody(response);
      if (
        method !== "get" &&
        !retriedAntiforgery &&
        isAntiforgeryValidationFailure(response, errBody)
      ) {
        runtime.antiforgeryToken = null;
        retriedAntiforgery = true;
        continue;
      }
      if (
        response.status === 401 &&
        runtime === apiRuntime &&
        typeof window !== "undefined" &&
        !String(path).startsWith("/api/auth/") &&
        !runtime.baseUrl
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
  ...args: RequestArguments<P, "get">
): Promise<ApiResponse<P, "get">> {
  return apiRequest(path, "get", ...args) as Promise<ApiResponse<P, "get">>;
}

export function apiPost<P extends PathsWith<"post">>(
  path: P,
  ...args: RequestArguments<P, "post">
): Promise<ApiResponse<P, "post">> {
  return apiRequest(path, "post", ...args) as Promise<ApiResponse<P, "post">>;
}

export function apiPut<P extends PathsWith<"put">>(
  path: P,
  ...args: RequestArguments<P, "put">
): Promise<ApiResponse<P, "put">> {
  return apiRequest(path, "put", ...args) as Promise<ApiResponse<P, "put">>;
}

export function apiDelete<P extends PathsWith<"delete">>(
  path: P,
  ...args: RequestArguments<P, "delete">
): Promise<ApiResponse<P, "delete">> {
  return apiRequest(path, "delete", ...args) as Promise<ApiResponse<P, "delete">>;
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

    let response: Response;
    try {
      response = await fetch(url, init);
    } catch (caught) {
      throw new ApiTransportError({ url, cause: caught });
    }
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
