import { getApiRuntime } from "@agw/api";
import {
  type ContextProject,
  type ContextTarget,
  type ContextUser,
  createExtensionError,
  type ExtensionPermission,
  type HostContext,
  type WorkspaceState,
} from "@agw/extension-sdk";

/** 连接标识：profile id、规范化后的 baseUrl 与当前 token。Connection identity: profile id, normalized base URL and token. */
export type ConnectionIdentity = {
  profileId: string;
  baseUrl: string;
  token: string | null;
};

export type ServerSummary = HostContext["server"];

export type WorkspaceSelection = {
  project: ContextProject | null;
  conversation: { id: string } | null;
  target: ContextTarget | null;
};

/** 与 `@agw/api` 的 `configureApiRuntime` 相同的规范化。The same normalization as `@agw/api`'s `configureApiRuntime`. */
export function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.trim().replace(/\/+$/u, "");
}

export function connectionKeyOf(identity: ConnectionIdentity): string {
  return JSON.stringify([identity.profileId, normalizeBaseUrl(identity.baseUrl), identity.token]);
}

export function connectionChangedError(): Error {
  return createExtensionError("ERR_CONNECTION_CHANGED", "The Server connection changed.");
}

/** 不包含 `extension` 字段的宿主 context。The host context without the `extension` field. */
export type HostContextBase = Omit<HostContext, "extension">;

/** 按批准的权限去掉 context 字段。Removes context fields the extension has no permission for. */
export function filterContextForPermissions(
  context: HostContext,
  permissions: readonly ExtensionPermission[],
): HostContext {
  const filtered: HostContext = { ...context };
  if (!permissions.includes("context.user")) delete filtered.user;
  if (!permissions.includes("context.project")) {
    delete filtered.project;
    delete filtered.conversation;
  }
  if (!permissions.includes("context.agent")) delete filtered.target;
  return filtered;
}

/**
 * 宿主 context 与连接标识。标识变化时清空 user/project/conversation/target，只通知一次，
 * 并以 ERR_CONNECTION_CHANGED 结束旧连接下尚未完成的请求。
 * Host context and connection identity. A changed identity clears user/project/conversation/target,
 * notifies once and ends pending requests of the old connection with ERR_CONNECTION_CHANGED.
 */
export class ContextService {
  private identity: ConnectionIdentity | null = null;
  private key: string | null = null;
  private context: HostContextBase;
  private readonly listeners = new Set<(context: HostContextBase) => void>();
  private readonly connectionListeners = new Set<(key: string | null) => void>();
  private readonly pending = new Set<AbortController>();

  public constructor(app: HostContext["app"], workspace: WorkspaceState) {
    this.context = {
      app,
      server: { id: "", name: "", kind: "local" },
      user: null,
      project: null,
      conversation: null,
      target: null,
      workspace,
    };
  }

  public get connectionKey(): string | null {
    return this.key;
  }

  public get connection(): ConnectionIdentity | null {
    return this.identity;
  }

  public snapshot(): HostContextBase {
    return this.context;
  }

  public subscribe(listener: (context: HostContextBase) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** 连接标识变化时调用（清除云端授权、关闭执行会话）。Called when the identity changes. */
  public onConnectionChanged(listener: (key: string | null) => void): () => void {
    this.connectionListeners.add(listener);
    return () => this.connectionListeners.delete(listener);
  }

  public setApp(app: HostContext["app"]): void {
    this.replace({ app });
  }

  public setConnection(identity: ConnectionIdentity | null, server: ServerSummary | null): void {
    const key = identity ? connectionKeyOf(identity) : null;
    if (key === this.key) {
      if (server && JSON.stringify(server) !== JSON.stringify(this.context.server))
        this.replace({ server });
      return;
    }
    this.identity = identity ? { ...identity, baseUrl: normalizeBaseUrl(identity.baseUrl) } : null;
    this.key = key;
    for (const controller of this.pending) controller.abort(connectionChangedError());
    this.pending.clear();
    for (const listener of this.connectionListeners) listener(key);
    this.replace({
      server: server ?? { id: "", name: "", kind: "local" },
      user: null,
      project: null,
      conversation: null,
      target: null,
    });
  }

  /** 携带旧连接标识的写入被丢弃。Writes carrying an old connection key are dropped. */
  public setUser(connectionKey: string, user: ContextUser | null): void {
    if (connectionKey !== this.key) return;
    this.replace({ user });
  }

  public setSelection(connectionKey: string, selection: WorkspaceSelection): void {
    if (connectionKey !== this.key) return;
    this.replace(selection);
  }

  public setWorkspace(workspace: WorkspaceState): void {
    this.replace({ workspace });
  }

  /**
   * 宿主发出 Server 请求前同步对比 `getApiRuntime()` 与连接标识；标识变化时请求以 ERR_CONNECTION_CHANGED 结束。
   * Compares `getApiRuntime()` with the identity before a Server request; an identity change ends it with ERR_CONNECTION_CHANGED.
   */
  public async request<T>(
    operation: (signal: AbortSignal, identity: ConnectionIdentity) => Promise<T>,
    signal?: AbortSignal,
  ): Promise<T> {
    const identity = this.identity;
    const runtime = getApiRuntime();
    if (
      !identity ||
      normalizeBaseUrl(runtime.baseUrl) !== identity.baseUrl ||
      (runtime.token ?? null) !== identity.token
    ) {
      throw connectionChangedError();
    }
    const controller = new AbortController();
    const forwardAbort = () => controller.abort(signal?.reason);
    signal?.addEventListener("abort", forwardAbort, { once: true });
    if (signal?.aborted) controller.abort(signal.reason);
    this.pending.add(controller);
    try {
      const result = await Promise.race([
        operation(controller.signal, identity),
        new Promise<never>((_, reject) => {
          const fail = () => reject(this.abortReason(controller.signal));
          if (controller.signal.aborted) fail();
          else controller.signal.addEventListener("abort", fail, { once: true });
        }),
      ]);
      return result;
    } finally {
      this.pending.delete(controller);
      signal?.removeEventListener("abort", forwardAbort);
    }
  }

  private abortReason(signal: AbortSignal): Error {
    return signal.reason instanceof Error
      ? signal.reason
      : createExtensionError("ABORT_ERR", "The operation was aborted.");
  }

  private replace(changes: Partial<HostContextBase>): void {
    this.context = { ...this.context, ...changes };
    for (const listener of this.listeners) listener(this.context);
  }
}
