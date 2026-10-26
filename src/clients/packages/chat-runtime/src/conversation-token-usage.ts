import {
  addTokenUsage,
  EMPTY_TOKEN_USAGE,
  getMessageTokenUsage,
  normalizeTokenUsage,
  type AiMessage,
  type TokenUsage,
  type TokenUsageInput,
} from "@agw/api";
import {
  getTurnPosition,
  isTurnStartMessage,
  type ChatEvent,
  type ChatScope,
} from "@agw/execution-core";
import {
  chatHttpClient,
  CONVERSATION_DETAILS_PATH,
  type ChatConnectionIdentity,
  type ChatHttpClient,
} from "./chat-http-client";
import { getExecutionSessionKey, type ExecutionSessionKey } from "./execution-activity-store";
import type { ExecutionAttachmentStore } from "./execution-session";

type UsageClient = Pick<ChatHttpClient, "subscribe" | "getConversation" | "getConnectionIdentity">;

/** 结算之前的同步间隔：1、2、4、8 秒，之后每 10 秒。The syncs before settlement: after 1, 2, 4 and 8 seconds, then every 10. */
const SETTLEMENT_SYNC_DELAYS_MS = [1_000, 2_000, 4_000, 8_000];
const SETTLEMENT_SYNC_INTERVAL_MS = 10_000;

/** 一个 Turn 推送的用量。The usage one turn pushed. */
type TurnUsage = {
  usage: TokenUsage;
  /** 已经计入的最大 turnSequence。The largest turnSequence counted. */
  maxSequence: number;
  /** 开始消息到达时的事件序号；从存储恢复的 Turn 为空。The event sequence when the start message arrived; null for a turn restored from storage. */
  startSequence: number | null;
  finished: boolean;
};

/** 被采用的对话详情：已结算的用量、未结算的 Turn 与请求的序号。The adopted details: settled usage, unsettled turns and the request's sequence. */
type Baseline = {
  usage: TokenUsage;
  unsettledTurnIds: ReadonlySet<string>;
  requestSequence: number;
};

type ConversationUsage = {
  key: ExecutionSessionKey;
  baseline: Baseline | null;
  turns: Map<string, TurnUsage>;
  /** 最近一个执行通道事件或被采用的详情响应的连接身份。The identity of the latest execution event or adopted details response. */
  connection: ChatConnectionIdentity | null;
  /** 最近一次确认没有运行中执行的事件序号。The event sequence of the latest confirmation that nothing is running. */
  idleSequence: number | null;
  value: TokenUsage;
  settlementTimer: ReturnType<typeof setTimeout> | null;
  settlementAttempt: number;
};

/** 保存在存储中的未结算 Turn。An unsettled turn kept in storage. */
type SavedTurn = { turnId: string; usage: TokenUsage; maxSequence: number; finished: boolean };

/**
 * 以 Turn 为边界统计对话的 Token usage：对话详情中已结算 Turn 的用量为基线，加上未结算 Turn 推送的 UsageContent；Turn 结束后在原连接下重新读取详情。
 * Counts a conversation's token usage by turn: the settled usage of the conversation details is the baseline, plus the UsageContent unsettled turns pushed; once a turn ends the details are read again over the original connection.
 */
export class ConversationTokenUsageStore {
  private readonly client: UsageClient;
  private readonly storage: ExecutionAttachmentStore | null;
  private readonly conversations = new Map<string, ConversationUsage>();
  /** 对话详情 request 事件的 id 与它的序号。The id of each details request event and its sequence. */
  private readonly detailsRequests = new Map<string, number>();
  private readonly listeners = new Set<() => void>();
  private readonly unsubscribes: (() => void)[];
  private sequence = 0;
  private disposed = false;

