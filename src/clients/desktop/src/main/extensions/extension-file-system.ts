import {
  lstat,
  mkdir,
  readdir,
  readFile,
  rename,
  rm,
  rmdir,
  stat,
  unlink,
  writeFile,
} from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import type { ExtensionFileSystemRequest, ExtensionFileSystemResult } from "../../shared/contracts";
import type { DirectoryEntry, ExtensionPermission, FileStat, FileType } from "@agw/extension-sdk";
import type { ExtensionAccessGrants } from "./extension-access-grants";
import { isPathInside } from "./extension-catalog";
import { ExtensionOperationError } from "./extension-ipc-result";
import { entryJudgementPath, judgementPath } from "./extension-path-judgement";
import { ReadWriteLock } from "./read-write-lock";

/** 单次读取或写入的上限。The limit for a single read or write. */
export const MAX_FILE_TRANSFER_BYTES = 100 * 1024 * 1024;

export type FileAccessContext = {
  extensionId: string;
  grantedPermissions: readonly ExtensionPermission[];
  storageDirectory: string;
};

type Access = "read" | "write";

/** 本地 `file:` URI 转换为绝对路径。Converts a local `file:` URI into an absolute path. */
export function localPathFromUri(uri: string): string {
  let url: URL;
  try {
    url = new URL(uri);
  } catch {
    throw new ExtensionOperationError("ERR_INVALID_URI", `Invalid URI: ${uri}`);
  }
  if (url.protocol !== "file:" || url.host !== "") {
    throw new ExtensionOperationError("ERR_INVALID_URI", `Not a local file URI: ${uri}`);
  }
  try {
    return fileURLToPath(url);
  } catch {
    throw new ExtensionOperationError("ERR_INVALID_URI", `Not a local file URI: ${uri}`);
  }
}

function fileType(info: { isDirectory(): boolean; isSymbolicLink(): boolean }): FileType {
  if (info.isSymbolicLink()) return "symlink";
  return info.isDirectory() ? "directory" : "file";
}

function tooLarge(): ExtensionOperationError {
  return new ExtensionOperationError(
    "ERR_FILE_TOO_LARGE",
    `A single read or write cannot exceed ${MAX_FILE_TRANSFER_BYTES / 1024 / 1024} MB.`,
  );
}

/**
 * 主进程的本地文件访问：先判断路径与权限，判断、创建父目录与实际读写在同一次持锁期间完成。
 * Main-process local file access: the path check, parent creation and the actual IO happen within one lock hold.
 */
export class ExtensionFileSystem {
  private readonly locks = new Map<string, ReadWriteLock>();

  public constructor(private readonly grants: ExtensionAccessGrants) {}

  public async execute<R extends ExtensionFileSystemRequest>(
    context: FileAccessContext,
    request: R,
  ): Promise<ExtensionFileSystemResult[R["operation"]]> {
    return (await this.dispatch(context, request)) as ExtensionFileSystemResult[R["operation"]];
  }

  /** 在读锁内检查读取权限后执行（`watch` 登记、Reveal）。Runs after a read check under the read lock (watch registration, reveal). */
  public async withReadAccess<T>(
    context: FileAccessContext,
    uri: string,
    operation: (path: string) => Promise<T>,
  ): Promise<T> {
    const path = localPathFromUri(uri);
    return this.lock(context.extensionId).read(async () => {
      await this.check(context, await judgementPath(path), "read");
      return operation(path);
    });
  }

  private dispatch(
    context: FileAccessContext,
    request: ExtensionFileSystemRequest,
  ): Promise<unknown> {
    const lock = this.lock(context.extensionId);
    switch (request.operation) {
      case "readFile": {
        const path = localPathFromUri(request.uri);
        return lock.read(async () => {
          await this.check(context, await judgementPath(path), "read");
          if ((await stat(path)).size > MAX_FILE_TRANSFER_BYTES) throw tooLarge();
          const data = await readFile(path);
          if (data.byteLength > MAX_FILE_TRANSFER_BYTES) throw tooLarge();
          return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
        });
      }
      case "readDirectory": {
        const path = localPathFromUri(request.uri);
        return lock.read(async () => {
          await this.check(context, await judgementPath(path), "read");
          const entries = await readdir(path, { withFileTypes: true });
          return entries.map((entry): DirectoryEntry => ({
            name: entry.name,
            uri: pathToFileURL(join(path, entry.name)).toString(),
            type: fileType(entry),
          }));
        });
      }
      case "stat": {
        const path = localPathFromUri(request.uri);
        return lock.read(async (): Promise<FileStat> => {
          await this.check(context, await judgementPath(path), "read");
          const info = await lstat(path);
          return {
            type: fileType(info),
            size: info.isDirectory() ? null : info.size,
            modifiedTime: info.mtime.toISOString(),
          };
        });
      }
      case "exists": {
        const path = localPathFromUri(request.uri);
        return lock.read(async () => {
          await this.check(context, await judgementPath(path), "read");
          try {
            await lstat(path);
            return true;
          } catch (error) {
            if ((error as NodeJS.ErrnoException).code === "ENOENT") return false;
            throw error;
          }
        });
      }
      case "writeFile": {
        const path = localPathFromUri(request.uri);
        if (request.data.byteLength > MAX_FILE_TRANSFER_BYTES) return Promise.reject(tooLarge());
        return lock.write(async () => {
          await this.check(context, await judgementPath(path), "write");
          if (request.createParents) await mkdir(dirname(path), { recursive: true });
          await writeFile(path, request.data, { flag: request.overwrite ? "w" : "wx" });
          return null;
        });
      }
      case "createDirectory": {
        const path = localPathFromUri(request.uri);
        return lock.write(async () => {
          await this.check(context, await judgementPath(path), "write");
          await mkdir(path, { recursive: request.recursive });
          return null;
        });
      }
      case "delete": {
        const path = localPathFromUri(request.uri);
        return lock.write(async () => {
          await this.check(context, await entryJudgementPath(path), "write");
          const info = await lstat(path);
          if (request.recursive) await rm(path, { recursive: true });
          else if (info.isDirectory()) await rmdir(path);
          else await unlink(path);
          return null;
        });
      }
      case "rename": {
        const from = localPathFromUri(request.from);
        const to = localPathFromUri(request.to);
        return lock.write(async () => {
          await this.check(context, await entryJudgementPath(from), "write");
          await this.check(context, await entryJudgementPath(to), "write");
          if (!request.overwrite) {
            try {
              await lstat(to);
              throw Object.assign(
                new Error(`EEXIST: file already exists, rename '${from}' -> '${to}'`),
                {
                  code: "EEXIST",
                  syscall: "rename",
                  path: from,
                  dest: to,
                },
              );
            } catch (error) {
              if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
            }
          }
          await rename(from, to);
          return null;
        });
      }
    }
  }

  private async check(
    context: FileAccessContext,
    judgedPath: string,
    access: Access,
  ): Promise<void> {
    const storage = await judgementPath(context.storageDirectory);
    if (isPathInside(storage, judgedPath)) return;
    const permission: ExtensionPermission = access === "read" ? "fs.local.read" : "fs.local.write";
    if (context.grantedPermissions.includes(permission)) return;
    if (this.grants.allows(context.extensionId, judgedPath, access)) return;
    throw new ExtensionOperationError(
      "ERR_PERMISSION_DENIED",
      `The extension is not allowed to ${access} this path.`,
    );
  }

  private lock(extensionId: string): ReadWriteLock {
    let lock = this.locks.get(extensionId);
    if (!lock) {
      lock = new ReadWriteLock();
      this.locks.set(extensionId, lock);
    }
    return lock;
  }
}
