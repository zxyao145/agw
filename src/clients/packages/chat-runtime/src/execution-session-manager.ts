import type { HumanResponseCommandInput } from "@agw/execution-core";
import {
  ExecutionSession,
  ExecutionStartRejectedError,
  getPendingInteraction,
  getTurnFinishedStatus,
  type ExecutionHubHandlers,
  type ExecutionReconnectState,
  type AgentMode,
  type PermissionMode,
  type ExecutionRequest,
  type ExecutionSetting,
  type ExecutionConfigurationResult,
  type AgentflowCheckpointAvailability,
  type TurnFinishedStatus,
} from "./execution-session";
import { createUuidV7, type AiMessage } from "@agw/api";
import { toExecutionUserInput } from "@agw/chat-core";
import {
  cloneMessage,
  getMessageStreamingScopeId,
  getTurnIdentity,
  isSupersededTurnMessage,
  isTurnStartMessage,
  mergeStreamingMessages,
  scopeStreamingMessage,
} from "@agw/execution-core";
import { ConversationStatusStore } from "./conversation-status-store";
import {
  ExecutionActivityStore,
  getExecutionSessionKey,
  type ExecutionSessionKey,
  type ExecutionStatus,
} from "./execution-activity-store";
import {
  EMPTY_EXECUTION_QUEUE,
  isSubmittableInput,
  type ExecutionQueueSnapshot,
  type ExecutionSubmission,
  type QueuedExecution,
  type SubmissionResult,
} from "./execution-queue";

type ExecutionClient = Pick<
  ExecutionSession,
  | "configure"
  | "execute"
  | "listAgentflowCheckpoints"
  | "resumeCheckpoint"
  | "setMode"
  | "setPermissionMode"
  | "interrupt"
  | "interruptAndWait"
  | "submitHumanResponse"
  | "retryConnection"
  | "hasActiveExecution"
  | "dispose"
>;

type ClientFactory = (handlers: ExecutionHubHandlers) => ExecutionClient;

/** 开始发送一条队列条目：它的用户消息此时进入消息区域。Sending of a queue entry began; its user message enters the message area now. */
export type SubmissionStartedEvent = {
  item: QueuedExecution;
  message: AiMessage;
};

/** 发送的条目回到队首：确认没有执行，或结果无法确认。A sent entry returned to the queue head: confirmed not run, or its outcome is unknown. */
export type SubmissionReturnedEvent = {
  item: QueuedExecution;
  error: Error;
};

export type ManagedExecutionHandlers = ExecutionHubHandlers & {
  onSubmissionStarted?: (event: SubmissionStartedEvent) => void;
  onSubmissionReturned?: (event: SubmissionReturnedEvent) => void;
};

/** 已开始发送、尚未结束的条目；started 表示已收到它的 Turn 开始消息。An entry being sent and not yet finished; started means its turn start message arrived. */
type InFlightSubmission = {
  item: QueuedExecution;
  started: boolean;
};

type Entry = {
  key: ExecutionSessionKey;
  client: ExecutionClient;
  handler: ManagedExecutionHandlers | null;
  pendingMessages: AiMessage[];
  pendingInteractions: Map<string, AiMessage>;
  reconnectState: ExecutionReconnectState | null;
  activeTurn: ActiveTurnState | null;
  /** 服务端连接当前使用的设置；新连接或连接关闭后为空。The settings the server connection uses now; empty for a new or closed connection. */
  appliedSetting: ExecutionSetting | null;
  /** 下一轮要使用的设置；与 appliedSetting 不同时不发送队列条目。The settings the next turn must use; queue entries are not sent while it differs from appliedSetting. */
  requestedSetting: ExecutionSetting | null;
  /** 正在进行的配置数量。The number of configurations in progress. */
  configuring: number;
  /** 最近一次应用 requestedSetting 失败；再次配置、恢复队列或提交前不自动重试。The last application of requestedSetting failed; it is not retried automatically until the next configure, resume or submit. */
  configurationFailed: boolean;
  queue: QueuedExecution[];
  queuePaused: boolean;
  pauseReason: string | null;
  editingItemId: string | null;
  stopping: boolean;
  inFlight: InFlightSubmission | null;
  queueSnapshot: ExecutionQueueSnapshot;
};

type ActiveTurnState = ActiveTurnSnapshot & {
  replayable: boolean;
};

export type ActiveTurnSnapshot = {
  streamingScopeId: string;
  messages: AiMessage[];
};

export type TurnFinishedEvent = {
  key: ExecutionSessionKey;
  status: TurnFinishedStatus;
};