  /**
   * 构造时订阅 client；storage 保存未结算 Turn 的推送用量，为 null 时只保存在内存中。
   * Subscribes to the client on construction; storage keeps the pushed usage of unsettled turns, which stays in memory only when it is null.
   */
  public constructor(client: UsageClient, storage: ExecutionAttachmentStore | null) {
    this.client = client;
    this.storage = storage;
    this.unsubscribes = [
      client.subscribe(
        { kinds: ["request", "response"], channels: ["http"] },
        (event, connection) => this.handleDetails(event, connection),
      ),
      client.subscribe({ kinds: ["response"], channels: ["execution"] }, (event, connection) =>
        this.handleHubResponse(event, connection),
      ),
      client.subscribe({ sources: ["live"], contentTypes: ["UsageContent"] }, (event, connection) =>
        this.handleUsage(event, connection),
      ),
      client.subscribe(
        { sources: ["live"], messageTypes: ["agw-turn-start", "agw-turn-finished"] },
        (event, connection) => this.handleTurnState(event, connection),
      ),
    ];
  }

  /** 对话的用量；没有记录时为 EMPTY_TOKEN_USAGE。The conversation's usage; EMPTY_TOKEN_USAGE without a record. */
  public get(key: ExecutionSessionKey): TokenUsage {
    return this.conversations.get(getExecutionSessionKey(key))?.value ?? EMPTY_TOKEN_USAGE;
  }

  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** 取消对 client 的订阅与已经安排的同步。Unsubscribes from the client and cancels scheduled syncs. */
  public dispose(): void {
    this.disposed = true;
    for (const unsubscribe of this.unsubscribes) unsubscribe();
    for (const conversation of this.conversations.values()) this.cancelSettlementSync(conversation);
  }

  /**
   * 对话详情的请求记下序号；响应在成功且请求晚于当前基线时被采用，并删除已经包含在基线中的 Turn。
   * A details request records its sequence; a successful response to a request later than the current baseline is adopted, removing the turns the baseline already includes.
   */
  private handleDetails(event: ChatEvent<AiMessage>, connection: ChatConnectionIdentity): void {
    const sequence = ++this.sequence;
    if (event.kind === "message" || event.channel !== "http") return;
    if (event.request.method !== "GET" || event.request.path !== CONVERSATION_DETAILS_PATH) return;
    if (event.kind === "request") {
      this.detailsRequests.set(event.id, sequence);
      return;
    }
    const requestSequence = this.detailsRequests.get(event.requestId);
    this.detailsRequests.delete(event.requestId);
    if (requestSequence === undefined || !event.ok) return;
    const details = readDetails(event.value);
    const conversation = this.getConversation({
      serverId: event.scope.serverId,
      projectId: details.projectId,
      conversationId: details.conversationId,
    });
    if (conversation.baseline && conversation.baseline.requestSequence >= requestSequence) return;
    conversation.baseline = {
      usage: details.usage,
      unsettledTurnIds: new Set(details.unsettledTurnIds),
      requestSequence,
    };
    conversation.connection = connection;
    const included = [...conversation.turns].filter(
      ([turnId, turn]) => !this.counts(conversation, turnId, turn),
    );
    for (const [turnId] of included) conversation.turns.delete(turnId);
    if (included.length > 0) this.save(conversation);
    this.scheduleSettlementSync(conversation);
    this.checkIdleSync(conversation);
    this.update(conversation);
  }

  /**
   * hub 调用的结果确认对话没有运行中的执行时记下序号：进程内模式刷新页面后收不到原执行的结束消息，由这里结束统计。
   * Records the sequence when a hub call's result confirms nothing runs in the conversation: after an in-process reload the original execution's finish never arrives, so this ends the counting.
   */
  private handleHubResponse(event: ChatEvent<AiMessage>, connection: ChatConnectionIdentity): void {
    const sequence = ++this.sequence;
    if (event.kind !== "response" || event.channel !== "execution" || !event.ok) return;
    const conversation = this.getConversation(toConversationKey(event.scope));
    conversation.connection = connection;
    const idle =
      (event.request.method === "FindInProcessExecution" && event.value === null) ||
      (event.request.method === "RecoverInProcessExecution" && event.value === false);
    if (!idle) return;
    conversation.idleSequence = sequence;
    this.checkIdleSync(conversation);
  }

