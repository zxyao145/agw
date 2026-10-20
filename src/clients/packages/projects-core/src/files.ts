/**
 * File API client for file system operations
 * Uses the backend API for file browsing and reading
 */

import {
  ApiError,
  apiDelete,
  apiDownload,
  apiGet,
  apiPost,
  apiPut,
  type AgwApiClient,
} from "@agw/api";

type ProjectFilesApiClient = Pick<
  AgwApiClient,
  "apiGet" | "apiPost" | "apiPut" | "apiDelete" | "apiDownload"
>;

const browserClient: ProjectFilesApiClient = { apiGet, apiPost, apiPut, apiDelete, apiDownload };

export type FileGitStatus = "added" | "modified" | "deleted" | "untracked";
export type GitDiffScope = "staged" | "unstaged";

export interface FileItem {
  directoryId?: string | null;
  name: string;
  path: string;
  type: "file" | "directory";
  size?: number;
  modifiedTime?: string;
  gitStatus?: FileGitStatus | null;
  gitStagedStatus?: FileGitStatus | null;
  gitUnstagedStatus?: FileGitStatus | null;
  children?: FileItem[]; // For tree structure support (used in recursive mode)
}

export interface ListFilesResponse {
  items: FileItem[];
}

export class FileApiError extends Error {
  public readonly status?: number;
  public readonly statusText?: string;
  /**
   * The Server ApiResult error code (for example 4040001 or 4090023), or null when the failure has
   * no ApiResult body, such as a transport error.
   * Server ApiResult 的错误码（例如 4040001、4090023）；失败没有 ApiResult 响应体（例如传输错误）时为 null。
   */
  public readonly code: number | null;

  constructor(message: string, status?: number, statusText?: string, code: number | null = null) {
    super(message);
    this.name = "FileApiError";
    this.status = status;
    this.statusText = statusText;
    this.code = code;
  }
}

function toFileApiError(err: unknown, fallbackMessage: string): FileApiError {
  if (err instanceof FileApiError) {
    return err;
  }

  if (err instanceof ApiError) {
    const body = err.body;
    const message =
      typeof body === "object" && body !== null
        ? ("detail" in body && typeof body.detail === "string" && body.detail) ||
          ("error" in body && typeof body.error === "string" && body.error) ||
          ("title" in body && typeof body.title === "string" && body.title) ||
          fallbackMessage
        : fallbackMessage;
    const code =
      typeof body === "object" && body !== null && "code" in body && typeof body.code === "number"
        ? body.code
        : null;
    return new FileApiError(message, err.status, err.statusText, code);
  }

  return new FileApiError(`Network error: ${(err as Error).message}`);
}

/**
 * List files and directories at the specified path
 * @param path - Directory path to list
 * @param diff - If true, only return modified files (requires git)
 * @param recursive - If true with onlyModified, return all changed files recursively (not just direct children)
 */
