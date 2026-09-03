import {
  addTokenUsage,
  createUuidV7,
  EMPTY_TOKEN_USAGE,
  getMessageTokenUsage,
  type AiMessage,
  type TokenUsage,
} from "@agw/api";
import {
  buildConversationRenderModel,
  getAgentflowCheckpointMessage,
  getPendingHumanGate,
  prepareClaudeHistory,
  type AgentflowCheckpointAvailability,
  type ConversationRenderItem,
  type PendingHumanGate,
} from "@agw/chat-core";
import {
  getAgentMode,
  getLatestAgentMode,
  getMessageStreamingScopeId,
  getTurnFinishedStatus,
  isModeControlMessage,
  isUserTurnMessage,
  mergeStreamingMessage,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
  type AgentMode,
  type PermissionMode,
} from "@agw/execution-core";
import { createUserMessage, toExecutionUserInput, type ChatImageAttachment } from "@agw/chat-core";

import {
  ExecutionSession,
  type ExecutionHubHandlers,
  type ExecutionReconnectState,
  type ExecutionRuntimeConfig,
} from "./execution-session";

export type ConversationTarget = { id: string; type: "agent" | "agentflow" };
export type ConversationSessionSeed = {
  revision: string | number;
  conversationId: string | null;
  contextId: string | null;
  messages: AiMessage[];
  usage?: TokenUsage;
};

export type ConversationRuntimeAdapter = {
  execution: ExecutionRuntimeConfig;
  clearRecords?(projectId: string, contextId: string): Promise<void>;
  onConversationIdChange?(conversationId: string | null): void;
  onContextIdChange?(contextId: string | null): void;
  onConversationChange?(): void | Promise<void>;
  onError?(error: unknown): void;
  createSession?(handlers: ExecutionHubHandlers): ExecutionSession;
};

export type ConversationControllerOptions = {
  adapter: ConversationRuntimeAdapter;
  projectId: string | null;
  target: ConversationTarget | null;
  sessionSeed: ConversationSessionSeed;
  environmentVariables?: Record<string, string>;
  permissionMode?: PermissionMode;
};

export type ConversationControllerState = {
  conversationId: string | null;
  contextId: string | null;
  rawMessages: AiMessage[];
  items: ConversationRenderItem[];
  usage: TokenUsage;
  isExecuting: boolean;
  isTransitioning: boolean;
  reconnectState: ExecutionReconnectState | null;
  pendingHumanGate: PendingHumanGate | null;
  checkpointAvailability: AgentflowCheckpointAvailability[];
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  error: string | null;
};

type Listener = () => void;

export class ConversationController {
  private readonly listeners = new Set<Listener>();
  private options: ConversationControllerOptions;
  private state: ConversationControllerState;
  private session: ExecutionSession | null = null;
  private configuredContextId: string | null = null;
  private activeStreamingScopeId: string | null = null;
  private resumeBuffer: AiMessage[] | null = null;
  private disposed = false;

  public constructor(options: ConversationControllerOptions) {
    this.options = options;
    const history = prepareHistory(options.sessionSeed.messages);
    this.state = {
      conversationId: options.sessionSeed.conversationId,
      contextId: options.sessionSeed.contextId,
      rawMessages: history,
      items: [],
      usage: options.sessionSeed.usage ?? EMPTY_TOKEN_USAGE,
      isExecuting: false,
      isTransitioning: false,
      reconnectState: null,
      pendingHumanGate: null,
      checkpointAvailability: [],
      permissionMode: options.permissionMode ?? "fullAccess",
      agentMode: getLatestAgentMode(options.sessionSeed.messages),
      error: null,
    };
    this.rebuildItems();
  }

  public readonly subscribe = (listener: Listener): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public readonly getSnapshot = (): ConversationControllerState => this.state;

  public updateOptions(options: ConversationControllerOptions): void {
    const previousKey = `${this.options.projectId}:${this.options.target?.type}:${this.options.target?.id}`;
    const nextKey = `${options.projectId}:${options.target?.type}:${options.target?.id}`;
    this.options = options;
    if (previousKey !== nextKey) {
      void this.disposeSession();
      this.patch({ pendingHumanGate: null, checkpointAvailability: [] });
    }
  }

  public hydrate(seed: ConversationSessionSeed): void {
    this.activeStreamingScopeId = null;
    this.resumeBuffer = null;
    this.patch({
      conversationId: seed.conversationId,
      contextId: seed.contextId,
      rawMessages: prepareHistory(seed.messages),
      usage: seed.usage ?? EMPTY_TOKEN_USAGE,
      pendingHumanGate: null,
      checkpointAvailability: [],
      agentMode: getLatestAgentMode(seed.messages),
      isExecuting: false,
      isTransitioning: false,
      reconnectState: null,
      error: null,
    });
  }