export type ManagedExecutionHandle = {
  matchesKey(key: ExecutionSessionKey): boolean;
  /**
   * 调用时同步记录下一轮要使用的设置，生效前队列不发送。连接空闲、尚未配置或设置未变化时立即发送；Turn 运行期间设置变化时，Turn 结束后先应用新设置再发送队列条目，返回的结果不代表已生效。
   * Records the settings the next turn must use synchronously on call, and the queue does not send before they apply. They are sent at once when the connection is idle, not configured yet, or unchanged; a change during a running turn is applied after it ends and before the next queue entry, and the returned result does not mean it applied.
   */
  configure(setting: ExecutionSetting): Promise<ExecutionConfigurationResult>;
  /**
   * 提交一条输入：会话空闲且队列可发送时立即开始，否则排到队尾。无效输入或正在终止时抛出错误，调用方保留草稿。
   * Submits an input: it starts at once when the conversation is idle and the queue can send, otherwise it joins the tail. Invalid input or a pending stop throws, and the caller keeps its draft.
   */
  submit(submission: ExecutionSubmission): SubmissionResult;
  getQueue(): ExecutionQueueSnapshot;
  updateQueuedItem(itemId: string, text: string): void;
  removeQueuedItem(itemId: string): void;
  /** 标记正在编辑的条目；编辑队首时等待保存或取消。Marks the entry being edited; an edited head waits for save or cancel. */
  setEditingQueuedItem(itemId: string | null): void;
  resumeQueue(): void;
  /** 清空队列、取消尚未开始的发送，再请求终止执行。Clears the queue, cancels sends not yet started, then requests the stop. */
  stop(reason?: string): Promise<void>;
  listAgentflowCheckpoints(agentflowId: string): Promise<AgentflowCheckpointAvailability[]>;
  resumeCheckpoint(args: {
    checkpointOccurrenceId: string;
    agentflowId: string;
    resumeExecutionId?: string;
  }): Promise<string>;
  setMode(agentId: string, mode: AgentMode): Promise<void>;
  setPermissionMode(permissionMode: PermissionMode): Promise<void>;
  interrupt(reason?: string): Promise<void>;
  interruptAndWait(reason?: string): Promise<void>;
  submitHumanResponse(args: HumanResponseCommandInput): Promise<void>;
  getStatus(): ExecutionStatus;
  getReconnectState(): ExecutionReconnectState | null;
  getActiveTurnSnapshot(): ActiveTurnSnapshot | null;
  detach(): void;
  dispose(): Promise<void>;
};

const FAILED_TURN_PAUSE_REASON = "The previous message failed. The queue is paused.";
const INTERRUPTED_TURN_PAUSE_REASON = "The previous message was interrupted. The queue is paused.";
const UNKNOWN_OUTCOME_PAUSE_REASON =
  "The connection was lost before the message finished, so its outcome is unknown. The queue is paused.";
const SUPERSEDED_TURN_PAUSE_REASON =
  "A newer message was sent to this conversation from elsewhere. The queue is paused.";

export class ExecutionSessionManager {
  private readonly entries = new Map<string, Entry>();
  private readonly createClient: ClientFactory;
  private readonly activity = new ExecutionActivityStore();
  private readonly reconnectedListeners = new Set<() => void>();
  private readonly supersededTurnListeners = new Set<(key: ExecutionSessionKey) => void>();
  private readonly queueListeners = new Set<() => void>();
  private queueVersion = 0;

  /**
   * 会话列表右侧的执行状态；当前显示的会话与后台会话的 Turn 事件都写入这里。
   * The conversation list's execution statuses; turn events of the displayed and background conversations both write here.
   */
  public readonly conversationStatuses = new ConversationStatusStore();

  public constructor(createClient: ClientFactory = (handlers) => new ExecutionSession(handlers)) {
    this.createClient = createClient;
  }

