/** Windows 与 macOS 的文件系统默认不区分大小写，键转为小写。Windows and macOS file systems ignore case by default, so keys are lower-cased. */
export function identityKey(path: string): string {
  return process.platform === "win32" || process.platform === "darwin" ? path.toLowerCase() : path;
}

/**
 * 按文件身份串行执行写入的锁表，所有 extension 共用。同时需要多个文件时按排序后的顺序获取，持有者之间不会互相等待。
 * A lock table that serializes writes per file identity, shared by every extension. Several files are taken in sorted order, so holders never wait on each other.
 */
export class FileIdentityLocks {
  /** 每个键的队列：第一项是当前持有者，其余按到达顺序等待。Per key: the first entry holds the lock, the rest wait in arrival order. */
  private readonly queues = new Map<string, Array<() => void>>();

  /** `paths` 为已经解析过的完整物理路径。`paths` are resolved full physical paths. */
  public async run<T>(paths: readonly string[], operation: () => Promise<T>): Promise<T> {
    const keys = [...new Set(paths.map(identityKey))].sort();
    const held: string[] = [];
    try {
      for (const key of keys) {
        await this.acquire(key);
        held.push(key);
      }
      return await operation();
    } finally {
      for (const key of held.reverse()) this.release(key);
    }
  }

  private acquire(key: string): Promise<void> {
    return new Promise((resolve) => {
      const queue = this.queues.get(key);
      if (queue) {
        queue.push(resolve);
        return;
      }
      this.queues.set(key, [resolve]);
      resolve();
    });
  }

  private release(key: string): void {
    const queue = this.queues.get(key)!;
    queue.shift();
    if (queue.length === 0) this.queues.delete(key);
    else queue[0]();
  }
}
