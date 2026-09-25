import type {
  ConversationStatusScope,
  ConversationStatusSnapshotItem,
  ConversationStatusStore,
} from "@agw/chat-runtime";

export type ConversationStatusSnapshotLoader = (
  projectId: string,
  signal: AbortSignal,
) => Promise<readonly ConversationStatusSnapshotItem[]>;

type SnapshotRefresh = {
  requestAgain: boolean;
  promise: Promise<void>;
};

/**
 * 获取会话状态快照并写入状态库。同一范围同时只有一个请求；请求期间再次触发时，当前请求结束后再请求一次。
 * Fetches conversation status snapshots into the store. Each scope has at most one request; triggers during a request cause exactly one more request after it.
 */
export class ConversationStatusSnapshots {
  private readonly store: ConversationStatusStore;
  private readonly load: ConversationStatusSnapshotLoader;
  private readonly onError: (error: unknown) => void;
  private readonly abortController = new AbortController();
  private readonly refreshes = new Map<string, SnapshotRefresh>();

  public constructor(
    store: ConversationStatusStore,
    load: ConversationStatusSnapshotLoader,
    onError: (error: unknown) => void,
  ) {
    this.store = store;
    this.load = load;
    this.onError = onError;
  }

  public refresh(scope: ConversationStatusScope): Promise<void> {
    const key = JSON.stringify([scope.serverId, scope.projectId]);
    const active = this.refreshes.get(key);
    if (active) {
      active.requestAgain = true;
      return active.promise;
    }

    const refresh: SnapshotRefresh = { requestAgain: false, promise: Promise.resolve() };
    const signal = this.abortController.signal;
    const run = async () => {
      do {
        refresh.requestAgain = false;
        const requestRevision = this.store.beginSnapshot(scope);
        try {
          const items = await this.load(scope.projectId, signal);
          if (signal.aborted) return;
          this.store.applySnapshot(scope, requestRevision, items);
        } catch (error) {
          if (signal.aborted) return;
          // 失败时保留已有状态。Existing statuses stay when a request fails.
          this.onError(error);
        }
      } while (refresh.requestAgain && !signal.aborted);
    };
    refresh.promise = run().finally(() => {
      if (this.refreshes.get(key) === refresh) this.refreshes.delete(key);
    });
    this.refreshes.set(key, refresh);
    return refresh.promise;
  }

  /** 取消进行中的请求，之后不再写入状态库。Cancels in-flight requests; nothing is written afterwards. */
  public dispose(): void {
    this.abortController.abort();
  }
}