  public attach(
    key: ExecutionSessionKey,
    handler: ManagedExecutionHandlers,
  ): ManagedExecutionHandle {
    const id = getExecutionSessionKey(key);
    let entry = this.entries.get(id);
    if (!entry) {
      let nextEntry!: Entry;
      const client = this.createClient({
        onMessage: (message) => this.handleMessage(nextEntry, message),
        onError: (error) => nextEntry.handler?.onError?.(error),
        onClose: (error) => this.handleClose(nextEntry, error),
        onReconnecting: (state) => this.handleReconnecting(nextEntry, state),
        onReconnectFailed: (state) => this.handleReconnectFailed(nextEntry, state),
        onReconnected: () => this.handleReconnected(nextEntry),
      });
      nextEntry = {
        key,
        client,
        handler,
        pendingMessages: [],
        pendingInteractions: new Map(),
        reconnectState: null,
        activeTurn: null,
        appliedSetting: null,
        requestedSetting: null,
        configuring: 0,
        configurationFailed: false,
        queue: [],
        queuePaused: false,
        pauseReason: null,
        editingItemId: null,
        stopping: false,
        inFlight: null,
        queueSnapshot: EMPTY_EXECUTION_QUEUE,
      };
      entry = nextEntry;
      this.entries.set(id, entry);
    } else {
      entry.handler = handler;
    }
    this.activity.attach(key);
    const pendingMessages = entry.pendingMessages.splice(0);
    const replayableActiveTurn = entry.activeTurn?.replayable === true;
    const replayMessages = replayableActiveTurn
      ? []
      : pendingMessages.filter((message) => getPendingInteraction(message) === null);
    replayMessages.push(...entry.pendingInteractions.values());
    const attachedEntry = entry;
    if (replayMessages.length > 0) {
      queueMicrotask(() => {
        for (const message of replayMessages) {
          if (this.entries.get(id) !== attachedEntry || attachedEntry.handler !== handler) return;
          const interaction = getPendingInteraction(message);
          if (interaction && !attachedEntry.pendingInteractions.has(interaction.interactionId))
            continue;
          handler.onMessage(message);
        }
      });
    }

    return {
      matchesKey: (candidate) => getExecutionSessionKey(candidate) === id,
      configure: async (setting) => {
        attachedEntry.requestedSetting = setting;
        attachedEntry.configurationFailed = false;
        const applied = attachedEntry.appliedSetting;
        if (
          applied &&
          !isSameExecutionSetting(applied, setting) &&
          (attachedEntry.inFlight !== null || this.isTurnRunning(attachedEntry))
        ) {
          // 服务端在 Turn 运行期间拒绝修改设置：Turn 结束后由 drain 先应用新设置，再发送队列条目。
          // The server refuses a settings change during a running turn: drain applies it after the turn ends, before the next queue entry.
          return { restoredDurableExecution: false };
        }
        return await this.applySetting(attachedEntry, setting);
      },
      submit: (submission) => {
        if (this.entries.get(id) !== attachedEntry) {
          throw new Error("Execution session is not available.");
        }
        if (!isSubmittableInput(submission)) {
          throw new Error("Enter a prompt or add code comments before sending.");
        }
        if (attachedEntry.stopping) {
          throw new Error("Please wait for the current execution to stop.");
        }
        const itemId = createUuidV7();
        attachedEntry.queue.push({
          ...submission,
          id: itemId,
          executionId: createUuidV7(),
          uncertain: false,
        });
        // 新的提交重新尝试应用失败的设置。A new submission retries applying settings that failed.
        attachedEntry.configurationFailed = false;
        this.emitQueueChange(attachedEntry);
        this.drain(attachedEntry);
        return attachedEntry.inFlight?.item.id === itemId ? "started" : "queued";
      },
      getQueue: () => attachedEntry.queueSnapshot,
      updateQueuedItem: (itemId, text) => {
        const index = attachedEntry.queue.findIndex((item) => item.id === itemId);
        const item = attachedEntry.queue[index];
        if (!item) throw new Error("The queued message no longer exists.");
        if (item.uncertain) {
          throw new Error("This message may have been sent already, so it cannot be edited.");
        }
        if (!isSubmittableInput({ text, fileCommentCount: item.fileCommentCount })) {
          throw new Error("Enter a prompt or add code comments before sending.");
        }
        attachedEntry.queue[index] = { ...item, text };
        if (attachedEntry.editingItemId === itemId) attachedEntry.editingItemId = null;
        this.emitQueueChange(attachedEntry);
        this.drain(attachedEntry);
      },
      removeQueuedItem: (itemId) => {
        attachedEntry.queue = attachedEntry.queue.filter((item) => item.id !== itemId);
        if (attachedEntry.editingItemId === itemId) attachedEntry.editingItemId = null;
        if (attachedEntry.queue.length === 0) this.clearPause(attachedEntry);
        this.emitQueueChange(attachedEntry);
        this.drain(attachedEntry);
      },
      setEditingQueuedItem: (itemId) => {
        if (itemId !== null) {
          const item = attachedEntry.queue.find((candidate) => candidate.id === itemId);
          if (!item) throw new Error("The queued message no longer exists.");
          if (item.uncertain) {
            throw new Error("This message may have been sent already, so it cannot be edited.");
          }
        }
        attachedEntry.editingItemId = itemId;
        this.emitQueueChange(attachedEntry);
        if (itemId === null) this.drain(attachedEntry);
      },
      resumeQueue: () => {
        this.clearPause(attachedEntry);
        attachedEntry.configurationFailed = false;
        this.emitQueueChange(attachedEntry);
        this.drain(attachedEntry);
      },
      stop: async (reason) => {
        attachedEntry.queue = [];
        attachedEntry.editingItemId = null;
        this.clearPause(attachedEntry);
        attachedEntry.stopping =
          attachedEntry.inFlight !== null ||
          this.activity.isActive(key) ||
          attachedEntry.client.hasActiveExecution();
        this.emitQueueChange(attachedEntry);
        try {
          await attachedEntry.client.interrupt(reason);
        } catch (error) {
          attachedEntry.stopping = false;
          this.emitQueueChange(attachedEntry);
          throw error;
        }
      },
      listAgentflowCheckpoints: (agentflowId) =>
        attachedEntry.client.listAgentflowCheckpoints(agentflowId),
      resumeCheckpoint: async (args) => {
        if (this.activity.isActive(key) || attachedEntry.inFlight) {
          throw new Error("This conversation already has a running task.");
        }
        attachedEntry.activeTurn = null;
        this.activity.turnStarted(key);
        try {
          return await attachedEntry.client.resumeCheckpoint(args);
        } catch (error) {
          if (!attachedEntry.client.hasActiveExecution()) this.activity.turnFinished(key, "failed");
          throw error;
        }
      },
      setMode: (agentId, mode) => attachedEntry.client.setMode(agentId, mode),
      setPermissionMode: async (permissionMode) => {
        await attachedEntry.client.setPermissionMode(permissionMode);
        // 服务端设置随之更新，记录的设置保持一致。The server settings follow, so the recorded settings stay in step.
        if (attachedEntry.appliedSetting) {
          attachedEntry.appliedSetting = { ...attachedEntry.appliedSetting, permissionMode };
        }
        if (attachedEntry.requestedSetting) {
          attachedEntry.requestedSetting = { ...attachedEntry.requestedSetting, permissionMode };
        }
      },
      interrupt: (reason) => attachedEntry.client.interrupt(reason),
      interruptAndWait: (reason) => attachedEntry.client.interruptAndWait(reason),
      submitHumanResponse: async (args) => {
        await attachedEntry.client.submitHumanResponse(args);
        if (this.entries.get(id) !== attachedEntry) return;
        this.clearPendingInteraction(attachedEntry, args.response.interactionId);
        const remaining = Array.from(attachedEntry.pendingInteractions.values()).at(-1);
        if (remaining) {
          this.activity.waitingForApproval(key);
          attachedEntry.handler?.onMessage(remaining);
        } else if (this.activity.isActive(key)) {
          this.activity.turnStarted(key);
        }
      },
      getStatus: () => this.activity.getStatus(key),
      getReconnectState: () => attachedEntry.reconnectState,
      getActiveTurnSnapshot: () => this.readActiveTurnSnapshot(attachedEntry),
      detach: () => {
        if (attachedEntry.handler === handler) {
          attachedEntry.handler = null;
          this.activity.detach(key);
          // 离开会话时取消未保存的编辑，后台继续处理队列。
          // Leaving the conversation cancels an unsaved edit; the queue keeps running in the background.
          if (attachedEntry.editingItemId !== null) {
            attachedEntry.editingItemId = null;
            this.emitQueueChange(attachedEntry);
            this.drain(attachedEntry);
          }
        }
      },
      dispose: async () => {
        if (this.entries.get(id) !== attachedEntry) return;
        this.entries.delete(id);
        this.activity.remove(key);
        this.queueVersion += 1;
        this.notifyQueueListeners();
        await attachedEntry.client.dispose();
      },
    };
  }

