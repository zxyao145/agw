import type { ExtensionHttpRequest, ExtensionHttpResult } from "../../shared/contracts";
import {
  type HostPermission,
  matchesHostPermissionHttpUrl,
} from "../../shared/extension-host-permissions";
import { ExtensionOperationError } from "./extension-ipc-result";
import { ExtensionResourceTracker, type ResourceOwner } from "./extension-resource-tracker";

export const MAX_REDIRECTS = 5;
export const MAX_RESPONSE_BYTES = 100 * 1024 * 1024;

const REDIRECT_STATUSES = new Set([301, 302, 303, 307, 308]);

/** 一次请求的结果：重定向（不跟随）或最终响应。One hop: a redirect (not followed) or the final response. */
export type HttpHop =
  | { kind: "redirect"; status: number; location: string }
  | {
      kind: "response";
      status: number;
      headers: Record<string, string>;
      body: AsyncIterable<Uint8Array>;
      cancel(): void;
    };

export type HttpHopRequest = {
  url: string;
  method: string;
  headers?: Record<string, string>;
  body?: string | Uint8Array;
  signal: AbortSignal;
};

/** 发出一次请求、不跟随重定向。Sends one request without following redirects. */
export type ExtensionHttpTransport = (request: HttpHopRequest) => Promise<HttpHop>;

/** 基于 fetch 的传输（`redirect: "manual"` 返回 3xx 响应）。A fetch-based transport (`redirect: "manual"` returns the 3xx). */
export function fetchHttpTransport(fetchImpl: typeof fetch): ExtensionHttpTransport {
  return async ({ url, method, headers, body, signal }) => {
    const response = await fetchImpl(url, {
      method,
      headers,
      body: body as BodyInit | undefined,
      redirect: "manual",
      credentials: "omit",
      signal,
    });
    const location = response.headers.get("location");
    if (REDIRECT_STATUSES.has(response.status) && location !== null) {
      await response.body?.cancel();
      return {
        kind: "redirect",
        status: response.status,
        location: new URL(location, url).toString(),
      };
    }
    const stream = response.body;
    return {
      kind: "response",
      status: response.status,
      headers: Object.fromEntries(response.headers),
      body: stream ?? (async function* () {})(),
      cancel: () => void stream?.cancel(),
    };
  };
}

function notPermitted(url: string): ExtensionOperationError {
  return new ExtensionOperationError(
    "ERR_HOST_NOT_PERMITTED",
    `${url} does not match an approved host permission.`,
  );
}

/**
 * extension 的 `http.request`：生产环境的传输使用该 extension 的 session，请求同样经过 webRequest 与出站代理。
 * The extension's `http.request`; in production the transport uses the extension session, so requests still pass
 * webRequest and the egress proxy.
 */
export class ExtensionHttpClient {
  private readonly requests = new ExtensionResourceTracker<AbortController>((controller) =>
    controller.abort(),
  );

  public constructor(
    private readonly transportFor: (extensionId: string) => ExtensionHttpTransport,
    private readonly maxResponseBytes = MAX_RESPONSE_BYTES,
  ) {}

  public async request(
    owner: ResourceOwner,
    permissions: readonly HostPermission[],
    request: ExtensionHttpRequest,
  ): Promise<ExtensionHttpResult> {
    if (!matchesHostPermissionHttpUrl(permissions, request.url)) throw notPermitted(request.url);
    const controller = new AbortController();
    this.requests.add(request.requestId, owner, controller);
    try {
      return await this.send(owner.extensionId, permissions, request, controller.signal);
    } catch (error) {
      if (controller.signal.aborted) {
        throw new ExtensionOperationError("ABORT_ERR", "The request was aborted.");
      }
      throw error;
    } finally {
      this.requests.forget(request.requestId);
    }
  }

  /** `requestId` 不存在时不做任何事情。Does nothing when the request already ended. */
  public cancel(requestId: string): void {
    this.requests.remove(requestId);
  }

  public count(predicate: (owner: ResourceOwner) => boolean): number {
    return this.requests.count(predicate);
  }

  public releasePage(frameId: string, pageId: string): void {
    this.requests.releasePage(frameId, pageId);
  }

  public releaseFrame(extensionId: string, frameId: string): void {
    this.requests.releaseFrame(extensionId, frameId);
  }

  public releaseExtension(extensionId: string): void {
    this.requests.releaseExtension(extensionId);
  }

  public releaseAll(): void {
    this.requests.releaseAll();
  }

  /** 最多跟随 5 次重定向，每次先检查 Location 的 origin；303 改为 GET。At most 5 redirects, each Location checked first; 303 becomes GET. */
  private async send(
    extensionId: string,
    permissions: readonly HostPermission[],
    request: ExtensionHttpRequest,
    signal: AbortSignal,
  ): Promise<ExtensionHttpResult> {
    const transport = this.transportFor(extensionId);
    let url = request.url;
    let method = request.method ?? "GET";
    let body = request.body;
    for (let redirects = 0; ; redirects += 1) {
      const hop = await transport({ url, method, headers: request.headers, body, signal });
      if (hop.kind === "redirect") {
        if (redirects >= MAX_REDIRECTS) {
          throw new ExtensionOperationError(
            "ERR_TOO_MANY_REDIRECTS",
            `More than ${MAX_REDIRECTS} redirects.`,
          );
        }
        if (!matchesHostPermissionHttpUrl(permissions, hop.location))
          throw notPermitted(hop.location);
        url = hop.location;
        if (hop.status === 303) {
          method = "GET";
          body = undefined;
        }
        continue;
      }
      return { status: hop.status, headers: hop.headers, body: await this.readBody(hop) };
    }
  }

  private async readBody(hop: Extract<HttpHop, { kind: "response" }>): Promise<Uint8Array> {
    const chunks: Uint8Array[] = [];
    let total = 0;
    let tooLarge = false;
    for await (const chunk of hop.body) {
      total += chunk.byteLength;
      if (total > this.maxResponseBytes) {
        tooLarge = true;
        break;
      }
      chunks.push(chunk);
    }
    if (tooLarge) {
      // 先离开循环释放读取器，再取消请求。Leave the loop to release the reader, then cancel the request.
      hop.cancel();
      throw new ExtensionOperationError(
        "ERR_RESPONSE_TOO_LARGE",
        `The response body exceeds ${this.maxResponseBytes} bytes.`,
      );
    }
    const body = new Uint8Array(total);
    let offset = 0;
    for (const chunk of chunks) {
      body.set(chunk, offset);
      offset += chunk.byteLength;
    }
    return body;
  }
}