  /**
   * 推送的 UsageContent 按 turnSequence 去重后加到所属 Turn：服务端按序号推送与补发，只记最大序号即可去重。
   * Pushed UsageContent is deduplicated by turnSequence and added to its turn: the server pushes and replays in sequence order, so the largest sequence suffices.
   */
  private handleUsage(event: ChatEvent<AiMessage>, connection: ChatConnectionIdentity): void {
    ++this.sequence;
    if (event.kind !== "message") return;
    const position = getTurnPosition(event.message);
    if (!position) throw new Error("Pushed usage content requires turnId and turnSequence.");
    const conversation = this.getConversation(toConversationKey(event.scope));
    conversation.connection = connection;
    const turn = this.getTurn(conversation, position.turnId);
    if (position.turnSequence <= turn.maxSequence) return;
    turn.usage = addTokenUsage(
      turn.usage,
      getMessageTokenUsage(event.message) ?? EMPTY_TOKEN_USAGE,
    );
    turn.maxSequence = position.turnSequence;
    this.save(conversation);
    this.update(conversation);
  }

  /**
   * 开始消息记下序号；结束消息标记 Turn 已结束，并在当前事件派发结束后读取对话详情。
   * A start message records its sequence; a finish message marks the turn finished and reads the details once the current event dispatch ends.
   */
  private handleTurnState(event: ChatEvent<AiMessage>, connection: ChatConnectionIdentity): void {
    const sequence = ++this.sequence;
    if (event.kind !== "message") return;
    const turnId = event.message.additionalProperties?.turnId;
    // 连接上没有 Turn 时服务端发出的结束消息不属于任何 Turn。A finish the server sends without a turn on the connection belongs to no turn.
    if (typeof turnId !== "string" || turnId.length === 0) return;
    const conversation = this.getConversation(toConversationKey(event.scope));
    conversation.connection = connection;
    const turn = this.getTurn(conversation, turnId);
    if (isTurnStartMessage(event.message)) {
      turn.startSequence ??= sequence;
      this.update(conversation);
      return;
    }
    turn.finished = true;
    this.save(conversation);
    queueMicrotask(() => this.requestSync(conversation));
  }

  /**
   * 确认没有运行中的执行之后，基线早于确认且还有未结算的 Turn 时读取详情；收到确认与采用新基线时各检查一次。
   * After a confirmation that nothing runs, reads the details when the baseline predates it and unsettled turns remain; checked on the confirmation and on adopting a baseline.
   */
  private checkIdleSync(conversation: ConversationUsage): void {
    const { baseline, idleSequence } = conversation;
    if (idleSequence === null) return;
    if (baseline && baseline.requestSequence > idleSequence) return;
    if ((baseline?.unsettledTurnIds.size ?? 0) === 0 && conversation.turns.size === 0) return;
    queueMicrotask(() => this.requestSync(conversation));
  }

  /**
   * 已经结束、仍列在 unsettledTurnIds 中的 Turn（Distributed 中断后 worker 尚未退出）按 1、2、4、8 秒、之后每 10 秒读取详情，直到没有这样的 Turn。
   * Finished turns still listed in unsettledTurnIds (the worker has not exited after a distributed interrupt) read the details after 1, 2, 4 and 8 seconds and then every 10, until none is left.
   */
  private scheduleSettlementSync(conversation: ConversationUsage): void {
    const unsettled = conversation.baseline?.unsettledTurnIds;
    const awaiting = [...conversation.turns].some(
      ([turnId, turn]) => turn.finished && unsettled?.has(turnId) === true,
    );
    if (!awaiting) {
      this.cancelSettlementSync(conversation);
      return;
    }
    if (conversation.settlementTimer !== null) return;
    const delay =
      SETTLEMENT_SYNC_DELAYS_MS[conversation.settlementAttempt] ?? SETTLEMENT_SYNC_INTERVAL_MS;
    conversation.settlementAttempt++;
    conversation.settlementTimer = setTimeout(() => {
      conversation.settlementTimer = null;
      this.requestSync(conversation);
    }, delay);
  }