  public has(key: ExecutionSessionKey): boolean {
    return this.entries.has(getExecutionSessionKey(key));
  }

  /** 读取会话的队列快照；没有连接的会话队列为空。Reads a conversation's queue snapshot; a conversation without a connection has an empty queue. */
  public getQueue(key: ExecutionSessionKey): ExecutionQueueSnapshot {
    return this.entries.get(getExecutionSessionKey(key))?.queueSnapshot ?? EMPTY_EXECUTION_QUEUE;
  }

  public subscribeQueue = (listener: () => void): (() => void) => {
    this.queueListeners.add(listener);
    return () => this.queueListeners.delete(listener);
  };

  public getQueueVersion = (): number => this.queueVersion;

  /**
   * 释放已删除会话的连接与队列。
   * Releases the connection and queue of a deleted conversation.
   */
  public async discard(key: ExecutionSessionKey): Promise<void> {
    const id = getExecutionSessionKey(key);
    const entry = this.entries.get(id);
    if (!entry) return;
    this.entries.delete(id);
    this.activity.remove(key);
    this.queueVersion += 1;
    this.notifyQueueListeners();
    await entry.client.dispose();
  }

  /**
   * 清除项目的全部对话后，释放该项目所有会话的连接与队列。
   * After all conversations of a project are cleared, releases the connections and queues of its conversations.
   */
  public async discardProject(scope: { serverId: string; projectId: string }): Promise<void> {
    const keys = [...this.entries.values()]
      .map((entry) => entry.key)
      .filter((key) => key.serverId === scope.serverId && key.projectId === scope.projectId);
    await Promise.all(keys.map((key) => this.discard(key)));
  }

