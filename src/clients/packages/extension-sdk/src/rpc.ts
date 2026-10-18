// SDK 与宿主 broker 共用的端口消息协议。The port message protocol shared by the SDK and the host broker.
import type { ExtensionErrorData } from "./api";

export type RpcMessage =
  | { kind: "request"; id: number; method: string; params: unknown }
  | { kind: "response"; id: number; ok: true; value: unknown }
  | { kind: "response"; id: number; ok: false; error: ExtensionErrorData }
  | { kind: "event"; name: string; data: unknown }
  | { kind: "cancel"; id: number }
  | { kind: "ping"; seq: number }
  | { kind: "pong"; seq: number };

/** 浏览器 `MessagePort` 与 Node `worker_threads` 的 `MessagePort` 共有的部分。What browser and Node `MessagePort`s have in common. */
export interface RpcPort {
  postMessage(message: unknown, transfer: never[]): void;
  addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
  start(): void;
  close(): void;
}

/** Extension API 抛出的错误。Errors thrown by the Extension API. */
export class ExtensionError extends Error {
  public override readonly name = "ExtensionError";
  public readonly code: string | null;
  public readonly syscall: string | null;
  public readonly path: string | null;

  public constructor(data: Omit<ExtensionErrorData, "name">) {
    super(data.message);
    this.code = data.code;
    this.syscall = data.syscall;
    this.path = data.path;
  }
}

export function createExtensionError(code: string, message: string): ExtensionError {
  return new ExtensionError({ message, code, syscall: null, path: null });
}

/** 把抛出值转换为跨端口的错误数据。Converts a thrown value into error data for the port. */
export function toExtensionErrorData(error: unknown): ExtensionErrorData {
  if (error instanceof Error) {
    const details = error as Error & { code?: unknown; syscall?: unknown; path?: unknown };
    return {
      name: error.name,
      message: error.message,
      code: typeof details.code === "string" ? details.code : null,
      syscall: typeof details.syscall === "string" ? details.syscall : null,
      path: typeof details.path === "string" ? details.path : null,
    };
  }
  return { name: "Error", message: String(error), code: null, syscall: null, path: null };
}

export function fromExtensionErrorData(data: ExtensionErrorData): ExtensionError {
  return new ExtensionError(data);
}

/** 收集值中 `Uint8Array` 的 `ArrayBuffer`，放进转移列表。Collects the `ArrayBuffer`s of `Uint8Array`s for the transfer list. */
export function collectTransferables(
  value: unknown,
  found = new Set<ArrayBuffer>(),
): ArrayBuffer[] {
  if (value instanceof Uint8Array) {
    if (value.buffer instanceof ArrayBuffer) found.add(value.buffer);
  } else if (Array.isArray(value)) {
    for (const item of value) collectTransferables(item, found);
  } else if (
    typeof value === "object" &&
    value !== null &&
    Object.getPrototypeOf(value) === Object.prototype
  ) {
    for (const item of Object.values(value)) collectTransferables(item, found);
  }
  return [...found];
}

export type RpcRequestHandler = (method: string, params: unknown, signal: AbortSignal) => unknown;

export type RpcEndpointHandlers = {
  request?: RpcRequestHandler;
  event?(name: string, data: unknown): void;
  pong?(seq: number): void;
};

type PendingRequest = {
  resolve(value: unknown): void;
  reject(error: ExtensionError): void;
};

/** 端口一端：发起请求、处理对方请求、收发事件、回复心跳。One end of the port. */
export class RpcEndpoint {
  private nextId = 1;
  private closed = false;
  private readonly pending = new Map<number, PendingRequest>();
  private readonly incoming = new Map<number, AbortController>();
  private readonly listener = (event: { data: unknown }) => this.receive(event.data as RpcMessage);

  public constructor(
    private readonly port: RpcPort,
    private readonly handlers: RpcEndpointHandlers,
  ) {
    port.addEventListener("message", this.listener);
    port.start();
  }

  public get isClosed(): boolean {
    return this.closed;
  }

  public request<T = unknown>(
    method: string,
    params: unknown,
    options: { signal?: AbortSignal } = {},
  ): Promise<T> {
    if (this.closed)
      return Promise.reject(
        createExtensionError("ERR_CONNECTION_CLOSED", "The connection is closed."),
      );
    const { signal } = options;
    if (signal?.aborted)
      return Promise.reject(createExtensionError("ABORT_ERR", "The operation was aborted."));
    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      const onAbort = () => {
        if (!this.pending.delete(id)) return;
        this.post({ kind: "cancel", id });
        reject(createExtensionError("ABORT_ERR", "The operation was aborted."));
      };
      this.pending.set(id, {
        resolve: (value) => {
          signal?.removeEventListener("abort", onAbort);
          resolve(value as T);
        },
        reject: (error) => {
          signal?.removeEventListener("abort", onAbort);
          reject(error);
        },
      });
      signal?.addEventListener("abort", onAbort, { once: true });
      this.post({ kind: "request", id, method, params }, collectTransferables(params));
    });
  }

  public emit(name: string, data: unknown): void {
    if (!this.closed) this.post({ kind: "event", name, data }, collectTransferables(data));
  }

  public ping(seq: number): void {
    if (!this.closed) this.post({ kind: "ping", seq });
  }

  /** 关闭端口，未完成的请求以 `code` 结束。Closes the port and ends pending requests with `code`. */
  public close(code = "ERR_CONNECTION_CLOSED", message = "The connection is closed."): void {
    if (this.closed) return;
    this.closed = true;
    this.port.removeEventListener("message", this.listener);
    this.port.close();
    for (const controller of this.incoming.values()) controller.abort();
    this.incoming.clear();
    const pending = [...this.pending.values()];
    this.pending.clear();
    for (const request of pending) request.reject(createExtensionError(code, message));
  }

  private post(message: RpcMessage, transfer: ArrayBuffer[] = []): void {
    this.port.postMessage(message, transfer as never[]);
  }

  private receive(message: RpcMessage): void {
    if (this.closed || typeof message !== "object" || message === null) return;
    switch (message.kind) {
      case "request":
        void this.handleRequest(message.id, message.method, message.params);
        return;
      case "response": {
        const pending = this.pending.get(message.id);
        if (!pending) return;
        this.pending.delete(message.id);
        if (message.ok) pending.resolve(message.value);
        else pending.reject(fromExtensionErrorData(message.error));
        return;
      }
      case "event":
        this.handlers.event?.(message.name, message.data);
        return;
      case "cancel":
        this.incoming.get(message.id)?.abort();
        return;
      case "ping":
        this.post({ kind: "pong", seq: message.seq });
        return;
      case "pong":
        this.handlers.pong?.(message.seq);
        return;
    }
  }

  private async handleRequest(id: number, method: string, params: unknown): Promise<void> {
    const controller = new AbortController();
    this.incoming.set(id, controller);
    try {
      if (!this.handlers.request)
        throw createExtensionError("ERR_UNKNOWN_METHOD", `Unknown method: ${method}`);
      const value = await this.handlers.request(method, params, controller.signal);
      if (this.incoming.delete(id) && !this.closed) {
        this.post({ kind: "response", id, ok: true, value }, collectTransferables(value));
      }
    } catch (error) {
      if (this.incoming.delete(id) && !this.closed) {
        this.post({ kind: "response", id, ok: false, error: toExtensionErrorData(error) });
      }
    }
  }
}
