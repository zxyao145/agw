import { apiGet } from "@agw/api";
import { getProjectDirectories } from "@agw/projects-core";
import type {
  AgwDesktopBridge,
  ExtensionCaller,
  ExtensionIpcResult,
} from "@desktop/shared/contracts";
import type {
  ContextProject,
  ContextTarget,
  ExtensionFrameKind,
  ExtensionManifest,
  ExtensionWelcome,
  HostContext,
  ThemeInfo,
} from "@desktop/shared/extension-api";
import {
  type HostPermission,
  matchesHostPermissionHttpUrl,
} from "@desktop/shared/extension-host-permissions";
import type { ExtensionPermission } from "@desktop/shared/extension-permissions";
import {
  createExtensionError,
  fromExtensionErrorData,
  RpcEndpoint,
  type RpcPort,
} from "@desktop/shared/extension-rpc";
import type { A2AService } from "./a2a-service";
import { type CloudAccess, type CloudFileSystem, formatCloudUri } from "./cloud-file-system";
import { type ContextService, filterContextForPermissions } from "./context-service";
import type { ExecutionService, SessionOwner } from "./execution-service";
import type { WorkspaceLayoutStore } from "./workspace-layout-store";
import { resolveViewId } from "./workspace-ids";

/** broker 需要的 extension 当前状态。The extension state the broker needs. */
export type BrokerExtension = {
  manifest: ExtensionManifest;
  permissions: readonly ExtensionPermission[];
  hostPermissions: readonly HostPermission[];
  storageUri: string;
};

export type FrameConnectionInfo = {
  extensionId: string;
  frameId: string;
  pageId: string;
  kind: ExtensionFrameKind;
  viewId: string | null;
  params: unknown;
};

type FrameConnection = FrameConnectionInfo & {
  endpoint: RpcEndpoint;
  controllers: Set<AbortController>;
  disposeContext: () => void;
};

export type ExtensionBridgeBrokerOptions = {
  bridge(): AgwDesktopBridge;
  hostInstanceId: string;
  context: ContextService;
  layout: WorkspaceLayoutStore;
  cloud: CloudFileSystem;
  execution: ExecutionService;
  a2a: A2AService;
  extension(extensionId: string): BrokerExtension | null;
  theme(): ThemeInfo;
  isFrameClosing(frameId: string): boolean;
  insertComposerText(text: string): void;
  notify(
    extensionId: string,
    notification: { type: "info" | "success" | "error"; message: string },
  ): void;
  onPong(frameId: string, pageId: string): void;
};

type RequestHandler = (connection: FrameConnection, params: never, signal: AbortSignal) => unknown;

function unwrap<T>(result: ExtensionIpcResult<T>): T {
  if (result.ok) return result.value;
  throw fromExtensionErrorData(result.error);
}

function fileKind(uri: string): "local" | "cloud" {
  if (typeof uri === "string" && uri.startsWith("file:")) return "local";
  if (typeof uri === "string" && uri.startsWith("agw-cloud:")) return "cloud";
  throw createExtensionError("ERR_INVALID_URI", `Unsupported URI: ${String(uri)}`);
}

function frameClosing(): Error {
  return createExtensionError("ERR_FRAME_CLOSING", "The frame is closing.");
}

const ENGINE_KINDS: Record<number, ContextTarget["externalAgentKind"]> = {
  0: "maf",
  1: "claudeCode",
  2: "codex",
  3: "pi",
};

type AgentDto = {
  id: string;
  name: string;
  displayName?: string | null;
  description?: string | null;
  type?: number;
  externalAgentKind?: number | null;
};

type AgentflowDto = { id: string; name: string; description?: string | null };

type ProjectDto = {
  id: string;
  name: string;
  description?: string | null;
  workspace?: string | null;
  additionalDirectories?: { id: string; path: string; name?: string | null }[] | null;
};

export function toContextTarget(
  type: "agent" | "agentflow",
  value: AgentDto | AgentflowDto,
): ContextTarget {
  const agent = value as AgentDto;
  return {
    type,
    id: value.id,
    name: value.name,
    displayName: type === "agent" ? (agent.displayName ?? null) : null,
    description: value.description ?? null,
    externalAgentKind:
      type === "agent" && agent.type === 1
        ? (ENGINE_KINDS[agent.externalAgentKind ?? -1] ?? null)
        : null,
  };
}

