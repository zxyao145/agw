import type { AgwApiClient } from "./agw-api-client";
import type {
  AgwFileActionResponse,
  AgwFileListResponse,
  AgwGitDiffResponse,
} from "./agw-api-types";

export function listFiles(
  apiClient: AgwApiClient,
  path: string,
  diff = false,
  recursive = false
): Promise<AgwFileListResponse> {
  return apiClient.getJson<AgwFileListResponse>("/api/files/list", {
    query: { diff, path, recursive },
  });
}

export function readFile(
  apiClient: AgwApiClient,
  path: string
): Promise<string> {
  return apiClient.getText("/api/files/read", {
    query: { path },
  });
}

export function getFileDiff(
  apiClient: AgwApiClient,
  path: string
): Promise<AgwGitDiffResponse> {
  return apiClient.getJson<AgwGitDiffResponse>("/api/files/diff", {
    query: { path },
  });
}

export function deleteFile(
  apiClient: AgwApiClient,
  path: string
): Promise<AgwFileActionResponse> {
  return apiClient.deleteJson<AgwFileActionResponse>("/api/files/delete", {
    query: { path },
  });
}

export function resetFile(
  apiClient: AgwApiClient,
  path: string
): Promise<AgwFileActionResponse> {
  return apiClient.postJson<AgwFileActionResponse>(
    "/api/files/reset",
    undefined,
    { query: { path } }
  );
}
