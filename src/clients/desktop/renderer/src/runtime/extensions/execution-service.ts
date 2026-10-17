import { createUuidV7 } from "@agw/api";
import {
  createUserTextMessage,
  ExecutionSession,
  type ExecutionHubHandlers,
  type ExecutionRuntimeConfig,
  toExecutionUserInput,
} from "@agw/chat-runtime";
import {
  getTurnFinishedStatus,
  isTurnStartMessage,
  type ExecutionMessage,
  type InteractionResponse,
  type PermissionMode,
} from "@agw/execution-core";
import type { ExecutionCloseReason } from "@desktop/shared/extension-api";
import { createExtensionError } from "@desktop/shared/extension-rpc";
import type { ContextService } from "./context-service";

export const MAX_SESSIONS_PER_EXTENSION = 4;

/** 会话所属的 frame 页面。The frame page that owns a session. */
export type SessionOwner = { extensionId: string; frameId: string; pageId: string };

/** `ExecutionSession` 中服务使用的部分。The part of `ExecutionSession` the service uses. */
export type ExecutionSessionLike = Pick<
  ExecutionSession,
  "configure" | "execute" | "interrupt" | "submitHumanResponse" | "setPermissionMode" | "dispose"
>;

export type ExecutionServiceOptions = {
  context: ContextService;
  /** 只发给会话所属的 frame 页面。Delivered only to the owning frame page. */
  deliver(
    owner: SessionOwner,
    event: "execution.message" | "execution.closed",
    data: unknown,
  ): void;
  /** 会话的 Project 是当前 Project 时刷新对话列表。Refreshes the conversation list for the current Project. */
  refreshConversations(projectId: string): void;
  createSession?(
    handlers: ExecutionHubHandlers,
    runtime: ExecutionRuntimeConfig,
  ): ExecutionSessionLike;
};

type SessionRecord = {
  sessionId: string;
  owner: SessionOwner;
  projectId: string;
  conversationId: string;
  session: ExecutionSessionLike;
};

type PendingCreation = { owner: SessionOwner; controller: AbortController };

function isExecutionBusy(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return (
    message.includes("4090021") ||
    message.includes("already has a running task") ||
    message.includes("The previous execution is currently in progress")
  );
}

/** extension 的执行会话：每个会话一条独立的 SignalR 连接。Extension execution sessions, each with its own SignalR connection. */
export class ExecutionService {
  private readonly sessions = new Map<string, SessionRecord>();
  private readonly pending = new Set<PendingCreation>();

  public constructor(private readonly options: ExecutionServiceOptions) {
    options.context.onConnectionChanged(() =>
      this.closeWhere(() => true, "connection-changed", true),
    );
  }

  public count(predicate: (owner: SessionOwner) => boolean): number {
    return [...this.sessions.values()].filter((record) => predicate(record.owner)).length;
  }