export function toContextProject(serverId: string, project: ProjectDto): ContextProject {
  return {
    id: project.id,
    name: project.name,
    description: project.description ?? null,
    directories: getProjectDirectories(project as Parameters<typeof getProjectDirectories>[0]).map(
      (directory) => ({
        directoryId: directory.id,
        label: directory.name,
        uri: formatCloudUri({
          serverId,
          projectId: project.id,
          directoryId: directory.id,
          path: "",
        }),
      }),
    ),
  };
}

/**
 * 宿主中处理 extension 端口的 broker：按权限检查后在宿主完成操作，或交给主进程。
 * The host-side broker for extension ports: checks permissions, then acts in the host or hands off to main.
 */
export class ExtensionBridgeBroker {
  private readonly connections = new Map<string, FrameConnection>();
  private readonly handlers: Record<string, RequestHandler>;

  public constructor(private readonly options: ExtensionBridgeBrokerOptions) {
    this.handlers = this.createHandlers();
  }

  /** 接入端口并发送 `welcome`。Attaches a port and sends `welcome`. */
  public connect(info: FrameConnectionInfo, port: MessagePort): void {
    this.disconnect(info.frameId);
    const controllers = new Set<AbortController>();
    const connection = { ...info, controllers } as FrameConnection;
    connection.endpoint = new RpcEndpoint(port as unknown as RpcPort, {
      request: (method, params, signal) => this.handle(connection, method, params, signal),
      pong: () => this.options.onPong(info.frameId, info.pageId),
    });
    connection.disposeContext = this.options.context.subscribe(() => this.sendContext(connection));
    this.connections.set(info.frameId, connection);
    const extension = this.requireExtension(info.extensionId);
    const welcome: ExtensionWelcome = {
      frame: { frameId: info.frameId, kind: info.kind, viewId: info.viewId, params: info.params },
      manifest: extension.manifest,
      context: this.contextFor(info.extensionId),
      theme: this.options.theme(),
    };
    connection.endpoint.emit("welcome", welcome);
  }

  public isConnected(frameId: string, pageId?: string): boolean {
    const connection = this.connections.get(frameId);
    return connection !== undefined && (pageId === undefined || connection.pageId === pageId);
  }

  /** 关闭端口并中止它进行中的云端文件、A2A、HTTP 请求。Closes the port and aborts its in-flight requests. */
  public disconnect(frameId: string, pageId?: string): void {
    const connection = this.connections.get(frameId);
    if (!connection || (pageId !== undefined && connection.pageId !== pageId)) return;
    this.connections.delete(frameId);
    connection.disposeContext();
    for (const controller of connection.controllers) controller.abort(frameClosing());
    connection.endpoint.close("ERR_FRAME_GONE", "The frame is gone.");
  }

  /** 宿主调用 frame 中的钩子或命令。The host calls a hook or command in the frame. */
  public call(frameId: string, method: string, params: unknown): Promise<unknown> {
    const connection = this.connections.get(frameId);
    if (!connection)
      return Promise.reject(createExtensionError("ERR_FRAME_GONE", "The frame is not connected."));
    return connection.endpoint.request(method, params);
  }

  public ping(frameId: string, seq: number): void {
    this.connections.get(frameId)?.endpoint.ping(seq);
  }

  public emit(frameId: string, pageId: string | null, name: string, data: unknown): void {
    const connection = this.connections.get(frameId);
    if (!connection || (pageId !== null && connection.pageId !== pageId)) return;
    connection.endpoint.emit(name, data);
  }

  public broadcastTheme(theme: ThemeInfo): void {
    for (const connection of this.connections.values())
      connection.endpoint.emit("theme.changed", theme);
  }

  public deliverExecution(owner: SessionOwner, event: string, data: unknown): void {
    this.emit(owner.frameId, owner.pageId, event, data);
  }

  private contextFor(extensionId: string): HostContext {
    const extension = this.requireExtension(extensionId);
    return filterContextForPermissions(
      {
        ...this.options.context.snapshot(),
        extension: {
          id: extensionId,
          version: extension.manifest.version,
          manifestVersion: extension.manifest.manifestVersion,
          storageUri: extension.storageUri,
        },
      },
      extension.permissions,
    );
  }

  private sendContext(connection: FrameConnection): void {
    if (!this.options.extension(connection.extensionId)) return;
    connection.endpoint.emit("context.changed", this.contextFor(connection.extensionId));
  }

  private requireExtension(extensionId: string): BrokerExtension {
    const extension = this.options.extension(extensionId);
    if (!extension)
      throw createExtensionError(
        "ERR_EXTENSION_DISABLED",
        `Extension ${extensionId} is not active.`,
      );
    return extension;
  }

