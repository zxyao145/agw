import {
  createDirectory,
  deleteFile,
  downloadFile,
  FileApiError,
  listFiles,
  moveFile,
  statFile,
  writeFile,
} from "@agw/projects-core";
import {
  createExtensionError,
  type DirectoryEntry,
  ExtensionError,
  type ExtensionPermission,
  type FileStat,
} from "@agw/extension-sdk";
import type { ContextService } from "./context-service";
import { connectionChangedError } from "./context-service";

export const CLOUD_SCHEME = "agw-cloud:";
export const PRIMARY_DIRECTORY_KEY = "workspace";
export const MAX_CLOUD_TRANSFER_BYTES = 100 * 1024 * 1024;

/** `agw-cloud://<serverId>/<projectId>/<directoryKey>/<path>`。 */
export type CloudLocation = {
  serverId: string;
  projectId: string;
  /** null 表示 Project 主目录。null means the Project's primary directory. */
  directoryId: string | null;
  path: string;
};

export function parseCloudUri(uri: string): CloudLocation {
  let url: URL;
  try {
    url = new URL(uri);
  } catch {
    throw createExtensionError("ERR_INVALID_URI", `Invalid URI: ${uri}`);
  }
  const segments = url.pathname.split("/").slice(1);
  if (
    url.protocol !== CLOUD_SCHEME ||
    url.host === "" ||
    segments.length < 2 ||
    !segments[0] ||
    !segments[1]
  ) {
    throw createExtensionError("ERR_INVALID_URI", `Invalid cloud file URI: ${uri}`);
  }
  let path: string;
  try {
    path = segments
      .slice(2)
      .map((segment) => decodeURIComponent(segment))
      .filter((segment) => segment.length > 0)
      .join("/");
  } catch {
    throw createExtensionError("ERR_INVALID_URI", `Invalid cloud file URI: ${uri}`);
  }
  return {
    serverId: decodeURIComponent(url.host),
    projectId: decodeURIComponent(segments[0]),
    directoryId: segments[1] === PRIMARY_DIRECTORY_KEY ? null : decodeURIComponent(segments[1]),
    path,
  };
}

export function formatCloudUri(location: CloudLocation): string {
  const directoryKey = location.directoryId ?? PRIMARY_DIRECTORY_KEY;
  const path = location.path
    .split("/")
    .filter((segment) => segment.length > 0)
    .map((segment) => encodeURIComponent(segment))
    .join("/");
  return `agw-cloud://${encodeURIComponent(location.serverId)}/${encodeURIComponent(location.projectId)}/${encodeURIComponent(directoryKey)}/${path}`;
}

/** 规范化 URI，用于授权比较。Normalizes a URI for grant comparison. */
function normalizedUri(uri: string): string {
  return formatCloudUri(parseCloudUri(uri));
}

type CloudGrant = { extensionId: string; uri: string; directory: boolean; connectionKey: string };

/**
 * 文件浏览器右键菜单授予的云端读取授权，记录授予时的连接标识。
 * Cloud read grants from the file explorer context menu, recorded with the connection key at grant time.
 */
export class CloudAccessGrants {
  private grants: CloudGrant[] = [];

  public grant(extensionId: string, uri: string, directory: boolean, connectionKey: string): void {
    this.grants.push({ extensionId, uri: normalizedUri(uri), directory, connectionKey });
  }

  public allows(extensionId: string, uri: string, connectionKey: string | null): boolean {
    if (connectionKey === null) return false;
    const target = normalizedUri(uri);
    return this.grants.some(
      (grant) =>
        grant.extensionId === extensionId &&
        grant.connectionKey === connectionKey &&
        (grant.uri === target ||
          (grant.directory &&
            target.startsWith(grant.uri.endsWith("/") ? grant.uri : `${grant.uri}/`))),
    );
  }

  public release(extensionId: string): void {
    this.grants = this.grants.filter((grant) => grant.extensionId !== extensionId);
  }

  public clear(): void {
    this.grants = [];
  }
}

/** Server 错误码转换为 Node.js 风格的错误码。Converts Server error codes into Node.js-style codes. */
export function toCloudError(error: unknown): Error {
  if (error instanceof ExtensionError) return error;
  if (!(error instanceof FileApiError))
    return error instanceof Error ? error : new Error(String(error));
  const code =
    error.code === 4040001 || error.code === 4040007 || error.code === 4040002
      ? "ENOENT"
      : error.code === 4000040
        ? "EEXIST"
        : error.code === 4030005 || error.code === 4030007
          ? "EACCES"
          : error.code === 4090023
            ? "ENOTEMPTY"
            : error.code === 4090024
              ? "ERR_FILE_CHANGED"
              : error.code === 4130001
                ? "ERR_FILE_TOO_LARGE"
                : "ERR_SERVER";
  return createExtensionError(code, error.message);
}

export type CloudAccess = {
  extensionId: string;
  permissions: readonly ExtensionPermission[];
};

/** 写入成功的文件；`directoryId` 为 null 表示 Project 主目录。A written file; a null `directoryId` is the primary directory. */
export type FileWriteLocation = { projectId: string; directoryId: string | null; path: string };

