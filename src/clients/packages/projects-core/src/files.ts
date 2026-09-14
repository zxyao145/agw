/**
 * File API client for file system operations
 * Uses the backend API for file browsing and reading
 */

import { ApiError, apiDelete, apiGet, apiPost, type AgwApiClient } from "@agw/api";

type ProjectFilesApiClient = Pick<AgwApiClient, "apiGet" | "apiPost" | "apiDelete">;

const browserClient: ProjectFilesApiClient = { apiGet, apiPost, apiDelete };

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

  constructor(message: string, status?: number, statusText?: string) {
    super(message);
    this.name = "FileApiError";
    this.status = status;
    this.statusText = statusText;
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
    return new FileApiError(message, err.status, err.statusText);
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
 */
export async function deleteFile(
  projectId: string,
  path: string,
  client: ProjectFilesApiClient = browserClient,
  directoryId?: string | null,
): Promise<{ success: boolean; message: string }> {
  try {
    return (await client.apiDelete("/api/files/delete", {
      params: { query: { projectId, path, directoryId: directoryId || undefined } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, "Failed to delete");
  }
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
  ): Promise<{ success: boolean; message: string }>;
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
    deleteFile: (projectId, path, directoryId) => deleteFile(projectId, path, client, directoryId),
    resetFile: (projectId, path, directoryId) => resetFile(projectId, path, client, directoryId),
    setFileStaged: (projectId, path, staged, directoryId) =>
      setFileStaged(projectId, path, staged, client, directoryId),
    searchFiles: (projectId, path, keyword, recursive, directoryId) =>
      searchFiles(projectId, path, keyword, recursive, client, directoryId),
    searchFilesInDirectories: (projectId, path, keyword, recursive, directoryIds) =>
      searchFilesInDirectories(projectId, path, keyword, recursive, directoryIds, client),
  };
}