  private cancelSettlementSync(conversation: ConversationUsage): void {
    if (conversation.settlementTimer !== null) clearTimeout(conversation.settlementTimer);
    conversation.settlementTimer = null;
    conversation.settlementAttempt = 0;
  }

  /**
   * 只在对话记下的连接身份等于当前 HTTP 连接身份时读取详情；比较与发出在同一次同步调用中完成，不会用另一个 Server 的地址与凭据查询。
   * Reads the details only when the conversation's recorded identity equals the current HTTP identity; comparing and sending happen in one synchronous call, so another server's address and credentials are never used.
   */
  private requestSync(conversation: ConversationUsage): void {
    if (this.disposed) return;
    const recorded = conversation.connection;
    const current = this.client.getConnectionIdentity();
    if (!recorded || recorded.baseUrl !== current.baseUrl || recorded.token !== current.token)
      return;
    void this.client.getConversation(conversation.key).catch(() => {
      // 同步失败时用量保持不变，结算之前的同步随之停止，等待下一次读取详情。
      // A failed sync leaves the usage unchanged and stops the syncs before settlement until the details are read again.
      this.cancelSettlementSync(conversation);
    });
  }

  /**
   * 计入显示值的 Turn：列在基线的 unsettledTurnIds 中，或开始消息晚于基线的请求到达；没有基线时全部计入。
   * The turns counted in the displayed value: listed in the baseline's unsettledTurnIds, or started after the baseline's request; all of them without a baseline.
   */
  private counts(conversation: ConversationUsage, turnId: string, turn: TurnUsage): boolean {
    const { baseline } = conversation;
    return (
      !baseline ||
      baseline.unsettledTurnIds.has(turnId) ||
      (turn.startSequence !== null && turn.startSequence > baseline.requestSequence)
    );
  }

  private update(conversation: ConversationUsage): void {
    let value = conversation.baseline?.usage ?? EMPTY_TOKEN_USAGE;
    for (const [turnId, turn] of conversation.turns) {
      if (this.counts(conversation, turnId, turn)) value = addTokenUsage(value, turn.usage);
    }
    if (isSameUsage(value, conversation.value)) return;
    conversation.value = value;
    for (const listener of this.listeners) listener();
  }

  private getConversation(key: ExecutionSessionKey): ConversationUsage {
    const id = getExecutionSessionKey(key);
    let conversation = this.conversations.get(id);
    if (!conversation) {
      conversation = {
        key: {
          serverId: key.serverId,
          projectId: key.projectId,
          conversationId: key.conversationId,
        },
        baseline: null,
        turns: this.load(key),
        connection: null,
        idleSequence: null,
        value: EMPTY_TOKEN_USAGE,
        settlementTimer: null,
        settlementAttempt: 0,
      };
      this.conversations.set(id, conversation);
      this.update(conversation);
    }
    return conversation;
  }

  private getTurn(conversation: ConversationUsage, turnId: string): TurnUsage {
    let turn = conversation.turns.get(turnId);
    if (!turn) {
      turn = { usage: EMPTY_TOKEN_USAGE, maxSequence: 0, startSequence: null, finished: false };
      conversation.turns.set(turnId, turn);
    }
    return turn;
  }

  /** 读取对话保存的未结算 Turn；读取失败时只失去刷新页面后的恢复。Reads the conversation's saved unsettled turns; a failed read only loses the recovery after a reload. */
  private load(key: ExecutionSessionKey): Map<string, TurnUsage> {
    const turns = new Map<string, TurnUsage>();
    if (!this.storage) return turns;
    let saved: unknown;
    try {
      const value = this.storage.getItem(getUsageStorageKey(key));
      saved = value ? JSON.parse(value) : null;
    } catch {
      // 存储不可用或内容损坏时统计照常进行。Counting goes on when the store is unavailable or its content is damaged.
      return turns;
    }
    for (const turn of readSavedTurns(saved)) {
      turns.set(turn.turnId, {
        usage: turn.usage,
        maxSequence: turn.maxSequence,
        startSequence: null,
        finished: turn.finished,
      });
    }
    return turns;
  }

