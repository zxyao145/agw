/**
 * File API client for file system operations
 * Uses the backend API for file browsing and reading
 */

import { ApiError, apiDelete, apiGet, apiPost } from "./client";

export interface FileItem {
  name: string;
  path: string;
  type: "file" | "directory";
  size?: number;
  modifiedTime?: string;
  gitStatus?: "added" | "modified" | "deleted" | "untracked";
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
      typeof body === "object" &&
      body !== null &&
      "error" in body &&
      typeof body.error === "string"
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
  path: string,
  diff: boolean = false,
  recursive: boolean = false,
): Promise<ListFilesResponse> {
  try {
    return (await apiGet("/api/files/list", {
      params: { query: { path, diff: diff || undefined, recursive: recursive || undefined } },
    })) as ListFilesResponse;
  } catch (err) {
    throw toFileApiError(err, "Failed to load directory");
  }
}

/**
 * Read file content from the specified path
 */
export async function readFile(path: string): Promise<string> {
  try {
    return (await apiGet("/api/files/read", { params: { query: { path } } })) as string;
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
export async function getFileDiff(path: string): Promise<GitDiffResponse> {
  try {
    return (await apiGet("/api/files/diff", { params: { query: { path } } })) as GitDiffResponse;
  } catch (err) {
    throw toFileApiError(err, "Failed to get diff");
  }
}

/**
 * Delete a file or directory
 */
export async function deleteFile(path: string): Promise<{ success: boolean; message: string }> {
  try {
    return (await apiDelete("/api/files/delete", {
      params: { query: { path } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, "Failed to delete");
  }
}

/**
 * Reset file to git HEAD (discard modifications)
 */
export async function resetFile(path: string): Promise<{ success: boolean; message: string }> {
  try {
    return (await apiPost("/api/files/reset", {
      params: { query: { path } },
    })) as { success: boolean; message: string };
  } catch (err) {
    throw toFileApiError(err, "Failed to reset");
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
  path: string,
  keyword: string,
  recursive: boolean = true,
): Promise<SearchFilesResponse> {
  try {
    return (await apiGet("/api/files/search", {
      params: { query: { path, keyword, recursive: recursive || undefined } },
    })) as SearchFilesResponse;
  } catch (err) {
    throw toFileApiError(err, "Failed to search files");
  }
}
