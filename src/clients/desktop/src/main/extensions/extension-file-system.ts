import { createHash, randomBytes } from "node:crypto";
import { type BigIntStats, constants, type Dirent, type Stats } from "node:fs";
import {
  access,
  type FileHandle,
  link,
  lstat,
  mkdir,
  open,
  readdir,
  readFile,
  rename,
  rm,
  rmdir,
  stat,
  unlink,
} from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { getSystemErrorMessage } from "node:util";

import type { ExtensionFileSystemRequest, ExtensionFileSystemResult } from "../../shared/contracts";
import type { DirectoryEntry, ExtensionPermission, FileStat, FileType } from "@agw/extension-sdk";
import type { ExtensionAccessGrants } from "./extension-access-grants";
import { isPathInside } from "./extension-catalog";
import { ExtensionOperationError } from "./extension-ipc-result";
import { entryJudgementPath, judgementPath } from "./extension-path-judgement";
import { FileIdentityLocks, identityKey } from "./file-identity-locks";
import { ReadWriteLock } from "./read-write-lock";
import type { ReplacedFileRegistry } from "./replaced-file-registry";

/** 单次读取或写入的上限。The limit for a single read or write. */
export const MAX_FILE_TRANSFER_BYTES = 100 * 1024 * 1024;

/**
 * Agw 写入时在目标旁边创建的临时文件名：`.{文件名}.agw-{32 位十六进制}.tmp`（见 `commitFile`）。
 * The temporary file Agw creates next to the target while writing: `.{name}.agw-{32 hex}.tmp` (see `commitFile`).
 */
export const TEMPORARY_FILE_PATTERN = /^\..+\.agw-[0-9a-f]{32}\.tmp$/u;

const SHA256_PATTERN = /^[0-9a-f]{64}$/u;

const HASH_CHUNK_BYTES = 1024 * 1024;

type CommitOptions = {
  overwrite: boolean;
  createParents: boolean;
  expectedSha256: string | undefined;
};

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

function fileChanged(message: string): ExtensionOperationError {
  return new ExtensionOperationError("ERR_FILE_CHANGED", message);
}

function errorCode(error: unknown): string | undefined {
  return (error as NodeJS.ErrnoException).code;
}

/** `expectedSha256` 为 64 位小写十六进制，并且只能与 `overwrite: true` 一起使用。`expectedSha256` is 64 lowercase hex digits and needs `overwrite: true`. */
function writePreconditionError(
  request: Extract<ExtensionFileSystemRequest, { operation: "writeFile" }>,
): ExtensionOperationError | null {
  if (request.expectedSha256 === undefined) return null;
  if (typeof request.expectedSha256 !== "string" || !SHA256_PATTERN.test(request.expectedSha256)) {
    return new ExtensionOperationError(
      "ERR_INVALID_ARGUMENT",
      "expectedSha256 must be 64 lowercase hexadecimal characters.",
    );
  }
  if (!request.overwrite) {
    return new ExtensionOperationError(
      "ERR_INVALID_ARGUMENT",
      "expectedSha256 can only be used with overwrite: true.",
    );
  }
  return null;
}

/** 与 Node 的 `open` 相同形式的错误，保持原有的错误码。An error shaped like Node's `open` errors, keeping the existing codes. */
function openError(code: "EEXIST" | "EISDIR", description: string, path: string): Error {
  return Object.assign(new Error(`${code}: ${description}, open '${path}'`), {
    code,
    syscall: "open",
    path,
  });
}

/**
 * 临时文件是内部文件：对它的系统调用失败时，错误码与系统调用不变，错误中的路径改为目标文件。
 * Temporary files are internal: when a system call on one fails, the code and syscall stay and the error names the target.
 */
function targetError(error: unknown, target: string): unknown {
  const { code, errno, syscall } = error as NodeJS.ErrnoException;
  if (code === undefined || errno === undefined || syscall === undefined) return error;
  return Object.assign(
    new Error(`${code}: ${getSystemErrorMessage(errno)}, ${syscall} '${target}'`),
    {
      code,
      errno,
      syscall,
      path: target,
    },
  );
}

