import type { AiMessage } from "@agw/api";
import { normalizeTokenUsage, type TokenUsage, type TokenUsageInput } from "@agw/api";

import { ApiError, type AgwApiClient } from "@agw/api";
import * as browserClient from "@agw/api";

type ProjectContextApiClient = Pick<AgwApiClient, "apiGet" | "apiPut" | "apiDelete">;

export interface ContextSummary {
  projectId: string;
  contextId: string;
  jobId?: string | null;
  title: string;
  latestStatus?: number | null;
  executionCount: number;
  messageCount: number;
  createTime: string;
  updateTime?: string | null;
  errorMessage?: string | null;
}

export interface ContextDetails extends ContextSummary {
  messages: AiMessage[];
  usage: TokenUsage;
}

export type ProjectContextSummaryResponse = {
  projectId: string;
  contextId: string;
  jobId?: string | null;
  title: string;
  latestStatus?: number | null;
  executionCount: number;
  messageCount: number;
  createTime: string;
  updateTime?: string | null;
  errorMessage?: string | null;
};

export type ProjectContextResponse = ProjectContextSummaryResponse & {
  messages?: AiMessage[] | null;
  usage?: TokenUsageInput | null;
};

function toContextSummary(context: ProjectContextSummaryResponse): ContextSummary {
  return {
    projectId: context.projectId,
    contextId: context.contextId,
    jobId: context.jobId ?? null,
    title: context.title,
    latestStatus: context.latestStatus ?? null,
    executionCount: context.executionCount,
    messageCount: context.messageCount,
    createTime: context.createTime,
    updateTime: context.updateTime ?? null,
    errorMessage: context.errorMessage ?? null,
  };
}

function shouldIncludeContext(context: ContextSummary): boolean {
  return context.messageCount > 0 || context.executionCount === 0;
}

function toContextDetails(context: ProjectContextResponse): ContextDetails {
  return {
    ...toContextSummary(context),
    messages: context.messages ?? [],
    usage: normalizeTokenUsage(context.usage),
  };
}

function isNotFoundError(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

export async function getProjectContexts(
  projectId: string,
  client: ProjectContextApiClient = browserClient,
): Promise<ContextSummary[]> {
  const result = (await client.apiGet("/api/projects/{projectId}/contexts", {
    params: { path: { projectId } },
  })) as ProjectContextSummaryResponse[];

  return result.map(toContextSummary).filter(shouldIncludeContext);
}

export async function getProjectContextDetails(
  projectId: string,
  contextId: string,
  client: ProjectContextApiClient = browserClient,
): Promise<ContextDetails> {
  const response = (await client.apiGet("/api/projects/{projectId}/contexts/{contextId}", {
    params: { path: { projectId, contextId } },
  })) as ProjectContextResponse;

  return toContextDetails(response);
}

export async function updateProjectContextTitle(
  projectId: string,
  contextId: string,
  title: string,
  client: ProjectContextApiClient = browserClient,
): Promise<boolean> {
  const normalizedTitle = title.trim();
  if (!projectId || !contextId || !normalizedTitle) {
    return false;
  }

  try {
    await client.apiPut("/api/projects/{projectId}/contexts/{contextId}/title", {
      params: { path: { projectId, contextId } },
      body: { title: normalizedTitle },
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function deleteProjectContext(
  projectId: string,
  contextId: string,
  client: ProjectContextApiClient = browserClient,
): Promise<boolean> {
  if (!projectId || !contextId) {
    return false;
  }

  try {
    await client.apiDelete("/api/projects/{projectId}/contexts/{contextId}", {
      params: { path: { projectId, contextId } },
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function clearProjectContextRecords(
  projectId: string,
  contextId: string,
  client: ProjectContextApiClient = browserClient,
): Promise<boolean> {
  if (!projectId || !contextId) {
    return false;
  }

  try {
    await client.apiDelete("/api/projects/{projectId}/contexts/{contextId}/clear-records", {
      params: { path: { projectId, contextId } },
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function deleteAllProjectContexts(
  projectId: string,
  client: ProjectContextApiClient = browserClient,
): Promise<boolean> {
  if (!projectId) {
    return false;
  }

  try {
    await client.apiDelete("/api/projects/{projectId}/contexts", {
      params: { path: { projectId } },
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export type ProjectContextService = {
  getProjectContexts(projectId: string): Promise<ContextSummary[]>;
  getProjectContextDetails(projectId: string, contextId: string): Promise<ContextDetails>;
  updateProjectContextTitle(projectId: string, contextId: string, title: string): Promise<boolean>;
  deleteProjectContext(projectId: string, contextId: string): Promise<boolean>;
  clearProjectContextRecords(projectId: string, contextId: string): Promise<boolean>;
  deleteAllProjectContexts(projectId: string): Promise<boolean>;
};

export function createProjectContextService(
  client: ProjectContextApiClient,
): ProjectContextService {
  return {
    getProjectContexts: (projectId) => getProjectContexts(projectId, client),
    getProjectContextDetails: (projectId, contextId) =>
      getProjectContextDetails(projectId, contextId, client),
    updateProjectContextTitle: (projectId, contextId, title) =>
      updateProjectContextTitle(projectId, contextId, title, client),
    deleteProjectContext: (projectId, contextId) =>
      deleteProjectContext(projectId, contextId, client),
    clearProjectContextRecords: (projectId, contextId) =>
      clearProjectContextRecords(projectId, contextId, client),
    deleteAllProjectContexts: (projectId) => deleteAllProjectContexts(projectId, client),
  };
}