  public async send(text: string, attachments: readonly ChatImageAttachment[]): Promise<void> {
    if (
      this.state.isExecuting ||
      !this.options.projectId ||
      !this.options.target ||
      (!text.trim() && attachments.length === 0)
    ) {
      return;
    }

    const conversationId = this.ensureConversationId();
    const contextId = this.ensureContextId();
    const userMessage = createUserMessage(text, attachments);
    const scopedUserMessage = scopeStreamingMessage(userMessage, userMessage.messageId);
    this.activeStreamingScopeId = userMessage.messageId;
    this.patch({
      rawMessages: [...this.state.rawMessages, scopedUserMessage],
      pendingHumanGate: null,
      isExecuting: true,
      error: null,
    });

    try {
      const session = await this.ensureSession(contextId);
      await session.execute({
        conversationId,
        agentId: this.options.target.id,
        agentType: this.options.target.type === "agentflow" ? 1 : 0,
        executionId: createUuidV7(),
        stream: true,
        input: toExecutionUserInput(userMessage),
      });
    } catch (error) {
      this.activeStreamingScopeId = null;
      this.fail(error);
    }
  }

  public async stop(reason = "Stop requested by user."): Promise<void> {
    try {
      await this.session?.interrupt(reason);
    } catch (error) {
      this.fail(error);
    }
  }

  public async clearRecords(): Promise<void> {
    const { contextId } = this.state;
    if (!contextId || !this.options.projectId) return;
    await this.options.adapter.clearRecords?.(this.options.projectId, contextId);
    this.patch({ rawMessages: [], usage: EMPTY_TOKEN_USAGE, pendingHumanGate: null });
  }

  public async setMode(mode: AgentMode): Promise<void> {
    if (!this.options.target || this.options.target.type !== "agent") return;
    const previous = this.state.agentMode;
    this.patch({ agentMode: mode });
    try {
      await (
        await this.ensureSession(this.ensureContextId())
      ).setMode(this.options.target.id, mode);
    } catch (error) {
      this.patch({ agentMode: previous });
      this.fail(error);
    }
  }

  public async setPermissionMode(mode: PermissionMode): Promise<void> {
    const previous = this.state.permissionMode;
    this.patch({ permissionMode: mode, isTransitioning: true });
    try {
      await (await this.ensureSession(this.ensureContextId())).setPermissionMode(mode);
      if (mode === "fullAccess" && this.state.pendingHumanGate?.requestType === "tool-approval") {
        this.patch({ pendingHumanGate: null });
      }
    } catch (error) {
      this.patch({ permissionMode: previous });
      this.fail(error);
    } finally {
      this.patch({ isTransitioning: false });
    }
  }

  public async submitHumanResponse(args: {
    approved: boolean;
    responseText?: string;
    approvalScope?: "once" | "always-tool" | "always-arguments";
    responseData?: unknown;
  }): Promise<void> {
    const request = this.state.pendingHumanGate;
    if (!request || !this.session) return;
    try {
      await this.session.submitHumanResponse({ requestId: request.requestId, ...args });
      if (this.state.pendingHumanGate?.requestId === request.requestId) {
        this.patch({ pendingHumanGate: null });
      }
    } catch (error) {
      this.fail(error);
    }
  }

  public async resumeCheckpoint(occurrenceId: string): Promise<void> {
    if (
      !this.options.projectId ||
      !this.options.target ||
      this.options.target.type !== "agentflow"
    ) {
      return;
    }
    const checkpointIndex = this.state.rawMessages.findIndex(
      (message) => getAgentflowCheckpointMessage(message)?.occurrenceId === occurrenceId,
    );
    if (checkpointIndex < 0) return;

    const resumeExecutionId = createUuidV7();
    this.resumeBuffer = [];
    this.patch({ isTransitioning: true, pendingHumanGate: null, error: null });
    try {
      const session = await this.ensureSession(this.ensureContextId());
      await session.resumeCheckpoint({
        checkpointOccurrenceId: occurrenceId,
        agentflowId: this.options.target.id,
        resumeExecutionId,
      });
      const retained = this.state.rawMessages.slice(0, checkpointIndex + 1);
      const buffered = this.resumeBuffer;
      this.resumeBuffer = null;
      this.patch({
        rawMessages: buffered ? buffered.reduce(mergeStreamingMessage, retained) : retained,
        isExecuting: true,
      });
    } catch (error) {
      this.resumeBuffer = null;
      this.fail(error);
    } finally {
      this.patch({ isTransitioning: false });
    }
  }