  public getProjectStatus(serverId: string, projectId: string): ExecutionStatus {
    return this.activity.getProjectStatus(serverId, projectId);
  }

  public getActiveCount(): number {
    return this.activity.getActiveCount();
  }

  /** 请求指定会话立即执行当前重连次数，失败后继续剩余计划。 */
  public async retryConnection(key: ExecutionSessionKey): Promise<void> {
    const entry = this.entries.get(getExecutionSessionKey(key));
    if (!entry) throw new Error("Execution session is not available.");
    await entry.client.retryConnection();
  }

  private readonly turnFinishedListeners = new Set<(event: TurnFinishedEvent) => void>();

  public subscribe = this.activity.subscribe;

  /**
   * 订阅 turn 进入终态的一次性事件。
   * 仅在执行从 active 转为终态时触发；重复终态消息、回放与水合不会重复触发。
   */
  public subscribeTurnFinished = (listener: (event: TurnFinishedEvent) => void): (() => void) => {
    this.turnFinishedListeners.add(listener);
    return () => this.turnFinishedListeners.delete(listener);
  };

  public getSnapshot = this.activity.getSnapshot;

  /**
   * 订阅任一执行连接重连成功的事件。
   * Subscribes to any execution connection reconnecting successfully.
   */
  public subscribeReconnected = (listener: () => void): (() => void) => {
    this.reconnectedListeners.add(listener);
    return () => this.reconnectedListeners.delete(listener);
  };

  /**
   * 订阅已被更新 Turn 取代的生命周期消息；这时本地记录可能已经过期，需要重新获取该项目的快照。
   * Subscribes to lifecycle messages superseded by a newer turn; the local record may then be stale and the project's snapshot needs fetching again.
   */
  public subscribeSupersededTurn = (listener: (key: ExecutionSessionKey) => void): (() => void) => {
    this.supersededTurnListeners.add(listener);
    return () => this.supersededTurnListeners.delete(listener);
  };

  private handleMessage(entry: Entry, message: AiMessage): void {
    this.captureActiveTurnMessage(entry, message);
    const interaction = getPendingInteraction(message);
    // 缺少会话或 Turn 标识、或已被更新 Turn 取代的生命周期消息不更新会话状态，对应会话由快照更新。
    // Lifecycle messages without conversation or turn IDs, or superseded by a newer turn, leave statuses to the next snapshot.
    const superseded = isSupersededTurnMessage(message);
    const turn = superseded ? null : getTurnIdentity(message);
    // 队列按条目的 executionId 识别自己的 Turn，被取代的消息同样适用。The queue recognizes its own turn by the entry's executionId, superseded messages included.
    const turnId = readTurnId(message);
    if (isTurnStartMessage(message)) {
      this.clearPendingInteraction(entry);
      this.activity.turnStarted(entry.key);
      if (turn) this.conversationStatuses.turnStarted(entry.key, turn.conversationId, turn.turnId);
      if (entry.inFlight && turnId === entry.inFlight.item.executionId)
        entry.inFlight.started = true;
    } else if (interaction) {
      entry.pendingInteractions.set(interaction.interactionId, message);
      this.activity.waitingForApproval(entry.key);
    } else {
      const terminalStatus = getTurnFinishedStatus(message);
      if (terminalStatus) {
        this.clearPendingInteraction(entry);
        const wasActive = this.activity.isActive(entry.key);
        this.activity.turnFinished(entry.key, terminalStatus);
        entry.activeTurn = null;
        if (turn) {
          this.conversationStatuses.turnFinished(
            entry.key,
            turn.conversationId,
            turn.turnId,
            terminalStatus,
          );
        }
        if (wasActive) this.emitTurnFinished(entry.key, terminalStatus);
        this.settleQueueAfterTurn(entry, turnId, terminalStatus, wasActive, superseded);
      }
    }
    if (superseded) {
      for (const listener of this.supersededTurnListeners) listener(entry.key);
    }
    if (entry.handler) {
      entry.handler.onMessage(message);
    } else {
      entry.pendingMessages.push(message);
      if (entry.pendingMessages.length > 200) entry.pendingMessages.shift();
    }
  }

