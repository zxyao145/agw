import type { paths } from "./openapi";

export type ApiMethod = "get" | "post" | "put" | "delete";

export type PathsWith<M extends ApiMethod> = {
  [P in keyof paths]-?: M extends keyof paths[P] ? P : never;
}[keyof paths];

type Operation<P extends keyof paths, M extends keyof paths[P]> = paths[P][M];

type OperationParams<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { parameters: infer T } ? T : never;

type PathParams<P extends keyof paths, M extends keyof paths[P]> =
  OperationParams<P, M> extends { path: infer T } ? T : never;

type QueryParams<P extends keyof paths, M extends keyof paths[P]> =
  OperationParams<P, M> extends { query: infer T } ? T : never;

type RequestContent<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { requestBody: { content: infer C } } ? C : never;

type JsonRequestBody<C> = C extends { "application/json": infer B } ? B : never;

type MultipartRequestBody<C> = C extends { "multipart/form-data": unknown }
  ? FormData
  : never;

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
  HasRequestBody<P, M> extends true
    ? { body: RequestBody<P, M> }
    : Record<never, never>;

export type ApiRequestOptions<
  P extends keyof paths,
  M extends keyof paths[P],
> = ParamsOption<P, M> &
  BodyOption<P, M> & {
    headers?: HeadersInit;
    signal?: AbortSignal;
  };

type OperationResponses<P extends keyof paths, M extends keyof paths[P]> =
  Operation<P, M> extends { responses: infer R } ? R : never;

type Response200<R> = R extends { 200: infer T }
  ? T
  : R extends { "200": infer T }
    ? T
    : unknown;

export type ApiResponse<P extends keyof paths, M extends keyof paths[P]> =
  Response200<OperationResponses<P, M>> extends { content: infer C }
    ? C extends { "application/json": infer T }
      ? T
      : unknown
    : unknown;

export class ApiError extends Error {
  public readonly status: number;
  public readonly statusText: string;
  public readonly url: string;
  public readonly body: unknown;

  public constructor(args: {
    status: number;
    statusText: string;
    url: string;
    body: unknown;
  }) {
    super(`Request failed: ${args.status} ${args.statusText}`);
    this.name = "ApiError";
    this.status = args.status;
    this.statusText = args.statusText;
    this.url = args.url;
    this.body = args.body;
  }
}

function compilePath(
  pathTemplate: string,
  pathParams?: Record<string, unknown>,
): string {
  if (!pathParams) return pathTemplate;
  return pathTemplate.replace(/\{(\w+)\}/g, (_, key: string) => {
    const value = pathParams[key];
    if (value === undefined || value === null) {
      throw new Error(`Missing path param: ${key}`);
    }
    return encodeURIComponent(String(value));
  });
}

function appendQuery(url: string, query?: Record<string, unknown>): string {
  if (!query) return url;
  const usp = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) {
    if (v === undefined || v === null) continue;
    if (Array.isArray(v)) {
      for (const item of v) {
        if (item === undefined || item === null) continue;
        usp.append(k, String(item));
      }
      continue;
    }
    usp.set(k, String(v));
  }
  const qs = usp.toString();
  if (!qs) return url;
  return url.includes("?") ? `${url}&${qs}` : `${url}?${qs}`;
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
  const url = appendQuery(urlWithPath, opts.params?.query);

  const headers: HeadersInit = { ...(opts.headers ?? {}) };
  const init: RequestInit = {
    method: method.toUpperCase(),
    headers,
    signal: opts.signal,
  };

  if (opts.body !== undefined) {
    if (opts.body instanceof FormData) {
      delete (headers as Record<string, string>)["content-type"];
      delete (headers as Record<string, string>)["Content-Type"];
      init.body = opts.body;
    } else {
      (headers as Record<string, string>)["content-type"] ??=
        "application/json";
      init.body = JSON.stringify(opts.body);
    }
  }

  const response = await fetch(url, init);

  if (!response.ok) {
    const errBody = await readResponseBody(response);
    throw new ApiError({
      status: response.status,
      statusText: response.statusText,
      url,
      body: errBody,
    });
  }

  // Some endpoints return 200 with no response body.
  return await readResponseBody(response);
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
  return apiRequest(path, "delete", options) as Promise<
    ApiResponse<P, "delete">
  >;
}
