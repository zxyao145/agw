/** 记录保留的时间：监听事件晚于提交到达，超过这个时间的记录不再使用。How long a record is kept; watch events arrive after the commit. */
export const REPLACED_FILE_WINDOW_MS = 2_000;

/**
 * Agw 用 rename 替换已有文件的记录。新文件有新的 inode 与创建时间，监听据此把这次写入报告为 changed，
 * 外部程序的替换不在这里，保持原来的判断。
 * Records of existing files Agw replaced with a rename. The new file has a new inode and birth time, so watchers use
 * these records to report the write as changed; replacements by other programs are not recorded and keep the usual rule.
 */
export class ReplacedFileRegistry {
  private sequence = 0;
  private readonly pending = new Map<string, Promise<void>>();
  /** 按 realpath 解析后的路径记录最近一次替换的编号与时间。The latest replacement's number and time, by resolved path. */
  private readonly entries = new Map<string, { id: number; at: number }>();

  /** 跟踪进行中的替换，提交成功后记录目标。Tracks a pending replacement and records its target after a successful commit. */
  public async record(path: string, commit: Promise<void>): Promise<void> {
    let resolvePending!: () => void;
    const promise = new Promise<void>((resolve) => {
      resolvePending = resolve;
    });
    this.pending.set(path, promise);
    try {
      await commit;
      const now = Date.now();
      this.prune(now);
      this.sequence += 1;
      this.entries.set(path, { id: this.sequence, at: now });
    } finally {
      this.pending.delete(path);
      resolvePending();
    }
  }

  /** 等待进行中的提交，返回时间窗内最近一次替换的编号。Waits for a pending commit and returns the latest replacement's number within the window. */
  public async latest(path: string): Promise<number | null> {
    await this.pending.get(path);
    this.prune(Date.now());
    return this.entries.get(path)?.id ?? null;
  }

  private prune(now: number): void {
    for (const [path, entry] of this.entries) {
      if (now - entry.at > REPLACED_FILE_WINDOW_MS) this.entries.delete(path);
    }
  }
}