/** 宿主中的云端文件系统，经 `@agw/projects-core` 访问 `/api/files`。The host's cloud file system over `/api/files`. */
export class CloudFileSystem {
  public constructor(
    private readonly context: ContextService,
    private readonly grants: CloudAccessGrants,
    /** `writeFile` 成功后调用。Called after a successful `writeFile`. */
    private readonly onFileWritten: (location: FileWriteLocation) => void,
  ) {}

  public readFile(access: CloudAccess, uri: string, signal?: AbortSignal): Promise<Uint8Array> {
    return this.run(access, uri, "read", signal, (location, requestSignal) =>
      downloadFile(
        location.projectId,
        location.path,
        { signal: requestSignal },
        undefined,
        location.directoryId,
      ),
    );
  }

  public async writeFile(
    access: CloudAccess,
    uri: string,
    data: Uint8Array,
    options: { overwrite: boolean; createParents: boolean; expectedSha256?: string },
    signal?: AbortSignal,
  ): Promise<null> {
    if (data.byteLength > MAX_CLOUD_TRANSFER_BYTES) {
      throw createExtensionError("ERR_FILE_TOO_LARGE", "A single write cannot exceed 100 MB.");
    }
    const location = await this.run(access, uri, "write", signal, async (target, requestSignal) => {
      await writeFile(
        target.projectId,
        target.path,
        data,
        { ...options, signal: requestSignal },
        undefined,
        target.directoryId,
      );
      return target;
    });
    // 写入已经提交：监听函数在之后的微任务中执行，它的错误不改变写入结果。
    // The write is committed: the listener runs in a later microtask, so its error leaves the result unchanged.
    queueMicrotask(() =>
      this.onFileWritten({
        projectId: location.projectId,
        directoryId: location.directoryId,
        path: location.path,
      }),
    );
    return null;
  }

  public readDirectory(access: CloudAccess, uri: string): Promise<DirectoryEntry[]> {
    return this.run(access, uri, "read", undefined, async (location) => {
      const listing = await listFiles(
        location.projectId,
        location.path,
        false,
        false,
        undefined,
        location.directoryId,
      );
      return listing.items.map((item) => ({
        name: item.name,
        uri: formatCloudUri({ ...location, path: item.path }),
        type: item.type,
      }));
    });
  }

  public stat(access: CloudAccess, uri: string): Promise<FileStat> {
    return this.run(access, uri, "read", undefined, async (location) => {
      const item = await statFile(
        location.projectId,
        location.path,
        undefined,
        location.directoryId,
      );
      return {
        type: item.type,
        size: item.type === "file" && item.size !== undefined ? Number(item.size) : null,
        modifiedTime: item.modifiedTime ?? null,
      };
    });
  }

  public async exists(access: CloudAccess, uri: string): Promise<boolean> {
    try {
      await this.stat(access, uri);
      return true;
    } catch (error) {
      if ((error as { code?: unknown }).code === "ENOENT") return false;
      throw error;
    }
  }

  public createDirectory(access: CloudAccess, uri: string, recursive: boolean): Promise<null> {
    return this.run(access, uri, "write", undefined, async (location) => {
      await createDirectory(
        location.projectId,
        location.path,
        recursive,
        undefined,
        location.directoryId,
      );
      return null;
    });
  }

  public delete(access: CloudAccess, uri: string, recursive: boolean): Promise<null> {
    return this.run(access, uri, "write", undefined, async (location) => {
      await deleteFile(
        location.projectId,
        location.path,
        undefined,
        location.directoryId,
        recursive,
      );
      return null;
    });
  }

  public rename(access: CloudAccess, from: string, to: string, overwrite: boolean): Promise<null> {
    const source = parseCloudUri(from);
    const target = parseCloudUri(to);
    if (
      source.serverId !== target.serverId ||
      source.projectId !== target.projectId ||
      source.directoryId !== target.directoryId
    ) {
      return Promise.reject(
        createExtensionError(
          "ERR_CROSS_DEVICE",
          "Cloud files can only move within one Project directory.",
        ),
      );
    }
    this.checkAccess(access, to, "write");
    return this.run(access, from, "write", undefined, async (location) => {
      await moveFile(
        location.projectId,
        location.path,
        target.path,
        overwrite,
        undefined,
        location.directoryId,
      );
      return null;
    });
  }

  private checkAccess(access: CloudAccess, uri: string, mode: "read" | "write"): CloudLocation {
    const location = parseCloudUri(uri);
    if (location.serverId !== this.context.snapshot().server.id) throw connectionChangedError();
    const permission: ExtensionPermission = mode === "read" ? "fs.cloud.read" : "fs.cloud.write";
    if (access.permissions.includes(permission)) return location;
    if (mode === "read" && this.grants.allows(access.extensionId, uri, this.context.connectionKey))
      return location;
    throw createExtensionError(
      "ERR_PERMISSION_DENIED",
      `The extension is not allowed to ${mode} ${uri}.`,
    );
  }

  private async run<T>(
    access: CloudAccess,
    uri: string,
    mode: "read" | "write",
    signal: AbortSignal | undefined,
    operation: (location: CloudLocation, signal: AbortSignal) => Promise<T>,
  ): Promise<T> {
    const location = this.checkAccess(access, uri, mode);
    try {
      return await this.context.request(
        (requestSignal) => operation(location, requestSignal),
        signal,
      );
    } catch (error) {
      throw toCloudError(error);
    }
  }
}
