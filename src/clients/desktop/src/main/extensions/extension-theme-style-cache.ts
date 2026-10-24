import { type FSWatcher, watch } from "node:fs";
import { realpath, stat } from "node:fs/promises";
import { basename, dirname } from "node:path";

import {
  type CompiledThemeCss,
  compileCssTheme,
  THEME_CSS_COMPILER_VERSION,
} from "./extension-theme-css";
import type { ThemeDependency } from "./extension-theme-resources";

/** 编译缓存按编译文本体积计算的上限。The compile cache limit, counted by compiled text size. */
export const MAX_THEME_STYLE_CACHE_BYTES = 64 * 1024 * 1024;

/** 编译后的主题样式。A compiled theme style. */
export type ThemeStyle = { styleId: string; cssText: string; byteLength: number };

type CacheEntry = {
  key: string;
  rootRealPath: string;
  style: ThemeStyle;
  dependencies: ThemeDependency[];
  watchers: FSWatcher[];
};

async function isFresh(dependency: ThemeDependency): Promise<boolean> {
  try {
    if ((await realpath(dependency.path)) !== dependency.realPath) return false;
    const info = await stat(dependency.realPath, { bigint: true });
    return (
      info.isFile() &&
      info.dev === dependency.dev &&
      info.ino === dependency.ino &&
      info.size === dependency.size &&
      info.mtimeNs === dependency.mtimeNs &&
      info.ctimeNs === dependency.ctimeNs
    );
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === "ENOENT" || code === "ENOTDIR" || code === "ELOOP") return false;
    throw error;
  }
}

/**
 * CSS 主题的编译缓存：缓存入口及完整依赖图的编译结果。命中前复核每个依赖的引用路径、实际路径、文件标识、大小、纳秒级修改时间与
 * 状态变更时间；监听依赖所在目录，变化时使条目失效。按编译文本体积采用 LRU，当前正在使用的主题保持引用。
 * The compile cache of CSS themes, holding the compile result of an entry and its whole dependency graph. Before a
 * hit, every dependency's referenced path, real path, file identity, size, nanosecond modification time and status
 * change time are checked again; the directories of the dependencies are watched, and changes invalidate the entry.
 * Entries follow an LRU by compiled text size, and the theme in use stays referenced.
 */
export class ThemeStyleCompiler {
  private readonly entries = new Map<string, CacheEntry>();
  private bytes = 0;
  private pinnedKey: string | null = null;

  public constructor(private readonly maxBytes = MAX_THEME_STYLE_CACHE_BYTES) {}

  /** 编译主题入口；依赖未变时复用缓存。Compiles a theme entry, reusing the cache while no dependency changed. */
  public async compile(rootDirectory: string, entryPath: string): Promise<ThemeStyle> {
    const rootRealPath = await realpath(rootDirectory);
    const key = `${THEME_CSS_COMPILER_VERSION}\u0000${rootRealPath}\u0000${entryPath}`;
    const cached = this.entries.get(key);
    if (cached) {
      const checks = await Promise.all(cached.dependencies.map(isFresh));
      // 检查期间条目可能已被监听事件移除。The entry may have been removed by a watch event during the checks.
      if (checks.every(Boolean) && this.entries.get(key) === cached) {
        this.entries.delete(key);
        this.entries.set(key, cached);
        return cached.style;
      }
      this.remove(key);
    }
    return this.store(key, rootRealPath, await compileCssTheme(rootRealPath, entryPath));
  }

  /**
   * 当前正在使用的主题：它的条目不会因为 LRU 被释放。传入 null 取消保留。
   * The theme in use: its entry is never released by the LRU. Null clears the hold.
   */
  public async pin(rootDirectory: string | null, entryPath: string | null): Promise<void> {
    this.pinnedKey =
      rootDirectory === null || entryPath === null
        ? null
        : `${THEME_CSS_COMPILER_VERSION}\u0000${await realpath(rootDirectory)}\u0000${entryPath}`;
    this.trim();
  }

  /** 显式 reload、安装替换或删除时，释放该目录下全部入口的缓存。Explicit reloads, replacements and removals drop every entry under the directory. */
  public async invalidate(rootDirectory: string): Promise<void> {
    let rootRealPath = rootDirectory;
    try {
      rootRealPath = await realpath(rootDirectory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    for (const entry of this.entries.values()) {
      if (entry.rootRealPath === rootRealPath || entry.rootRealPath === rootDirectory) {
        this.remove(entry.key);
      }
    }
  }

  /** 缓存的条目数量与文本体积。The cached entry count and text size. */
  public stats(): { entries: number; bytes: number } {
    return { entries: this.entries.size, bytes: this.bytes };
  }

  private store(key: string, rootRealPath: string, compiled: CompiledThemeCss): ThemeStyle {
    const entry: CacheEntry = {
      key,
      rootRealPath,
      style: {
        styleId: compiled.styleId,
        cssText: compiled.cssText,
        byteLength: compiled.byteLength,
      },
      dependencies: compiled.dependencies,
      watchers: [],
    };
    this.entries.set(key, entry);
    this.bytes += compiled.byteLength;
    // 监听引用路径与实际路径所在的目录：文件内容变化、替换与符号链接变化都会产生事件。
    // Watch the directories of the referenced and real paths: content changes, replacements and link changes all
    // produce events.
    const names = new Map<string, Set<string>>();
    for (const dependency of compiled.dependencies) {
      for (const path of [dependency.path, dependency.realPath]) {
        const directory = dirname(path);
        const set = names.get(directory) ?? new Set<string>();
        set.add(basename(path));
        names.set(directory, set);
      }
    }
    for (const [directory, files] of names) {
      try {
        const watcher = watch(directory, { persistent: false }, (_event, filename) => {
          if (filename === null || files.has(basename(filename.toString()))) this.remove(key);
        });
        watcher.on("error", () => this.remove(key));
        entry.watchers.push(watcher);
      } catch (error) {
        const code = (error as NodeJS.ErrnoException).code;
        if (code !== "ENOENT" && code !== "ENOTDIR") throw error;
        // 目录在编译之后被删除：条目已经过期。The directory was deleted after the compile, so the entry is stale.
        this.remove(key);
        return entry.style;
      }
    }
    this.trim();
    return entry.style;
  }

  private remove(key: string): void {
    const entry = this.entries.get(key);
    if (!entry) return;
    this.entries.delete(key);
    this.bytes -= entry.style.byteLength;
    for (const watcher of entry.watchers) watcher.close();
  }

  private trim(): void {
    for (const key of this.entries.keys()) {
      if (this.bytes <= this.maxBytes) return;
      if (key !== this.pinnedKey) this.remove(key);
    }
  }
}

/** 主进程共用的编译缓存。The compile cache shared by the main process. */
export const themeStyleCompiler = new ThemeStyleCompiler();