/**
 * 执行不能改变结果的清理：失败只记录到主进程日志，原来的错误或成功的结果不受影响。
 * Runs a cleanup that must not change the outcome: a failure is only logged to the main-process log.
 */
async function cleanUp(description: string, cleanup: () => Promise<unknown>): Promise<void> {
  try {
    await cleanup();
  } catch (cleanupError) {
    console.warn(description, cleanupError);
  }
}

/** 执行 operation 后关闭句柄；operation 失败时关闭的错误不替换它的错误。Closes the handle after operation; a close failure never replaces operation's error. */
async function closingAfter<T>(
  handle: FileHandle,
  path: string,
  operation: () => Promise<T>,
): Promise<T> {
  let result: T;
  try {
    result = await operation();
  } catch (error) {
    await cleanUp(`Unable to close ${path}.`, () => handle.close());
    throw error;
  }
  await handle.close();
  return result;
}

/**
 * 第 1 步：用同一个句柄记录文件标识并计算内容哈希；文件不存在或哈希不同时为 ERR_FILE_CHANGED。
 * Step 1: records the file identity and hashes the content through one handle; a missing file or another hash is ERR_FILE_CHANGED.
 */
async function verifyContent(target: string, expectedSha256: string): Promise<BigIntStats> {
  let handle: FileHandle;
  try {
    handle = await open(target, "r");
  } catch (error) {
    if (errorCode(error) === "ENOENT") throw fileChanged("The file no longer exists.");
    throw error;
  }
  return closingAfter(handle, target, async () => {
    const identity = await handle.stat({ bigint: true });
    const hash = createHash("sha256");
    const buffer = Buffer.alloc(HASH_CHUNK_BYTES);
    for (;;) {
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, null);
      if (bytesRead === 0) break;
      hash.update(buffer.subarray(0, bytesRead));
    }
    if (hash.digest("hex") !== expectedSha256) {
      throw fileChanged("The file was changed after it was read.");
    }
    return identity;
  });
}

async function statIfExists(path: string): Promise<Stats | null> {
  try {
    return await stat(path);
  } catch (error) {
    if (errorCode(error) === "ENOENT") return null;
    throw error;
  }
}

function sameFile(before: BigIntStats, after: BigIntStats): boolean {
  return (
    before.dev === after.dev &&
    before.ino === after.ino &&
    before.size === after.size &&
    before.mtimeNs === after.mtimeNs
  );
}

/**
 * `link` 的错误说明文件系统不支持 hard link：macOS 为 ENOTSUP，Linux 为 EPERM；Windows 的 EPERM 表示没有权限。
 * A `link` error meaning the file system has no hard links: ENOTSUP on macOS, EPERM on Linux; EPERM on Windows means access denied.
 */
function lacksHardLinks(error: unknown): boolean {
  const code = errorCode(error);
  if (code === "ENOTSUP" || code === "EOPNOTSUPP") return true;
  return code === "EPERM" && process.platform === "linux";
}

/**
 * 删除以前的写入为目标留下的临时文件（大小写规则与文件身份锁相同）。调用方持有这个文件的身份锁，Desktop 的其他写入
 * 不会正在使用它们；服务端同时写同一个文件时可能正在使用，见设计 11.3 节末尾的说明。目录不存在时没有临时文件，随后
 * 创建临时文件时报告错误。删除失败时错误中的路径为目标文件。
 * Deletes the temporary files earlier writes left for the target (letter case as in the identity locks). The caller holds
 * the file's identity lock, so no other Desktop write is using them; a server write to the same file at the same time
 * may be (see the note at the end of design section 11.3). A missing directory has none, and creating the temporary
 * file then reports the error. A failed delete names the target.
 */
