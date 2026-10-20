import { type FSWatcher, watch } from "node:fs";
import { lstat, realpath, stat } from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import { pathToFileURL } from "node:url";

import type { ExtensionEvent } from "../../shared/contracts";
import { TEMPORARY_FILE_PATTERN } from "./extension-file-system";
import { ExtensionOperationError } from "./extension-ipc-result";
import { ExtensionResourceTracker, type ResourceOwner } from "./extension-resource-tracker";
import type { ReplacedFileRegistry } from "./replaced-file-registry";

export const MAX_WATCHERS_PER_EXTENSION = 32;

/** Agw 写入时的临时文件不报告。Agw's own temporary files are never reported. */
function isTemporaryFile(filename: string | null): boolean {
  return filename !== null && TEMPORARY_FILE_PATTERN.test(basename(filename));
}

/** 一个监听的状态，键都是 realpath 解析后的路径。One watch's state, keyed by resolved paths. */
type WatchState = {
  startedAt: number;
  /** 已知存在的路径。Paths known to exist. */
  existing: Set<string>;
  /** 这个监听已经用过的替换记录编号。Replacement record numbers this watch already used. */
  usedReplacements: Map<string, number>;
};

/** 本地文件监听，每个 extension 最多 32 个。Local file watching, at most 32 per extension. */
export class ExtensionFileWatcher {
  private readonly watchers = new ExtensionResourceTracker<FSWatcher>((watcher) => watcher.close());

  public constructor(
    private readonly emit: (event: Extract<ExtensionEvent, { type: "file-change" }>) => void,
    private readonly replacedFiles: ReplacedFileRegistry,
  ) {}

  public async add(
    owner: ResourceOwner,
    watchId: string,
    path: string,
    recursive: boolean,
  ): Promise<void> {
    if (
      this.watchers.count((item) => item.extensionId === owner.extensionId) >=
      MAX_WATCHERS_PER_EXTENSION
    ) {
      throw new ExtensionOperationError(
        "ERR_TOO_MANY_WATCHERS",
        `An extension can watch at most ${MAX_WATCHERS_PER_EXTENSION} paths.`,
      );
    }
    const isDirectory = (await stat(path)).isDirectory();
    const state: WatchState = {
      startedAt: Date.now(),
      existing: new Set(),
      usedReplacements: new Map(),
    };
    let watcher: FSWatcher;
    if (isDirectory) {
      // 按 realpath 解析后的目录检查文件，与替换记录的路径一致；事件仍使用请求的路径。
      // Files are checked under the resolved directory, matching the replacement records; events keep the requested path.
      const realDirectory = await realpath(path);
      watcher = watch(path, { recursive }, (eventType, filename) => {
        if (isTemporaryFile(filename)) return;
        const statPath = filename ? join(realDirectory, filename) : realDirectory;
        const uriPath = filename ? join(path, filename) : path;
        void this.translate(owner, watchId, eventType, statPath, uriPath, state);
      });
    } else {
      // 单个文件通过 realpath 解析后所在的目录监听，只取它的事件：原子写入用 rename 替换文件，直接监听文件会停留在旧的 inode 上。
      // realpath 解析符号链接，并给出磁盘上的大小写与 Unicode 形式，与目录事件中的文件名一致；事件仍使用请求的 URI。
      // A single file is watched through the directory of its real path, keeping only its events: atomic writes replace
      // the file by rename, and a watch on the file itself would stay on the old inode. realpath resolves symbolic links
      // and gives the on-disk case and Unicode form that directory events use; events keep the requested URI.
      const real = await realpath(path);
      const name = basename(real);
      state.existing.add(real);
      watcher = watch(dirname(real), (eventType, filename) => {
        if (isTemporaryFile(filename) || (filename && filename !== name)) return;
        void this.translate(owner, watchId, eventType, real, path, state);
      });
    }
    watcher.on("error", () => this.watchers.remove(this.key(owner.extensionId, watchId)));
    this.watchers.add(this.key(owner.extensionId, watchId), owner, watcher);
  }

  public remove(extensionId: string, watchId: string): void {
    this.watchers.remove(this.key(extensionId, watchId));
  }

  public count(predicate: (owner: ResourceOwner) => boolean): number {
    return this.watchers.count(predicate);
  }

  public releasePage(frameId: string, pageId: string): void {
    this.watchers.releasePage(frameId, pageId);
  }

  public releaseFrame(extensionId: string, frameId: string): void {
    this.watchers.releaseFrame(extensionId, frameId);
  }

  public releaseExtension(extensionId: string): void {
    this.watchers.releaseExtension(extensionId);
  }

  public releaseAll(): void {
    this.watchers.releaseAll();
  }

  private key(extensionId: string, watchId: string): string {
    return `${extensionId}\u0000${watchId}`;
  }

  /**
   * Agw 在时间窗内用 rename 替换了这个文件、并且这个监听还没有用过这条记录时返回 true，每条记录对每个监听只用一次。
   * 必须在 lstat 返回之后调用：那时提交方已经记录了替换。
   * True when Agw replaced this file with a rename within the window and this watch has not used that record yet; each
   * record is used once per watch. Call it after lstat returns, when the committer has already recorded the replacement.
   */
  private takeReplacement(statPath: string, state: WatchState): boolean {
    const replacement = this.replacedFiles.latest(statPath);
    if (replacement === null) {
      state.usedReplacements.delete(statPath);
      return false;
    }
    if (state.usedReplacements.get(statPath) === replacement) return false;
    state.usedReplacements.set(statPath, replacement);
    return true;
  }

  /**
   * `rename` 按文件是否存在转换为 created 或 deleted，`change` 转换为 changed。macOS 上修改文件也可能报告
   * `rename`，因此 Agw 刚替换的文件、已知存在的路径（包括被 rename 替换为新 inode 的文件）或监听开始前就存在的文件
   * 报告为 changed。`statPath` 是检查是否存在的、经过 realpath 解析的路径，`uriPath` 是事件中报告的路径。
   * `rename` becomes created or deleted by existence and `change` becomes changed. On macOS edits may also report
   * `rename`, so a file Agw just replaced, a path known to exist (including a file replaced by a rename with a new inode)
   * or a file that predates the watcher reports changed. `statPath` is the resolved path checked for existence; `uriPath`
   * is the path reported.
   */
  private async translate(
    owner: ResourceOwner,
    watchId: string,
    eventType: string,
    statPath: string,
    uriPath: string,
    state: WatchState,
  ): Promise<void> {
    let type: "created" | "changed" | "deleted";
    try {
      const info = await lstat(statPath);
      const replaced = this.takeReplacement(statPath, state);
      const createdAt = info.birthtimeMs > 0 ? info.birthtimeMs : info.ctimeMs;
      type =
        eventType === "change" ||
        replaced ||
        state.existing.has(statPath) ||
        createdAt < state.startedAt
          ? "changed"
          : "created";
      state.existing.add(statPath);
    } catch {
      this.takeReplacement(statPath, state);
      type = "deleted";
      state.existing.delete(statPath);
    }
    if (!this.watchers.get(this.key(owner.extensionId, watchId))) return;
    this.emit({
      type: "file-change",
      extensionId: owner.extensionId,
      frameId: owner.frameId,
      pageId: owner.pageId,
      watchId,
      event: { type, uri: pathToFileURL(uriPath).toString() },
    });
  }
}
