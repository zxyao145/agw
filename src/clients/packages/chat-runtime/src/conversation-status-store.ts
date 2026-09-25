import type { TurnFinishedStatus } from "@agw/execution-core";

export type ConversationStatus = "idle" | "running" | "failed" | "interrupted";

export type ConversationStatusScope = {
  serverId: string;
  projectId: string;
};

export type ConversationStatusEntry = {
  turnId: string | null;
  status: ConversationStatus;
  /**
   * 记录内容对应的状态库版本：事件与清空取写入时的版本，快照取请求版本。
   * The store revision of this content: the write revision for events and resets, the request revision for snapshots.
   */
  revision: number;
};

/**
 * 快照中的一个会话；快照中没有的会话为 idle。
 * One conversation of a snapshot; conversations missing from the snapshot are idle.
 */
export type ConversationStatusSnapshotItem = {
  conversationId: string;
  turnId: string;
  status: Exclude<ConversationStatus, "idle">;
};

type ScopeState = {
  /**
   * 范围内单调递增的版本，每次写入与每次发出快照请求都加一。
   * The scope's monotonic revision, incremented by every write and every snapshot request.
   */
  revision: number;
  entries: Map<string, ConversationStatusEntry>;
  /**
   * 删除的会话与删除时的版本，较早发出的快照不能恢复这些记录。
   * Removed conversations and their removal revisions, so earlier snapshots cannot restore them.
   */
  removed: Map<string, number>;
  /**
   * 最近一次应用的快照请求版本，或删除全部会话时的版本；请求版本更小的快照不再应用。
   * The request revision of the last applied snapshot, or the revision of removing every conversation; snapshots requested earlier are not applied.
   */
  snapshotFloor: number;
  statuses: ReadonlyMap<string, ConversationStatus> | null;
};

const EMPTY_STATUSES: ReadonlyMap<string, ConversationStatus> = new Map();

function getScopeKey(scope: ConversationStatusScope): string {
  return JSON.stringify([scope.serverId, scope.projectId]);
}

/**
 * 按 (serverId, projectId) 在内存中保存会话列表右侧的执行状态。快照与 Turn 事件都写入这里，较早发出的快照不能覆盖请求发出后的本地更新。
 * Keeps the conversation list's execution statuses in memory per (serverId, projectId). Snapshots and turn events both write here, and an earlier snapshot cannot overwrite local updates made after its request.
 */
export class ConversationStatusStore {
  private readonly scopes = new Map<string, ScopeState>();
  private readonly listeners = new Set<() => void>();

  /**
   * 保存开始的 Turn；已结束 Turn 的重复开始消息不能恢复为 running。
   * Records a started turn; a repeated start of a finished turn cannot restore running.
   */
  public turnStarted(scope: ConversationStatusScope, conversationId: string, turnId: string): void {
    const state = this.getOrCreateScope(scope);
    const entry = state.entries.get(conversationId);
    if (entry?.turnId === turnId && entry.status !== "running") return;
    this.write(state, conversationId, turnId, "running");
  }

  /**
   * 写入 Turn 结局；只在没有记录或记录属于同一个 Turn 时写入，旧 Turn 的结束消息不能覆盖新 Turn。
   * Writes a turn outcome only when no record exists or the record belongs to the same turn, so an old turn's finish cannot overwrite a newer turn.
   */
  public turnFinished(
    scope: ConversationStatusScope,
    conversationId: string,
    turnId: string,
    result: TurnFinishedStatus,
  ): void {
    const state = this.getOrCreateScope(scope);
    const entry = state.entries.get(conversationId);
    if (entry && entry.turnId !== turnId) return;
    this.write(state, conversationId, turnId, result === "completed" ? "idle" : result);
  }

  /**
   * 发出快照请求前取得请求版本，之后的写入版本都大于它。
   * Returns the request revision before a snapshot request; every later write has a larger revision.
   */
  public beginSnapshot(scope: ConversationStatusScope): number {
    const state = this.getOrCreateScope(scope);
    state.revision += 1;
    return state.revision;
  }