  /**
   * Turn 结束后推进队列：只有发送中的条目收到自己的结束消息，或没有发送中的条目时进行中的 Turn 结束，才算队列的 Turn 结束。
   * 正常完成时在本次消息分发之后发送队首一条；失败或中断时保留剩余条目并暂停。
   * 被取代的结束消息同样结束这个 Turn：会话中已有其他地方发起的更新 Turn，它可能仍在运行，剩余条目暂停等待用户继续。
   * Advances the queue after a turn ends: only the in-flight entry's own finish message, or the end of the active turn when nothing is in flight, counts.
   * On completion the head is sent after this message is delivered; on failure or interruption the rest is kept and the queue pauses.
   * A superseded finish message ends the turn as well: the conversation has a newer turn started elsewhere that may still run, so the rest pauses until the user resumes.
   */
  private settleQueueAfterTurn(
    entry: Entry,
    turnId: string | null,
    status: TurnFinishedStatus,
    wasActive: boolean,
    superseded: boolean,
  ): void {
    const wasStopping = entry.stopping;
    entry.stopping = false;
    const inFlight = entry.inFlight;
    const endsQueueTurn = inFlight ? turnId === inFlight.item.executionId : wasActive;
    if (!endsQueueTurn) {
      if (wasStopping) this.emitQueueChange(entry);
      return;
    }

    entry.inFlight = null;
    if (entry.queue.length > 0 && (superseded || status !== "completed")) {
      this.pauseQueue(
        entry,
        superseded
          ? SUPERSEDED_TURN_PAUSE_REASON
          : status === "failed"
            ? FAILED_TURN_PAUSE_REASON
            : INTERRUPTED_TURN_PAUSE_REASON,
      );
    }
    this.emitQueueChange(entry);
    // 本次消息分发之后再推进：Turn 期间记录的设置先生效，未暂停时再发送队首一条。
    // Advance after this message is delivered: settings recorded during the turn apply first, then the head is sent unless paused.
    queueMicrotask(() => this.drain(entry));
  }

  /**
   * 连接关闭或重连后没有活动执行时，发送中条目的结果无法确认：尚未开始的条目回到队首并锁定编辑，队列暂停。
   * When the connection closes, or no execution is active after reconnecting, the in-flight entry's outcome is unknown: an entry not yet started returns to the head locked for editing, and the queue pauses.
   */
  private settleQueueWithUnknownOutcome(entry: Entry, turnWasActive: boolean): void {
    const inFlight = entry.inFlight;
    entry.inFlight = null;
    entry.stopping = false;
    let returned: QueuedExecution | null = null;
    if (inFlight && !inFlight.started) {
      returned = { ...inFlight.item, uncertain: true };
      entry.queue.unshift(returned);
    }
    if ((inFlight || turnWasActive) && entry.queue.length > 0) {
      this.pauseQueue(entry, UNKNOWN_OUTCOME_PAUSE_REASON);
    }
    this.emitQueueChange(entry);
    if (returned) {
      entry.handler?.onSubmissionReturned?.({
        item: returned,
        error: new Error(UNKNOWN_OUTCOME_PAUSE_REASON),
      });
    }
    this.drain(entry);
  }

  /**
   * 空闲时推进队列：连接的设置与下一轮要求的设置不同时先应用设置，相同、未暂停且队首没有在编辑时发送队首一条。
   * Advances the queue when idle: settings that differ from the ones the next turn requires are applied first; once they match, the head is sent unless the queue is paused or the head is being edited.
   */
  private drain(entry: Entry): void {
    if (!this.isAttached(entry)) return;
    if (entry.inFlight || entry.stopping || entry.configuring > 0 || this.isTurnRunning(entry)) {
      return;
    }
    const applied = entry.appliedSetting;
    const requested = entry.requestedSetting;
    if (!applied || !requested) return;
    if (!isSameExecutionSetting(applied, requested)) {
      // 未配置的新连接由调用方显式配置；这里只应用已配置连接在 Turn 期间记录的设置。
      // A new connection is configured explicitly by the caller; only settings recorded during a turn on a configured connection apply here.
      if (!entry.configurationFailed) {
        // 失败已由 applySetting 记录为暂停原因，连接也已报告，这里不再抛出。
        // applySetting already recorded the failure as the pause reason and the connection reported it, so it is not rethrown here.
        void this.applySetting(entry, requested).catch(() => undefined);
      }
      return;
    }
    if (entry.queuePaused) return;
    const head = entry.queue[0];
    if (!head || entry.editingItemId === head.id) return;
    entry.queue.shift();
    this.dispatch(entry, head);
  }