  private caller(connection: FrameConnection): ExtensionCaller {
    return {
      hostInstanceId: this.options.hostInstanceId,
      extensionId: connection.extensionId,
      frameId: connection.frameId,
      pageId: connection.pageId,
    };
  }

  private owner(connection: FrameConnection): SessionOwner {
    return {
      extensionId: connection.extensionId,
      frameId: connection.frameId,
      pageId: connection.pageId,
    };
  }

  private requirePermission(
    connection: FrameConnection,
    permission: ExtensionPermission,
  ): BrokerExtension {
    const extension = this.requireExtension(connection.extensionId);
    if (!extension.permissions.includes(permission)) {
      throw createExtensionError(
        "ERR_PERMISSION_DENIED",
        `This call requires the ${permission} permission.`,
      );
    }
    return extension;
  }

  private cloudAccess(connection: FrameConnection): CloudAccess {
    return {
      extensionId: connection.extensionId,
      permissions: this.requireExtension(connection.extensionId).permissions,
    };
  }

  /** 为一次性请求登记 AbortController，frame 关闭或页面替换时中止。Tracks a one-shot request for frame closing. */
  private async tracked<T>(
    connection: FrameConnection,
    signal: AbortSignal,
    operation: (signal: AbortSignal) => Promise<T>,
  ): Promise<T> {
    const controller = new AbortController();
    const forward = () => controller.abort(signal.reason);
    signal.addEventListener("abort", forward, { once: true });
    connection.controllers.add(controller);
    try {
      return await operation(controller.signal);
    } finally {
      connection.controllers.delete(controller);
      signal.removeEventListener("abort", forward);
    }
  }

  private async handle(
    connection: FrameConnection,
    method: string,
    params: unknown,
    signal: AbortSignal,
  ) {
    const handler = this.handlers[method];
    if (!handler) throw createExtensionError("ERR_UNKNOWN_METHOD", `Unknown method: ${method}`);
    return handler(connection, (params ?? {}) as never, signal);
  }

