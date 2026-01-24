/**
 * File API client for file system operations
 * Uses the backend API for file browsing and reading
 */

export interface FileItem {
  name: string;
  path: string;
  type: "file" | "directory";
  size?: number;
  modifiedTime?: string;
  gitStatus?: "added" | "modified" | "deleted" | "untracked";
  children?: FileItem[];  // For tree structure support (used in recursive mode)
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

/**
 * List files and directories at the specified path
 * @param path - Directory path to list
 * @param diff - If true, only return modified files (requires git)
 * @param recursive - If true with onlyModified, return all changed files recursively (not just direct children)
 */
export async function listFiles(path: string, diff: boolean = false, recursive: boolean = false): Promise<ListFilesResponse> {
  try {
    const params = new URLSearchParams({ path });
    if (diff) {
      params.append('diff', 'true');
    }
    if (recursive) {
      params.append('recursive', 'true');
    }

    const response = await fetch(
      `/api/files/list?${params.toString()}`
    );

    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new FileApiError(
        errorData.error || `Failed to load directory: ${response.statusText}`,
        response.status,
        response.statusText
      );
    }

    return await response.json();
  } catch (err) {
    if (err instanceof FileApiError) {
      throw err;
    }
    throw new FileApiError(
      `Network error: ${(err as Error).message}`
    );
  }
}

/**
 * Read file content from the specified path
 */
export async function readFile(path: string): Promise<string> {
  try {
    const response = await fetch(
      `/api/files/read?path=${encodeURIComponent(path)}`
    );

    if (!response.ok) {
      throw new FileApiError(
        `Failed to read file: ${response.statusText}`,
        response.status,
        response.statusText
      );
    }

    return await response.text();
  } catch (err) {
    if (err instanceof FileApiError) {
      throw err;
    }
    throw new FileApiError(
      `Network error: ${(err as Error).message}`
    );
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
    const response = await fetch(
      `/api/files/diff?path=${encodeURIComponent(path)}`
    );

    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new FileApiError(
        errorData.error || `Failed to get diff: ${response.statusText}`,
        response.status,
        response.statusText
      );
    }

    return await response.json();
  } catch (err) {
    if (err instanceof FileApiError) {
      throw err;
    }
    throw new FileApiError(
      `Network error: ${(err as Error).message}`
    );
  }
}

/**
 * Delete a file or directory
 */
export async function deleteFile(path: string): Promise<{ success: boolean; message: string }> {
  try {
    const response = await fetch(
      `/api/files/delete?path=${encodeURIComponent(path)}`,
      {
        method: 'DELETE',
      }
    );

    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new FileApiError(
        errorData.error || `Failed to delete: ${response.statusText}`,
        response.status,
        response.statusText
      );
    }

    return await response.json();
  } catch (err) {
    if (err instanceof FileApiError) {
      throw err;
    }
    throw new FileApiError(
      `Network error: ${(err as Error).message}`
    );
  }
}

/**
 * Reset file to git HEAD (discard modifications)
 */
export async function resetFile(path: string): Promise<{ success: boolean; message: string }> {
  try {
    const response = await fetch(
      `/api/files/reset?path=${encodeURIComponent(path)}`,
      {
        method: 'POST',
      }
    );

    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new FileApiError(
        errorData.error || `Failed to reset: ${response.statusText}`,
        response.status,
        response.statusText
      );
    }

    return await response.json();
  } catch (err) {
    if (err instanceof FileApiError) {
      throw err;
    }
    throw new FileApiError(
      `Network error: ${(err as Error).message}`
    );
  }
}
