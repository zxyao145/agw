import type { AgwApiClient } from "./agw-api-client";
import type {
  AgwFileActionResponse,
  AgwFileListResponse,
  AgwGitDiffResponse,
} from "./agw-api-types";

export function listFiles(
  apiClient: AgwApiClient,
  projectId: string,
  path: string,
  diff = false,
  recursive = false
): Promise<AgwFileListResponse> {
  return apiClient.getJson<AgwFileListResponse>("/api/files/list", {
    query: { diff, path, projectId, recursive },
  });
}

export function readFile(
  apiClient: AgwApiClient,
  projectId: string,
  path: string
): Promise<string> {
  return apiClient.getText("/api/files/read", {
    query: { path, projectId },
  });
}

export function getFileDiff(
  apiClient: AgwApiClient,
  projectId: string,
  path: string
): Promise<AgwGitDiffResponse> {
  return apiClient.getJson<AgwGitDiffResponse>("/api/files/diff", {
    query: { path, projectId },
  });
}

export function deleteFile(
  apiClient: AgwApiClient,
  projectId: string,
  path: string
): Promise<AgwFileActionResponse> {
  return apiClient.deleteJson<AgwFileActionResponse>("/api/files/delete", {
    query: { path, projectId },
  });
}

export function resetFile(
  apiClient: AgwApiClient,
  projectId: string,
  path: string
): Promise<AgwFileActionResponse> {
  return apiClient.postJson<AgwFileActionResponse>(
    "/api/files/reset",
    undefined,
    { query: { path, projectId } }
  );
}