  private createHandlers(): Record<string, RequestHandler> {
    const { bridge, context, layout, cloud, execution, a2a } = this.options;
    // bridge 按需取得：只有已连接的 frame 才会调用，普通浏览器中没有 frame。
    // The bridge is resolved on use: only connected frames call it, and a browser has none.
    const local = <T>(
      connection: FrameConnection,
      operation: (caller: ExtensionCaller) => Promise<ExtensionIpcResult<T>>,
    ) => operation(this.caller(connection)).then(unwrap);

    return {
      "log.error": (connection, params: { message: string; stack: string | null }) => {
        console.error(
          `[extension ${connection.extensionId}/${connection.viewId ?? connection.kind}] ${params.message}`,
          params.stack ?? "",
        );
        return null;
      },

      "context.listProjects": (connection, _params, signal) => {
        this.requirePermission(connection, "context.project");
        return this.tracked(connection, signal, (requestSignal) =>
          context.request(async (contextSignal) => {
            const projects = (await apiGet("/api/projects", {
              signal: contextSignal,
            })) as ProjectDto[];
            return projects.map((project) =>
              toContextProject(context.snapshot().server.id, project),
            );
          }, requestSignal),
        );
      },
      "context.listTargets": (connection, _params, signal) => {
        this.requirePermission(connection, "context.agent");
        return this.tracked(connection, signal, (requestSignal) =>
          context.request(async (contextSignal) => {
            const [agents, agentflows] = await Promise.all([
              apiGet("/api/agents", { signal: contextSignal }) as Promise<AgentDto[]>,
              apiGet("/api/agentflows", { signal: contextSignal }) as Promise<AgentflowDto[]>,
            ]);
            return [
              ...agents.map((agent) => toContextTarget("agent", agent)),
              ...agentflows.map((agentflow) => toContextTarget("agentflow", agentflow)),
            ];
          }, requestSignal),
        );
      },

      "workspace.showSidebarView": (connection, params: { id: string }) => {
        layout.showSidebarView(resolveViewId(connection.extensionId, params.id));
        return null;
      },
      "workspace.toggleSidebarView": (connection, params: { id: string }) => {
        layout.toggleSidebarView(resolveViewId(connection.extensionId, params.id));
        return null;
      },
      "workspace.setSidebarVisible": (_connection, params: { visible: boolean }) => {
        layout.setSidebarVisible(Boolean(params.visible));
        return null;
      },
      "workspace.openMainView": (connection, params: { id: string; params?: unknown }) => {
        layout.openMainView(resolveViewId(connection.extensionId, params.id), params.params ?? {});
        return null;
      },

      "fs.readFile": (connection, params: { uri: string }, signal) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, { operation: "readFile", uri: params.uri }),
            )
          : this.tracked(connection, signal, (requestSignal) =>
              cloud.readFile(this.cloudAccess(connection), params.uri, requestSignal),
            ),
      "fs.writeFile": (
        connection,
        params: { uri: string; data: Uint8Array; overwrite: boolean; createParents: boolean },
        signal,
      ) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, {
                operation: "writeFile",
                uri: params.uri,
                data: params.data,
                overwrite: params.overwrite,
                createParents: params.createParents,
              }),
            )
          : this.tracked(connection, signal, (requestSignal) =>
              cloud.writeFile(
                this.cloudAccess(connection),
                params.uri,
                params.data,
                { overwrite: params.overwrite, createParents: params.createParents },
                requestSignal,
              ),
            ),
      "fs.readDirectory": (connection, params: { uri: string }, signal) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, { operation: "readDirectory", uri: params.uri }),
            )
          : this.tracked(connection, signal, () =>
              cloud.readDirectory(this.cloudAccess(connection), params.uri),
            ),
      "fs.stat": (connection, params: { uri: string }, signal) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, { operation: "stat", uri: params.uri }),
            )
          : this.tracked(connection, signal, () =>
              cloud.stat(this.cloudAccess(connection), params.uri),
            ),
      "fs.exists": (connection, params: { uri: string }, signal) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, { operation: "exists", uri: params.uri }),
            )
          : this.tracked(connection, signal, () =>
              cloud.exists(this.cloudAccess(connection), params.uri),
            ),
      "fs.createDirectory": (connection, params: { uri: string; recursive: boolean }, signal) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, {
                operation: "createDirectory",
                uri: params.uri,
                recursive: params.recursive,
              }),
            )
          : this.tracked(connection, signal, () =>
              cloud.createDirectory(this.cloudAccess(connection), params.uri, params.recursive),
            ),
      "fs.delete": (connection, params: { uri: string; recursive: boolean }, signal) =>
        fileKind(params.uri) === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, {
                operation: "delete",
                uri: params.uri,
                recursive: params.recursive,
              }),
            )
          : this.tracked(connection, signal, () =>
              cloud.delete(this.cloudAccess(connection), params.uri, params.recursive),
            ),
      "fs.rename": (
        connection,
        params: { from: string; to: string; overwrite: boolean },
        signal,
      ) => {
        const kind = fileKind(params.from);
        if (kind !== fileKind(params.to)) {
          throw createExtensionError(
            "ERR_CROSS_DEVICE",
            "Files can only move within one file system.",
          );
        }
        return kind === "local"
          ? local(connection, (caller) =>
              bridge().extensionFileSystem(caller, {
                operation: "rename",
                from: params.from,
                to: params.to,
                overwrite: params.overwrite,
              }),
            )
          : this.tracked(connection, signal, () =>
              cloud.rename(this.cloudAccess(connection), params.from, params.to, params.overwrite),
            );
      },
      "fs.watch": async (connection, params: { uri: string; recursive: boolean }) => {
        if (this.options.isFrameClosing(connection.frameId)) throw frameClosing();
        if (fileKind(params.uri) === "cloud") {
          throw createExtensionError(
            "ERR_UNSUPPORTED_OPERATION",
            "Only local files can be watched.",
          );
        }
        const watchId = crypto.randomUUID();
        await local(connection, (caller) =>
          bridge().extensionWatch(caller, {
            watchId,
            uri: params.uri,
            recursive: params.recursive,
          }),
        );
        return { watchId };
      },
      "fs.unwatch": (connection, params: { watchId: string }) =>
        local(connection, (caller) => bridge().extensionUnwatch(caller, params.watchId)),

      "dialog.showOpenDialog": (connection, params) =>
        local(connection, (caller) => bridge().showExtensionOpenDialog(caller, params)),
      "dialog.showSaveDialog": (connection, params) =>
        local(connection, (caller) => bridge().showExtensionSaveDialog(caller, params)),
      "shell.revealInFileManager": (connection, params: { uri: string }) => {
        if (fileKind(params.uri) === "cloud") {
          throw createExtensionError(
            "ERR_UNSUPPORTED_OPERATION",
            "Only local files can be revealed.",
          );
        }
        return local(connection, (caller) => bridge().revealExtensionPath(caller, params.uri));
      },

      "execution.createSession": (
        connection,
        params: Parameters<ExecutionService["createSession"]>[1],
        signal,
      ) => {
        this.requirePermission(connection, "execution");
        if (this.options.isFrameClosing(connection.frameId)) throw frameClosing();
        return execution.createSession(this.owner(connection), params, signal);
      },
      "execution.send": (
        connection,
        params: { sessionId: string } & Parameters<ExecutionService["send"]>[2],
      ) => {
        this.requirePermission(connection, "execution");
        return execution.send(this.owner(connection), params.sessionId, params);
      },
      "execution.interrupt": (connection, params: { sessionId: string }) => {
        this.requirePermission(connection, "execution");
        return execution.interrupt(this.owner(connection), params.sessionId);
      },
      "execution.respond": (
        connection,
        params: { sessionId: string; response: Parameters<ExecutionService["respond"]>[2] },
      ) => {
        this.requirePermission(connection, "execution");
        return execution.respond(this.owner(connection), params.sessionId, params.response);
      },
      "execution.setPermissionMode": (
        connection,
        params: { sessionId: string; mode: Parameters<ExecutionService["setPermissionMode"]>[2] },
      ) => {
        this.requirePermission(connection, "execution");
        return execution.setPermissionMode(this.owner(connection), params.sessionId, params.mode);
      },
      "execution.dispose": (connection, params: { sessionId: string }) => {
        this.requirePermission(connection, "execution");
        return execution.dispose(this.owner(connection), params.sessionId);
      },

      "a2a.listAgents": (connection, _params, signal) => {
        this.requirePermission(connection, "a2a");
        return this.tracked(connection, signal, (requestSignal) => a2a.listAgents(requestSignal));
      },
      "a2a.getAgentCard": (connection, params: { agentName: string }, signal) => {
        this.requirePermission(connection, "a2a");
        return this.tracked(connection, signal, (requestSignal) =>
          a2a.getAgentCard(params.agentName, requestSignal),
        );
      },
      "a2a.request": (
        connection,
        params: { agentName: string; method: string; params?: unknown },
        signal,
      ) => {
        this.requirePermission(connection, "a2a");
        return this.tracked(connection, signal, (requestSignal) =>
          a2a.request(params.agentName, params.method, params.params, requestSignal),
        );
      },
      "a2a.stream": (
        connection,
        params: { streamId: string; agentName: string; method: string; params?: unknown },
        signal,
      ) => {
        this.requirePermission(connection, "a2a");
        return this.tracked(connection, signal, (requestSignal) =>
          a2a.stream(
            params.agentName,
            params.method,
            params.params,
            (event) => connection.endpoint.emit("a2a.event", { streamId: params.streamId, event }),
            requestSignal,
          ),
        );
      },

      "http.request": async (
        connection,
        params: {
          url: string;
          method?: string;
          headers?: Record<string, string>;
          body?: string | Uint8Array;
        },
        signal,
      ) => {
        const extension = this.requireExtension(connection.extensionId);
        if (!matchesHostPermissionHttpUrl(extension.hostPermissions, params.url)) {
          throw createExtensionError(
            "ERR_HOST_NOT_PERMITTED",
            `${params.url} does not match an approved host permission.`,
          );
        }
        const requestId = crypto.randomUUID();
        const cancel = () => void bridge().cancelExtensionHttpRequest(requestId);
        signal.addEventListener("abort", cancel, { once: true });
        try {
          return await local(connection, (caller) =>
            bridge().extensionHttpRequest(caller, { requestId, ...params }),
          );
        } finally {
          signal.removeEventListener("abort", cancel);
        }
      },

      "chat.composer.insertText": (connection, params: { text: string }) => {
        this.requirePermission(connection, "chat.composer");
        this.options.insertComposerText(String(params.text));
        return null;
      },

      "ui.notify": (
        connection,
        params: { type: "info" | "success" | "error"; message: string },
      ) => {
        this.options.notify(connection.extensionId, params);
        return null;
      },
      "ui.openExternal": async (connection, params: { url: string }) => {
        const extension = this.requireExtension(connection.extensionId);
        if (!matchesHostPermissionHttpUrl(extension.hostPermissions, params.url)) {
          throw createExtensionError(
            "ERR_HOST_NOT_PERMITTED",
            `${params.url} does not match an approved host permission.`,
          );
        }
        await bridge().openExternal(params.url);
        return null;
      },
    };
  }
}