export async function listFiles(
  projectId: string,
  path: string,
  diff: boolean = false,
  recursive: boolean = false,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<ListFilesResponse> {
  try {
    return (await client.apiGet("/api/files/list", {
      params: {
        query: {
          projectId,
          directoryId: directoryId || undefined,
          path: path || undefined,
          diff: diff || undefined,
          recursive: recursive || undefined,
        },
      },
    })) as ListFilesResponse;
  } catch (err) {
    throw toFileApiError(err, "Failed to load directory");
  }
}

/**
 * Read file content from the specified path
 */
export async function readFile(
  projectId: string,
  path: string,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<string> {
  try {
    return (await client.apiGet("/api/files/read", {
      params: { query: { projectId, path, directoryId: directoryId || undefined } },
    })) as string;
  } catch (err) {
    throw toFileApiError(err, "Failed to read file");
  }
}

export interface GitDiffResponse {
  diff: string;
  unchanged: boolean;
  message?: string;
  originalContent?: string;
}

/**
 * Get git diff for the specified file
 */
export async function getFileDiff(
  projectId: string,
  path: string,
  scope?: GitDiffScope,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<GitDiffResponse> {
  try {
    return (await client.apiGet("/api/files/diff", {
      params: { query: { projectId, path, scope, directoryId: directoryId || undefined } },
    })) as GitDiffResponse;
  } catch (err) {
    throw toFileApiError(err, "Failed to get diff");
  }
}

/**
 * Delete a file or directory
 * @param recursive - When false, a non-empty directory is kept and the Server returns
 * DirectoryNotEmpty (4090023); omitted, the parameter is not sent and the Server deletes recursively.
 * 为 false 时非空目录保持不变，Server 返回 DirectoryNotEmpty（4090023）；省略时不发送，Server 递归删除。
 */
export async function deleteFile(
  projectId: string,
  path: string,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
  recursive?: boolean,
): Promise<{ success: boolean; message: string }> {
  try {
    return (await client.apiDelete("/api/files/delete", {
      params: { query: { projectId, path, directoryId: directoryId || undefined, recursive } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, "Failed to delete");
  }
}

/**
 * Get the entry of a file or directory; an empty path is the directory root.
 * 获取文件或目录的条目；空路径表示目录根。
 */
export async function statFile(
  projectId: string,
  path: string,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<FileItem> {
  try {
    return (await client.apiGet("/api/files/stat", {
      params: { query: { projectId, path, directoryId: directoryId || undefined } },
    })) as FileItem;
  } catch (err) {
    throw toFileApiError(err, "Failed to get file information");
  }
}

export interface DownloadFileOptions {
  signal?: AbortSignal;
}

/**
 * Download the content of a file (at most 100 MiB).
 * 下载文件内容（不超过 100 MiB）。
 */
export async function downloadFile(
  projectId: string,
  path: string,
  options: DownloadFileOptions = {},
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<Uint8Array> {
  try {
    return await client.apiDownload("/api/files/download", {
      params: { query: { projectId, path, directoryId: directoryId || undefined } },
      signal: options.signal,
    });
  } catch (err) {
    throw toFileApiError(err, "Failed to download file");
  }
}

export interface WriteFileOptions {
  /** Omitted, the parameter is not sent and the Server does not overwrite. 省略时不发送，Server 不覆盖。 */
  overwrite?: boolean;
  /**
   * Omitted, the parameter is not sent and the Server does not create missing parents.
   * 省略时不发送，Server 不创建缺失的父目录。
   */
  createParents?: boolean;
  /**
   * SHA-256 (64 hex characters) of the content the caller last read; requires `overwrite: true`. The Server
   * rejects the write with a conflict when the file changed. Omitted, the parameter is not sent.
   * 调用方上次读取内容的 SHA-256（64 位十六进制），须与 `overwrite: true` 一起使用；文件已变化时 Server
   * 以冲突拒绝写入。省略时不发送。
   */
  expectedSha256?: string;
  signal?: AbortSignal;
}

/**
 * Write file content (at most 100 MiB) as the multipart field `content`; returns the written entry.
 * 以 multipart 字段 `content` 写入文件内容（不超过 100 MiB），返回写入后的条目。
 */
export async function writeFile(
  projectId: string,
  path: string,
  content: Uint8Array | string,
  options: WriteFileOptions = {},
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<FileItem> {
  const form = new FormData();
  form.append("content", new Blob([toBlobPart(content)]), getFileName(path));
  try {
    return (await client.apiPut("/api/files/write", {
      params: {
        query: {
          projectId,
          path,
          overwrite: options.overwrite,
          createParents: options.createParents,
          expectedSha256: options.expectedSha256,
          directoryId: directoryId || undefined,
        },
      },
      body: form,
      signal: options.signal,
    })) as FileItem;
  } catch (err) {
    throw toFileApiError(err, "Failed to write file");
  }
}

/**
 * Create a directory; returns the directory entry.
 * 创建目录，返回目录条目。
 * @param recursive - Create missing parents; omitted, the parameter is not sent and the Server does not.
 * 创建缺失的父目录；省略时不发送，Server 不创建。
 */
export async function createDirectory(
  projectId: string,
  path: string,
  recursive?: boolean,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<FileItem> {
  try {
    return (await client.apiPost("/api/files/create-directory", {
      params: { query: { projectId, path, recursive, directoryId: directoryId || undefined } },
    })) as FileItem;
  } catch (err) {
    throw toFileApiError(err, "Failed to create directory");
  }
}

/**
 * Move or rename a file or directory inside one Project directory; returns the destination entry.
 * 在同一个 Project 目录内移动或重命名文件、目录，返回目标的条目。
 * @param overwrite - Replace an existing destination file; omitted, the parameter is not sent and the
 * Server does not overwrite. 替换已存在的目标文件；省略时不发送，Server 不覆盖。
 */
export async function moveFile(
  projectId: string,
  sourcePath: string,
  destinationPath: string,
  overwrite?: boolean,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<FileItem> {
  try {
    return (await client.apiPost("/api/files/move", {
      params: {
        query: {
          projectId,
          sourcePath,
          destinationPath,
          overwrite,
          directoryId: directoryId || undefined,
        },
      },
    })) as FileItem;
  } catch (err) {
    throw toFileApiError(err, "Failed to move");
  }
}

/**
 * Blob parts must be backed by an ArrayBuffer; a view over shared memory is copied first.
 * Blob 的内容必须由 ArrayBuffer 承载；共享内存上的视图先复制一份。
 */
function toBlobPart(content: Uint8Array | string): BlobPart {
  if (typeof content === "string") return content;
  return content.buffer instanceof ArrayBuffer
    ? (content as Uint8Array<ArrayBuffer>)
    : new Uint8Array(content);
}

function getFileName(path: string): string {
  const name = path.split("/").filter(Boolean).pop();
  return name || "content";
}

/**
 * Reset file to git HEAD (discard modifications)
 */
export async function resetFile(
  projectId: string,
  path: string,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<{ success: boolean; message: string }> {
  try {
    return (await client.apiPost("/api/files/reset", {
      params: { query: { projectId, path, directoryId: directoryId || undefined } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, "Failed to reset");
  }
}

/**
 * Move file or directory changes between the working tree and Git index.
 */
export async function setFileStaged(
  projectId: string,
  path: string,
  staged: boolean,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<{ success: boolean; message: string }> {
  try {
    const endpoint = staged ? "/api/files/stage" : "/api/files/unstage";
    return (await client.apiPost(endpoint, {
      params: { query: { projectId, path, directoryId: directoryId || undefined } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, staged ? "Failed to stage changes" : "Failed to unstage changes");
  }
}

export interface FileSearchResult {
  directoryId?: string | null;
  fullPath: string;
  relativePath: string;
  type: "file" | "directory";
}

export interface SearchFilesResponse {
  results: FileSearchResult[];
}

/**
 * Search for files and directories by keyword
 * @param path - Root directory to search in
 * @param keyword - Search keyword (matches flattened relative paths)
 * @param recursive - If true, search subdirectories (default: true)
 */
export async function searchFiles(
  projectId: string,
  path: string,
  keyword: string,
  recursive: boolean = true,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<SearchFilesResponse> {
  try {
    return (await client.apiGet("/api/files/search", {
      params: {
        query: {
          projectId,
          directoryId: directoryId || undefined,
          path: path || undefined,
          keyword,
          recursive: recursive || undefined,
        },
      },
    })) as SearchFilesResponse;
  } catch (err) {
    throw toFileApiError(err, "Failed to search files");
  }
}

export async function searchFilesInDirectories(
  projectId: string,
  path: string,
  keyword: string,
  recursive: boolean,
  directoryIds: readonly (string | null)[],
  client: ProjectFilesApiClient = browserClient,
): Promise<SearchFilesResponse> {
  const responses = await Promise.allSettled(
    directoryIds.map((directoryId) =>
      searchFiles(projectId, path, keyword, recursive, client, directoryId),
    ),
  );
  return {
    results: responses.flatMap((response) =>
      response.status === "fulfilled" ? response.value.results : [],
    ),
  };
}

export type ProjectFilesService = {
  listFiles(
    projectId: string,
    path: string,
    diff?: boolean,
    recursive?: boolean,
    directoryId?: string | null,
  ): Promise<ListFilesResponse>;
  readFile(projectId: string, path: string, directoryId?: string | null): Promise<string>;
  getFileDiff(
    projectId: string,
    path: string,
    scope?: GitDiffScope,
    directoryId?: string | null,
  ): Promise<GitDiffResponse>;
  deleteFile(
    projectId: string,
    path: string,
    directoryId?: string | null,
    recursive?: boolean,
  ): Promise<{ success: boolean; message: string }>;
  statFile(projectId: string, path: string, directoryId?: string | null): Promise<FileItem>;
  downloadFile(
    projectId: string,
    path: string,
    options?: DownloadFileOptions,
    directoryId?: string | null,
  ): Promise<Uint8Array>;
  writeFile(
    projectId: string,
    path: string,
    content: Uint8Array | string,
    options?: WriteFileOptions,
    directoryId?: string | null,
  ): Promise<FileItem>;
  createDirectory(
    projectId: string,
    path: string,
    recursive?: boolean,
    directoryId?: string | null,
  ): Promise<FileItem>;
  moveFile(
    projectId: string,
    sourcePath: string,
    destinationPath: string,
    overwrite?: boolean,
    directoryId?: string | null,
  ): Promise<FileItem>;
  resetFile(
    projectId: string,
    path: string,
    directoryId?: string | null,
  ): Promise<{ success: boolean; message: string }>;
  setFileStaged(
    projectId: string,
    path: string,
    staged: boolean,
    directoryId?: string | null,
  ): Promise<{ success: boolean; message: string }>;
  searchFiles(
    projectId: string,
    path: string,
    keyword: string,
    recursive?: boolean,
    directoryId?: string | null,
  ): Promise<SearchFilesResponse>;
  searchFilesInDirectories(
    projectId: string,
    path: string,
    keyword: string,
    recursive: boolean,
    directoryIds: readonly (string | null)[],
  ): Promise<SearchFilesResponse>;
};

export function createProjectFilesService(client: ProjectFilesApiClient): ProjectFilesService {
  return {
    listFiles: (projectId, path, diff, recursive, directoryId) =>
      listFiles(projectId, path, diff, recursive, client, directoryId),
    readFile: (projectId, path, directoryId) => readFile(projectId, path, client, directoryId),
    getFileDiff: (projectId, path, scope, directoryId) =>
      getFileDiff(projectId, path, scope, client, directoryId),
    deleteFile: (projectId, path, directoryId, recursive) =>
      deleteFile(projectId, path, client, directoryId, recursive),
    statFile: (projectId, path, directoryId) => statFile(projectId, path, client, directoryId),
    downloadFile: (projectId, path, options, directoryId) =>
      downloadFile(projectId, path, options, client, directoryId),
    writeFile: (projectId, path, content, options, directoryId) =>
      writeFile(projectId, path, content, options, client, directoryId),
    createDirectory: (projectId, path, recursive, directoryId) =>
      createDirectory(projectId, path, recursive, client, directoryId),
    moveFile: (projectId, sourcePath, destinationPath, overwrite, directoryId) =>
      moveFile(projectId, sourcePath, destinationPath, overwrite, client, directoryId),
    resetFile: (projectId, path, directoryId) => resetFile(projectId, path, client, directoryId),
    setFileStaged: (projectId, path, staged, directoryId) =>
      setFileStaged(projectId, path, staged, client, directoryId),
    searchFiles: (projectId, path, keyword, recursive, directoryId) =>
      searchFiles(projectId, path, keyword, recursive, client, directoryId),
    searchFilesInDirectories: (projectId, path, keyword, recursive, directoryIds) =>
      searchFilesInDirectories(projectId, path, keyword, recursive, directoryIds, client),
  };
}