async function removeLeftoverTemporaryFiles(target: string): Promise<void> {
  const directory = dirname(target);
  const prefix = `.${basename(target)}.agw-`;
  let entries: Dirent[];
  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch (error) {
    if (errorCode(error) === "ENOENT") return;
    throw error;
  }
  for (const entry of entries) {
    // 前缀之后是 32 位十六进制与 `.tmp`。The prefix is followed by 32 hex digits and `.tmp`.
    if (
      !entry.isDirectory() &&
      entry.name.length === prefix.length + 36 &&
      identityKey(entry.name.slice(0, prefix.length)) === identityKey(prefix) &&
      TEMPORARY_FILE_PATTERN.test(entry.name)
    ) {
      try {
        await rm(join(directory, entry.name), { force: true });
      } catch (error) {
        throw targetError(error, target);
      }
    }
  }
}

/** 创建临时文件，失败时错误中的路径为目标文件。Creates the temporary file; a failure names the target. */
async function createTemporaryFile(temporary: string, target: string): Promise<FileHandle> {
  try {
    return await open(temporary, "wx");
  } catch (error) {
    throw targetError(error, target);
  }
}

/** 不替换已有文件的提交：`link` 在目标已存在时原子地失败（EEXIST）。A commit that never replaces: `link` fails atomically with EEXIST when the target exists. */
async function linkWithoutReplacing(temporary: string, target: string): Promise<void> {
  try {
    await link(temporary, target);
  } catch (error) {
    if (lacksHardLinks(error)) {
      throw new ExtensionOperationError(
        "ERR_UNSUPPORTED_OPERATION",
        "This file system cannot create files atomically.",
      );
    }
    throw error;
  }
  // 提交已经完成，临时文件删除失败只记录，写入仍然成功。The commit is done; a failed delete is only logged and the write still succeeds.
  await cleanUp(`Unable to delete the temporary file ${temporary} after the commit.`, () =>
    unlink(temporary),
  );
}

/**
 * 原子提交（9.7 节）：内容先完整写入同一目录中的临时文件并刷新到磁盘，再用一次 rename 或 link 提交；
 * 提交之前的任何失败都删除临时文件，目标文件保持原样。
 * Atomic commit (section 9.7): the content is written to a temporary file in the same directory and flushed, then committed
 * with one rename or link; any failure before that removes the temporary file and leaves the target unchanged.
 * 返回值说明这次提交是否替换了已有文件。The result tells whether the commit replaced an existing file.
 */
async function commitFile(
  target: string,
  data: Uint8Array,
  options: CommitOptions,
): Promise<boolean> {
  const verified =
    options.expectedSha256 === undefined
      ? null
      : await verifyContent(target, options.expectedSha256);
  // 有写入前提时文件已经存在，父目录也存在。With a precondition the file exists, and so do its parents.
  if (options.createParents && !verified) await mkdir(dirname(target), { recursive: true });
  // 写入临时文件之前的检查，提交时的 link 仍然处理检查之后才出现的目标。
  // Checks before the temporary write; the commit's link still handles a target that appears after them.
  const existing = verified ?? (await statIfExists(target));
  if (existing && !options.overwrite) throw openError("EEXIST", "file already exists", target);
  if (existing?.isDirectory())
    throw openError("EISDIR", "illegal operation on a directory", target);
  // 只读的目标不替换：rename 不检查目标的写权限，因此创建临时文件之前确认（EACCES）。
  // A read-only target is not replaced: rename ignores the target's write permission, so it is checked first (EACCES).
  if (existing) await access(target, constants.W_OK);
  await removeLeftoverTemporaryFiles(target);

  const temporary = join(
    dirname(target),
    `.${basename(target)}.agw-${randomBytes(16).toString("hex")}.tmp`,
  );
  const handle = await createTemporaryFile(temporary, target);
  try {
    await closingAfter(handle, temporary, async () => {
      await handle.writeFile(data);
      await handle.sync();
      if (existing) await handle.chmod(Number(existing.mode) & 0o7777);
    });
    if (verified) {
      let current: BigIntStats;
      try {
        current = await lstat(target, { bigint: true });
      } catch (error) {
        if (errorCode(error) === "ENOENT")
          throw fileChanged("The file was deleted during the write.");
        throw error;
      }
      if (!sameFile(verified, current)) throw fileChanged("The file was changed during the write.");
    }
    // 复核与提交之间没有其他异步操作。Nothing asynchronous runs between the re-check and the commit.
    if (options.overwrite) await rename(temporary, target);
    else await linkWithoutReplacing(temporary, target);
  } catch (error) {
    await cleanUp(`Unable to delete the temporary file ${temporary}.`, () =>
      rm(temporary, { force: true }),
    );
    throw error;
  }
  return existing !== null;
}