  /**
   * 把设置发送到连接；成功后记为连接当前的设置。失败时服务端保留原有设置，队列不按原有设置发送，等待再次配置、恢复队列或新的提交。
   * Sends the settings to the connection and records them as its current settings on success. On failure the server keeps its previous settings; the queue does not send with them and waits for the next configure, resume or submission.
   */
  private async applySetting(
    entry: Entry,
    setting: ExecutionSetting,
  ): Promise<ExecutionConfigurationResult> {
    entry.configuring += 1;
    try {
      const result = await entry.client.configure(setting);
      if (this.isAttached(entry)) {
        entry.appliedSetting = setting;
        if (result.restoredDurableExecution || entry.client.hasActiveExecution()) {
          this.activity.turnStarted(entry.key);
        }
      }
      return result;
    } catch (error) {
      if (this.isAttached(entry)) {
        if (entry.client.hasActiveExecution()) this.activity.turnStarted(entry.key);
        entry.configurationFailed = true;
        if (!entry.inFlight && entry.queue.length > 0) {
          this.pauseQueue(entry, toError(error).message);
          this.emitQueueChange(entry);
        }
      }
      throw error;
    } finally {
      entry.configuring -= 1;
      this.drain(entry);
    }
  }

  private isAttached(entry: Entry): boolean {
    return this.entries.get(getExecutionSessionKey(entry.key)) === entry;
  }

  /** 连接上仍有进行中的 Turn。A turn is still running on the connection. */
  private isTurnRunning(entry: Entry): boolean {
    return this.activity.isActive(entry.key) || entry.client.hasActiveExecution();
  }

  private dispatch(entry: Entry, item: QueuedExecution): void {
    const message = item.createMessage(item.text, item.id);
    const input = toExecutionUserInput(message);
    const request: ExecutionRequest = {
      conversationId: entry.key.conversationId,
      agentId: item.target.agentId,
      agentType: item.target.agentType,
      executionId: item.executionId,
      stream: true,
      input,
    };
    entry.inFlight = { item, started: false };
    this.beginActiveTurn(entry, input);
    this.activity.turnStarted(entry.key);
    this.emitQueueChange(entry);
    entry.handler?.onSubmissionStarted?.({ item, message });
    void entry.client
      .execute(request)
      .catch((error: unknown) => this.handleDispatchFailure(entry, item, toError(error)));
  }

  /**
   * 发送失败时只处理仍未开始、且连接不再持有它的条目：确认没有执行时可以编辑，结果无法确认时锁定编辑；两者都回到队首并暂停队列。
   * A failed send only handles an entry that has not started and that the connection no longer holds: one confirmed not run stays editable, one with an unknown outcome is locked; both return to the head and the queue pauses.
   */
  private handleDispatchFailure(entry: Entry, item: QueuedExecution, error: Error): void {
    if (this.entries.get(getExecutionSessionKey(entry.key)) !== entry) return;
    const inFlight = entry.inFlight;
    if (inFlight?.item !== item || inFlight.started || entry.client.hasActiveExecution()) return;
    entry.inFlight = null;
    entry.activeTurn = null;
    this.activity.turnFinished(entry.key, "failed");
    const returned: QueuedExecution = {
      ...item,
      uncertain: !(error instanceof ExecutionStartRejectedError),
    };
    entry.queue.unshift(returned);
    this.pauseQueue(entry, error.message);
    this.emitQueueChange(entry);
    entry.handler?.onSubmissionReturned?.({ item: returned, error });
  }

  private pauseQueue(entry: Entry, reason: string): void {
    entry.queuePaused = true;
    entry.pauseReason = reason;
  }

  private clearPause(entry: Entry): void {
    entry.queuePaused = false;
    entry.pauseReason = null;
  }

  private emitQueueChange(entry: Entry): void {
    entry.queueSnapshot = {
      items: [...entry.queue],
      paused: entry.queuePaused,
      pauseReason: entry.pauseReason,
      editingItemId: entry.editingItemId,
      stopping: entry.stopping,
    };
    this.queueVersion += 1;
    this.notifyQueueListeners();
  }

  private notifyQueueListeners(): void {
    for (const listener of this.queueListeners) listener();
  }

  private clearPendingInteraction(entry: Entry, interactionId?: string): void {
    if (interactionId === undefined) entry.pendingInteractions.clear();
    else entry.pendingInteractions.delete(interactionId);
    entry.pendingMessages = entry.pendingMessages.filter((message) => {
      const pendingInteraction = getPendingInteraction(message);
      return (
        !pendingInteraction ||
        (interactionId !== undefined && pendingInteraction.interactionId !== interactionId)
      );
    });
  }

  private beginActiveTurn(entry: Entry, input: ExecutionRequest["input"]): void {
    const streamingScopeId = input.messageId;
    entry.activeTurn = {
      streamingScopeId,
      replayable: true,
      messages: [
        scopeStreamingMessage(
          {
            messageId: input.messageId,
            createdAt: input.createdAt,
            author: input.author,
            role: "user",
            contents: input.contents,
          },
          streamingScopeId,
        ),
      ],
    };
    entry.pendingMessages = [];
    entry.pendingInteractions.clear();
  }