  public async createSession(
    owner: SessionOwner,
    options: {
      projectId: string;
      conversationId?: string;
      environmentVariables?: Record<string, string>;
      permissionMode?: PermissionMode;
    },
    signal: AbortSignal,
  ): Promise<{ sessionId: string; conversationId: string }> {
    const open =
      this.count((item) => item.extensionId === owner.extensionId) +
      [...this.pending].filter((item) => item.owner.extensionId === owner.extensionId).length;
    if (open >= MAX_SESSIONS_PER_EXTENSION) {
      throw createExtensionError(
        "ERR_TOO_MANY_SESSIONS",
        `An extension can open at most ${MAX_SESSIONS_PER_EXTENSION} execution sessions.`,
      );
    }
    const pending: PendingCreation = { owner, controller: new AbortController() };
    const abort = () => pending.controller.abort(signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
    this.pending.add(pending);

    const sessionId = crypto.randomUUID();
    const conversationId = options.conversationId ?? createUuidV7();
    let session: ExecutionSessionLike | null = null;
    try {
      return await this.options.context.request(async (requestSignal, identity) => {
        const handlers: ExecutionHubHandlers = {
          onMessage: (message) => this.receive(sessionId, message),
          onReconnectFailed: () => this.closeSession(sessionId, "connection-lost", true),
        };
        // SignalR 的 stop 等待启动结束；中止时直接关闭仍在握手的 WebSocket，使连接立即结束。
        // SignalR's stop waits for start to finish; an abort closes sockets still handshaking so the connection ends now.
        const sockets = new Set<WebSocket>();
        const TrackedWebSocket = class extends WebSocket {
          public constructor(url: string | URL, protocols?: string | string[]) {
            super(url, protocols);
            sockets.add(this);
            this.addEventListener("close", () => sockets.delete(this));
          }
        };
        const runtime: ExecutionRuntimeConfig = {
          baseUrl: identity.baseUrl,
          token: identity.token,
          attachmentStore: null,
          webSocket: TrackedWebSocket,
        };
        session =
          this.options.createSession?.(handlers, runtime) ??
          new ExecutionSession(handlers, runtime);
        const created = session;
        const stop = () => {
          for (const socket of sockets)
            if (socket.readyState === WebSocket.CONNECTING) socket.close();
          void created.dispose();
        };
        requestSignal.addEventListener("abort", stop, { once: true });
        await created.configure({
          projectId: options.projectId,
          conversationId,
          environmentVariables: options.environmentVariables,
          permissionMode: options.permissionMode ?? "alwaysAsk",
        });
        requestSignal.removeEventListener("abort", stop);
        if (requestSignal.aborted) throw requestSignal.reason;
        this.sessions.set(sessionId, {
          sessionId,
          owner,
          projectId: options.projectId,
          conversationId,
          session: created,
        });
        return { sessionId, conversationId };
      }, pending.controller.signal);
    } catch (error) {
      if (session && !this.sessions.has(sessionId))
        await (session as ExecutionSessionLike).dispose();
      throw error;
    } finally {
      this.pending.delete(pending);
      signal.removeEventListener("abort", abort);
    }
  }

  public async send(
    owner: SessionOwner,
    sessionId: string,
    input: { target: { type: "agent" | "agentflow"; id: string }; text: string },
  ): Promise<{ turnId: string }> {
    const record = this.owned(owner, sessionId);
    const turnId = createUuidV7();
    try {
      await record.session.execute({
        agentId: input.target.id,
        agentType: input.target.type === "agent" ? 0 : 1,
        conversationId: record.conversationId,
        turnId,
        input: toExecutionUserInput(createUserTextMessage(input.text)),
      });
    } catch (error) {
      if (isExecutionBusy(error)) {
        throw createExtensionError(
          "ERR_EXECUTION_BUSY",
          "The previous turn of this conversation is still running.",
        );
      }
      throw error;
    }
    return { turnId };
  }

  public async interrupt(owner: SessionOwner, sessionId: string): Promise<null> {
    await this.owned(owner, sessionId).session.interrupt();
    return null;
  }

  public async respond(
    owner: SessionOwner,
    sessionId: string,
    response: InteractionResponse,
  ): Promise<null> {
    await this.owned(owner, sessionId).session.submitHumanResponse({ response });
    return null;
  }

  public async setPermissionMode(
    owner: SessionOwner,
    sessionId: string,
    mode: PermissionMode,
  ): Promise<null> {
    await this.owned(owner, sessionId).session.setPermissionMode(mode);
    return null;
  }

  public async dispose(owner: SessionOwner, sessionId: string): Promise<null> {
    this.owned(owner, sessionId);
    await this.closeSession(sessionId, "disposed", true);
    return null;
  }

  /** 中止该 frame 尚未完成的 createSession。Aborts the frame's unfinished createSession calls. */
  public abortPendingCreations(frameId: string, reason: Error): void {
    for (const pending of this.pending) {
      if (pending.owner.frameId === frameId) pending.controller.abort(reason);
    }
  }

  /** 关闭 frame 拥有的会话；`notify` 为 true 时发送 `execution.closed`。Closes the frame's sessions. */
  public closeFrame(frameId: string, reason: ExecutionCloseReason, notify: boolean): Promise<void> {
    return this.closeWhere((owner) => owner.frameId === frameId, reason, notify);
  }

  /** 页面被替换：旧页面已经不存在，不发送 `execution.closed`。A replaced page no longer exists, so nothing is sent. */
  public closePage(frameId: string, pageId: string): Promise<void> {
    return this.closeWhere(
      (owner) => owner.frameId === frameId && owner.pageId === pageId,
      "frame-unloaded",
      false,
    );
  }

  public closeExtension(
    extensionId: string,
    reason: ExecutionCloseReason,
    notify: boolean,
  ): Promise<void> {
    return this.closeWhere((owner) => owner.extensionId === extensionId, reason, notify);
  }

  private owned(owner: SessionOwner, sessionId: string): SessionRecord {
    const record = this.sessions.get(sessionId);
    if (!record || record.owner.frameId !== owner.frameId || record.owner.pageId !== owner.pageId) {
      throw createExtensionError("ERR_SESSION_CLOSED", "The execution session is closed.");
    }
    return record;
  }

  private async closeWhere(
    predicate: (owner: SessionOwner) => boolean,
    reason: ExecutionCloseReason,
    notify: boolean,
  ): Promise<void> {
    const sessionIds = [...this.sessions.values()]
      .filter((record) => predicate(record.owner))
      .map((record) => record.sessionId);
    await Promise.all(sessionIds.map((sessionId) => this.closeSession(sessionId, reason, notify)));
  }

  /** 先关闭 SignalR 连接，再通知所属 frame。Closes the SignalR connection first, then notifies the owner. */
  private async closeSession(
    sessionId: string,
    reason: ExecutionCloseReason,
    notify: boolean,
  ): Promise<void> {
    const record = this.sessions.get(sessionId);
    if (!record) return;
    this.sessions.delete(sessionId);
    await record.session.dispose();
    if (notify) this.options.deliver(record.owner, "execution.closed", { sessionId, reason });
  }

  private receive(sessionId: string, message: ExecutionMessage): void {
    const record = this.sessions.get(sessionId);
    if (!record) return;
    this.options.deliver(record.owner, "execution.message", { sessionId, message });
    const currentProject = this.options.context.snapshot().project;
    if (
      currentProject?.id === record.projectId &&
      (isTurnStartMessage(message) || getTurnFinishedStatus(message) !== null)
    ) {
      this.options.refreshConversations(record.projectId);
    }
  }
}