/**
 * 主进程的本地文件访问：先判断路径与权限，判断、创建父目录与实际读写在同一次持锁期间完成。
 * 写入相关的操作在 extension 自己的写锁内，再按文件身份取得所有 extension 共用的锁，同一个文件的写入依次执行。
 * Main-process local file access: the path check, parent creation and the actual IO happen within one lock hold.
 * Write operations run under the extension's own write lock and then take the per-file-identity lock shared by every extension,
 * so writes to one file run one at a time.
 */
export class ExtensionFileSystem {
  private readonly locks = new Map<string, ReadWriteLock>();
  private readonly identityLocks = new FileIdentityLocks();

  public constructor(
    private readonly grants: ExtensionAccessGrants,
    private readonly replacedFiles: ReplacedFileRegistry,
  ) {}

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
        const preconditionError = writePreconditionError(request);
        if (preconditionError) return Promise.reject(preconditionError);
        return lock.write(async () => {
          // 写入最终目标，路径上的符号链接保持不变。Writes the final target; symbolic links on the path stay unchanged.
          const target = await judgementPath(path);
          await this.check(context, target, "write");
          return this.identityLocks.run([target], async () => {
            const replaced = await commitFile(target, request.data, {
              overwrite: request.overwrite,
              createParents: request.createParents,
              expectedSha256: request.expectedSha256,
            });
            // 在 rename 完成后的同一轮微任务中记录，监听对事件的 lstat 通常在这之后返回；lstat 更早返回的少数情况下，
            // 这次替换可能报告为 created。
            // Recorded in the microtasks right after the rename completes; a watcher's lstat for the event usually returns
            // later, and in the narrow race where it returns first this replacement may be reported as created.
            if (replaced) this.replacedFiles.record(target);
            return null;
          });
        });
      }
      case "createDirectory": {
        const path = localPathFromUri(request.uri);
        return lock.write(async () => {
          await this.check(context, await judgementPath(path), "write");
          // 创建目录是目录项操作，按目录项自身的路径加锁。Creating a directory is an entry operation, locked by the entry's own path.
          return this.identityLocks.run([await entryJudgementPath(path)], async () => {
            await mkdir(path, { recursive: request.recursive });
            return null;
          });
        });
      }
      case "delete": {
        const path = localPathFromUri(request.uri);
        return lock.write(async () => {
          const judged = await entryJudgementPath(path);
          await this.check(context, judged, "write");
          return this.identityLocks.run([judged], async () => {
            const info = await lstat(path);
            if (request.recursive) await rm(path, { recursive: true });
            else if (info.isDirectory()) await rmdir(path);
            else await unlink(path);
            return null;
          });
        });
      }
      case "rename": {
        const from = localPathFromUri(request.from);
        const to = localPathFromUri(request.to);
        return lock.write(async () => {
          const judgedFrom = await entryJudgementPath(from);
          await this.check(context, judgedFrom, "write");
          const judgedTo = await entryJudgementPath(to);
          await this.check(context, judgedTo, "write");
          return this.identityLocks.run([judgedFrom, judgedTo], async () => {
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