  private captureActiveTurnMessage(entry: Entry, message: AiMessage): void {
    const explicitScopeId = getMessageStreamingScopeId(message);
    if (isTurnStartMessage(message) && explicitScopeId) {
      if (!entry.activeTurn || entry.activeTurn.streamingScopeId !== explicitScopeId) {
        entry.activeTurn = {
          streamingScopeId: explicitScopeId,
          replayable: false,
          messages: [],
        };
      }
    }

    const activeTurn = entry.activeTurn;
    if (!activeTurn || (explicitScopeId && explicitScopeId !== activeTurn.streamingScopeId)) {
      return;
    }

    const scopedMessage = scopeStreamingMessage(message, activeTurn.streamingScopeId);
    activeTurn.messages = mergeStreamingMessages(activeTurn.messages, [scopedMessage]);
  }

  private readActiveTurnSnapshot(entry: Entry): ActiveTurnSnapshot | null {
    if (!entry.activeTurn?.replayable) {
      return null;
    }

    return {
      streamingScopeId: entry.activeTurn.streamingScopeId,
      messages: entry.activeTurn.messages.map((message) => cloneMessage(message)),
    };
  }

  private emitTurnFinished(key: ExecutionSessionKey, status: TurnFinishedStatus): void {
    for (const listener of this.turnFinishedListeners) listener({ key, status });
  }

  private handleClose(entry: Entry, error?: Error): void {
    entry.reconnectState = null;
    // 新连接不带原有设置，必须重新配置后才能发送队列条目。A new connection lacks the old settings and must be configured before sending queue entries.
    entry.appliedSetting = null;
    const wasActive = this.activity.isActive(entry.key);
    this.activity.connectionClosed(entry.key, error);
    this.settleQueueWithUnknownOutcome(entry, wasActive);
    entry.handler?.onClose?.(error);
  }

  /** 保存当前重试计划，并通知正在展示该会话的 Chat。 */
  private handleReconnecting(entry: Entry, state: ExecutionReconnectState): void {
    entry.reconnectState = state;
    entry.handler?.onReconnecting?.(state);
  }

  /** 保存重试耗尽状态，并通知 Chat 保留手动重试入口。 */
  private handleReconnectFailed(entry: Entry, state: ExecutionReconnectState): void {
    entry.reconnectState = state;
    entry.handler?.onReconnectFailed?.(state);
  }

  /**
   * 清理重试状态，并通知 Chat 可以恢复操作。重连后没有活动执行时，本地无法确认刚才的 Turn 如何结束，队列据此暂停。
   * Clears the retry state and lets Chat resume. With no active execution after reconnecting, how the last turn ended cannot be confirmed, so the queue pauses.
   */
  private handleReconnected(entry: Entry): void {
    entry.reconnectState = null;
    if (entry.client.hasActiveExecution()) {
      this.activity.turnStarted(entry.key);
    } else {
      const wasActive = this.activity.isActive(entry.key);
      if (wasActive) {
        this.activity.turnFinished(entry.key, "completed");
        this.emitTurnFinished(entry.key, "completed");
      }
      this.settleQueueWithUnknownOutcome(entry, wasActive);
    }
    entry.handler?.onReconnected?.();
    for (const listener of this.reconnectedListeners) listener();
  }
}

/** 读取消息的 turnId；连接上没有 Turn 时发出的结束消息没有它。Reads a message's turnId; a finish message sent without a turn on the connection has none. */
function readTurnId(message: AiMessage): string | null {
  const turnId = message.additionalProperties?.turnId;
  return typeof turnId === "string" && turnId.length > 0 ? turnId : null;
}

/**
 * 比较服务端判断设置是否变化所用的字段；resultOnly 在服务端不算变化，但同样只对下一轮生效，一并比较。
 * Compares the fields the server uses to decide whether settings changed; resultOnly is not a change on the server but also applies only to the next turn, so it is compared too.
 */
function isSameExecutionSetting(left: ExecutionSetting, right: ExecutionSetting): boolean {
  if (
    left.projectId !== right.projectId ||
    left.conversationId !== right.conversationId ||
    left.permissionMode !== right.permissionMode ||
    (left.resultOnly ?? false) !== (right.resultOnly ?? false)
  ) {
    return false;
  }
  const leftVariables = Object.entries(left.environmentVariables ?? {});
  const rightVariables = right.environmentVariables ?? {};
  return (
    leftVariables.length === Object.keys(rightVariables).length &&
    leftVariables.every(
      ([name, value]) => Object.hasOwn(rightVariables, name) && rightVariables[name] === value,
    )
  );
}

function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

export const executionSessionManager = new ExecutionSessionManager();