  /** 写入对话的未结算 Turn；没有时移除记录。写入失败时只失去刷新页面后的恢复。Writes the conversation's unsettled turns, removing the record when none is left; a failed write only loses the recovery after a reload. */
  private save(conversation: ConversationUsage): void {
    if (!this.storage) return;
    const key = getUsageStorageKey(conversation.key);
    const turns: SavedTurn[] = [...conversation.turns].map(([turnId, turn]) => ({
      turnId,
      usage: turn.usage,
      maxSequence: turn.maxSequence,
      finished: turn.finished,
    }));
    try {
      if (turns.length === 0) this.storage.removeItem(key);
      else this.storage.setItem(key, JSON.stringify({ turns }));
    } catch {
      // Safari 隐私模式等环境可能禁用存储；统计照常进行。Storage may be disabled, as in Safari private mode; counting goes on.
    }
  }
}

function getUsageStorageKey(key: ExecutionSessionKey): string {
  return `agw:turn-usage:v1:${getExecutionSessionKey(key)}`;
}

function toConversationKey(scope: ChatScope): ExecutionSessionKey {
  if (scope.projectId === null || scope.conversationId === null) {
    throw new Error("An execution event must belong to a conversation.");
  }
  return {
    serverId: scope.serverId,
    projectId: scope.projectId,
    conversationId: scope.conversationId,
  };
}

/** 读取对话详情响应中的作用范围与用量；缺少 unsettledTurnIds 的响应来自不支持按 Turn 结算的 Server。Reads the scope and usage of a details response; one without unsettledTurnIds comes from a server without turn settlement. */
function readDetails(value: unknown): {
  projectId: string;
  conversationId: string;
  usage: TokenUsage;
  unsettledTurnIds: string[];
} {
  const details = value as {
    projectId?: unknown;
    conversationId?: unknown;
    usage?: (TokenUsageInput & { unsettledTurnIds?: unknown }) | null;
  } | null;
  const unsettledTurnIds = details?.usage?.unsettledTurnIds;
  if (
    typeof details?.projectId !== "string" ||
    typeof details.conversationId !== "string" ||
    !Array.isArray(unsettledTurnIds) ||
    !unsettledTurnIds.every((turnId) => typeof turnId === "string")
  ) {
    throw new Error("The conversation details have no usage with unsettledTurnIds.");
  }
  return {
    projectId: details.projectId,
    conversationId: details.conversationId,
    usage: normalizeTokenUsage(details.usage),
    unsettledTurnIds,
  };
}

function readSavedTurns(saved: unknown): SavedTurn[] {
  const turns = (saved as { turns?: unknown } | null)?.turns;
  if (!Array.isArray(turns)) return [];
  return turns.flatMap((turn: Partial<Record<keyof SavedTurn, unknown>>) =>
    typeof turn?.turnId === "string" &&
    typeof turn.maxSequence === "number" &&
    typeof turn.finished === "boolean"
      ? [
          {
            turnId: turn.turnId,
            usage: normalizeTokenUsage(turn.usage as TokenUsageInput | null),
            maxSequence: turn.maxSequence,
            finished: turn.finished,
          },
        ]
      : [],
  );
}

function isSameUsage(left: TokenUsage, right: TokenUsage): boolean {
  return (
    left.inputTokenCount === right.inputTokenCount &&
    left.outputTokenCount === right.outputTokenCount &&
    left.totalTokenCount === right.totalTokenCount &&
    left.cachedInputTokenCount === right.cachedInputTokenCount &&
    left.reasoningTokenCount === right.reasoningTokenCount
  );
}

/** Web 与 Desktop 使用的实例。The instance Web and Desktop use. */
export const conversationTokenUsage = new ConversationTokenUsageStore(
  chatHttpClient,
  // Node 在没有 --localstorage-file 时读取 globalThis.localStorage 会发出警告；浏览器与 Electron 中二者是同一个对象。
  // Node warns when globalThis.localStorage is read without --localstorage-file; in browsers and Electron both are the same object.
  typeof window === "undefined" ? null : (window.localStorage ?? null),
);