  public async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    await this.disposeSession();
    this.listeners.clear();
  }

  private async ensureSession(contextId: string): Promise<ExecutionSession> {
    if (!this.options.projectId) throw new Error("A project is required.");
    if (!this.session) {
      const handlers: ExecutionHubHandlers = {
        onMessage: (message) => this.receive(message),
        onError: (error) => this.fail(error),
        onClose: (error) => {
          this.patch({ isExecuting: false, reconnectState: null });
          if (error) this.fail(error);
        },
        onReconnecting: (state) => this.patch({ reconnectState: state }),
        onReconnectFailed: (state) => this.patch({ reconnectState: state }),
        onReconnected: () => this.patch({ reconnectState: null }),
      };
      this.session =
        this.options.adapter.createSession?.(handlers) ??
        new ExecutionSession(handlers, this.options.adapter.execution);
    }
    if (this.configuredContextId !== contextId) {
      await this.session.configure({
        projectId: this.options.projectId,
        contextId,
        permissionMode: this.state.permissionMode,
        environmentVariables: this.options.environmentVariables,
      });
      this.configuredContextId = contextId;
    }
    return this.session;
  }

  private receive(message: AiMessage): void {
    const mode = getAgentMode(message);
    if (mode) {
      this.patch({ agentMode: mode });
      if (isModeControlMessage(message)) return;
    }

    const usage = getMessageTokenUsage(message);
    if (usage) this.patch({ usage: addTokenUsage(this.state.usage, usage) });

    const humanGate = getPendingHumanGate(message);
    if (humanGate) {
      this.patch({
        pendingHumanGate: {
          ...humanGate,
          streamingScopeId: humanGate.streamingScopeId ?? this.activeStreamingScopeId ?? undefined,
        },
      });
      return;
    }

    if (message.additionalProperties?.type === "turn-start") {
      this.activeStreamingScopeId =
        getMessageStreamingScopeId(message) ?? this.activeStreamingScopeId ?? message.messageId;
      this.patch({ isExecuting: true });
      return;
    }

    const terminal = getTurnFinishedStatus(message);
    if (terminal) {
      this.activeStreamingScopeId = null;
      this.patch({
        isExecuting: false,
        pendingHumanGate: null,
        error: terminal === "failed" ? "Execution failed." : this.state.error,
      });
      void this.options.adapter.onConversationChange?.();
      return;
    }

    if (isUserTurnMessage(message)) return;
    const scoped = scopeStreamingMessage(
      message,
      getMessageStreamingScopeId(message) ?? this.activeStreamingScopeId ?? message.messageId,
    );
    if (this.resumeBuffer) this.resumeBuffer = mergeStreamingMessage(this.resumeBuffer, scoped);
    else this.patch({ rawMessages: mergeStreamingMessage(this.state.rawMessages, scoped) });

    if (getAgentflowCheckpointMessage(scoped)) void this.refreshCheckpoints();
  }

  private async refreshCheckpoints(): Promise<void> {
    if (!this.session || this.options.target?.type !== "agentflow") return;
    try {
      const checkpoints = await this.session.listAgentflowCheckpoints(this.options.target.id);
      this.patch({ checkpointAvailability: checkpoints });
    } catch {
      // A stale checkpoint list must not interrupt message streaming.
    }
  }

  private ensureContextId(): string {
    if (this.state.contextId) return this.state.contextId;
    const contextId = createUuidV7();
    this.patch({ contextId });
    this.options.adapter.onContextIdChange?.(contextId);
    return contextId;
  }

  private ensureConversationId(): string {
    if (this.state.conversationId) return this.state.conversationId;
    const conversationId = createUuidV7();
    this.patch({ conversationId });
    this.options.adapter.onConversationIdChange?.(conversationId);
    return conversationId;
  }

  private rebuildItems(): void {
    this.state = {
      ...this.state,
      items: buildConversationRenderModel(this.state.rawMessages, {
        pendingHumanGate: this.state.pendingHumanGate,
        checkpointAvailability: this.state.checkpointAvailability,
      }),
    };
  }

  private patch(patch: Partial<ConversationControllerState>): void {
    if (this.disposed) return;
    this.state = { ...this.state, ...patch };
    this.rebuildItems();
    for (const listener of this.listeners) listener();
  }

  private fail(error: unknown): void {
    const message = error instanceof Error ? error.message : String(error);
    this.patch({ error: message, isExecuting: false });
    this.options.adapter.onError?.(error);
  }

  private async disposeSession(): Promise<void> {
    const session = this.session;
    this.session = null;
    this.configuredContextId = null;
    if (session) await session.dispose().catch(() => undefined);
  }
}

function prepareHistory(messages: AiMessage[]): AiMessage[] {
  return scopeMessagesByUserTurn(prepareClaudeHistory(messages).messages);
}
