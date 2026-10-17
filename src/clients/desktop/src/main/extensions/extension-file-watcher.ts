import { type FSWatcher, watch } from "node:fs";
import { lstat, stat } from "node:fs/promises";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

import type { ExtensionEvent } from "../../shared/contracts";
import { ExtensionOperationError } from "./extension-ipc-result";
import { ExtensionResourceTracker, type ResourceOwner } from "./extension-resource-tracker";

export const MAX_WATCHERS_PER_EXTENSION = 32;

/** 本地文件监听，每个 extension 最多 32 个。Local file watching, at most 32 per extension. */
export class ExtensionFileWatcher {
  private readonly watchers = new ExtensionResourceTracker<FSWatcher>((watcher) => watcher.close());

  public constructor(
    private readonly emit: (event: Extract<ExtensionEvent, { type: "file-change" }>) => void,
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
    const startedAt = Date.now();
    const inodes = new Map<string, number>();
    const watcher = watch(path, { recursive }, (eventType, filename) => {
      const changed = isDirectory && filename ? join(path, filename.toString()) : path;
      void this.translate(owner, watchId, eventType, changed, startedAt, inodes);
    });
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
   * `rename` 按文件是否存在转换为 created 或 deleted，`change` 转换为 changed。macOS 上修改文件也可能报告
   * `rename`，因此同一 inode 或监听开始前就存在的文件报告为 changed。
   * `rename` becomes created or deleted by existence and `change` becomes changed. On macOS edits may also report
   * `rename`, so the same inode or a file that predates the watcher reports changed.
   */
  private async translate(
    owner: ResourceOwner,
    watchId: string,
    eventType: string,
    path: string,
    startedAt: number,
    inodes: Map<string, number>,
  ): Promise<void> {
    let type: "created" | "changed" | "deleted";
    try {
      const info = await lstat(path);
      const known = inodes.get(path);
      const createdAt = info.birthtimeMs > 0 ? info.birthtimeMs : info.ctimeMs;
      type =
        eventType === "change" ||
        known === info.ino ||
        (known === undefined && createdAt < startedAt)
          ? "changed"
          : "created";
      inodes.set(path, info.ino);
    } catch {
      type = "deleted";
      inodes.delete(path);
    }
    if (!this.watchers.get(this.key(owner.extensionId, watchId))) return;
    this.emit({
      type: "file-change",
      extensionId: owner.extensionId,
      frameId: owner.frameId,
      pageId: owner.pageId,
      watchId,
      event: { type, uri: pathToFileURL(path).toString() },
    });
  }
}