  /**
   * 应用快照：请求发出后更新过的记录保持不变；快照中的会话写入快照结果；本地有记录、快照中没有的会话设为 idle 并保留 turnId。
   * Applies a snapshot: records updated after the request stay unchanged, conversations in the snapshot take its result, and local records missing from it become idle with their turnId kept.
   */
  public applySnapshot(
    scope: ConversationStatusScope,
    requestRevision: number,
    items: readonly ConversationStatusSnapshotItem[],
  ): void {
    const state = this.getOrCreateScope(scope);
    if (requestRevision < state.snapshotFloor) return;
    state.snapshotFloor = requestRevision;
    const snapshot = new Map(items.map((item) => [item.conversationId, item]));
    for (const [conversationId, entry] of state.entries) {
      if (entry.revision > requestRevision || snapshot.has(conversationId)) continue;
      state.entries.set(conversationId, {
        turnId: entry.turnId,
        status: "idle",
        revision: requestRevision,
      });
    }
    for (const item of snapshot.values()) {
      const entry = state.entries.get(item.conversationId);
      const removedRevision = state.removed.get(item.conversationId);
      if (
        (entry && entry.revision > requestRevision) ||
        (removedRevision !== undefined && removedRevision > requestRevision)
      ) {
        continue;
      }
      state.entries.set(item.conversationId, {
        turnId: item.turnId,
        status: item.status,
        revision: requestRevision,
      });
    }
    for (const [conversationId, revision] of state.removed) {
      if (revision <= requestRevision) state.removed.delete(conversationId);
    }
    this.emitChange(state);
  }

  /**
   * 清空会话记录后调用；服务端已删除该会话的全部 Turn。
   * Called after clearing a conversation's records; the server has deleted all of its turns.
   */
  public reset(scope: ConversationStatusScope, conversationId: string): void {
    this.write(this.getOrCreateScope(scope), conversationId, null, "idle");
  }

  public remove(scope: ConversationStatusScope, conversationId: string): void {
    const state = this.getOrCreateScope(scope);
    state.revision += 1;
    state.removed.set(conversationId, state.revision);
    if (state.entries.delete(conversationId)) this.emitChange(state);
  }

  public removeScope(scope: ConversationStatusScope): void {
    const state = this.getOrCreateScope(scope);
    state.revision += 1;
    state.snapshotFloor = state.revision;
    state.removed.clear();
    if (state.entries.size === 0) return;
    state.entries.clear();
    this.emitChange(state);
  }

  public get(
    scope: ConversationStatusScope,
    conversationId: string,
  ): ConversationStatusEntry | undefined {
    return this.scopes.get(getScopeKey(scope))?.entries.get(conversationId);
  }

  /**
   * 返回范围内的状态映射，范围内没有新写入时保持同一个实例；没有记录的会话为 idle。
   * Returns the scope's status map, keeping the same instance until the scope is written; conversations without a record are idle.
   */
  public getStatuses(scope: ConversationStatusScope): ReadonlyMap<string, ConversationStatus> {
    const state = this.scopes.get(getScopeKey(scope));
    if (!state) return EMPTY_STATUSES;
    state.statuses ??= new Map(
      [...state.entries].map(([conversationId, entry]) => [conversationId, entry.status]),
    );
    return state.statuses;
  }

  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  private getOrCreateScope(scope: ConversationStatusScope): ScopeState {
    const key = getScopeKey(scope);
    let state = this.scopes.get(key);
    if (!state) {
      state = {
        revision: 0,
        entries: new Map(),
        removed: new Map(),
        snapshotFloor: 0,
        statuses: null,
      };
      this.scopes.set(key, state);
    }
    return state;
  }

  private write(
    state: ScopeState,
    conversationId: string,
    turnId: string | null,
    status: ConversationStatus,
  ): void {
    state.revision += 1;
    state.entries.set(conversationId, { turnId, status, revision: state.revision });
    state.removed.delete(conversationId);
    this.emitChange(state);
  }

  private emitChange(state: ScopeState): void {
    state.statuses = null;
    for (const listener of this.listeners) listener();
  }
}
