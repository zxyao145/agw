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
      typeof body === "object" && body !== null && "error" in body && typeof body.error === "string"
        ? body.error
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
): Promise<ListFilesResponse> {
  try {
    return (await client.apiGet("/api/files/list", {
      params: {
        query: {
          projectId,
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
): Promise<string> {
  try {
    return (await client.apiGet("/api/files/read", {
      params: { query: { projectId, path } },
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
): Promise<GitDiffResponse> {
  try {
    return (await client.apiGet("/api/files/diff", {
      params: { query: { projectId, path, scope } },
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
): Promise<{ success: boolean; message: string }> {
  try {
    return (await client.apiDelete("/api/files/delete", {
      params: { query: { projectId, path } },
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
): Promise<{ success: boolean; message: string }> {
  try {
    return (await client.apiPost("/api/files/reset", {
      params: { query: { projectId, path } },
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
): Promise<{ success: boolean; message: string }> {
  try {
    const endpoint = staged ? "/api/files/stage" : "/api/files/unstage";
    return (await client.apiPost(endpoint, {
      params: { query: { projectId, path } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, staged ? "Failed to stage changes" : "Failed to unstage changes");
  }
}

export interface FileSearchResult {
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
): Promise<SearchFilesResponse> {
  try {
    return (await client.apiGet("/api/files/search", {
      params: {
        query: {
          projectId,
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

export type ProjectFilesService = {
  listFiles(
    projectId: string,
    path: string,
    diff?: boolean,
    recursive?: boolean,
  ): Promise<ListFilesResponse>;
  readFile(projectId: string, path: string): Promise<string>;
  getFileDiff(projectId: string, path: string, scope?: GitDiffScope): Promise<GitDiffResponse>;
  deleteFile(projectId: string, path: string): Promise<{ success: boolean; message: string }>;
  resetFile(projectId: string, path: string): Promise<{ success: boolean; message: string }>;
  setFileStaged(
    projectId: string,
    path: string,
    staged: boolean,
  ): Promise<{ success: boolean; message: string }>;
  searchFiles(
    projectId: string,
    path: string,
    keyword: string,
    recursive?: boolean,
  ): Promise<SearchFilesResponse>;
};

export function createProjectFilesService(client: ProjectFilesApiClient): ProjectFilesService {
  return {
    listFiles: (projectId, path, diff, recursive) =>
      listFiles(projectId, path, diff, recursive, client),
    readFile: (projectId, path) => readFile(projectId, path, client),
    getFileDiff: (projectId, path, scope) => getFileDiff(projectId, path, scope, client),
    deleteFile: (projectId, path) => deleteFile(projectId, path, client),
    resetFile: (projectId, path) => resetFile(projectId, path, client),
    setFileStaged: (projectId, path, staged) => setFileStaged(projectId, path, staged, client),
    searchFiles: (projectId, path, keyword, recursive) =>
      searchFiles(projectId, path, keyword, recursive, client),
  };
}
