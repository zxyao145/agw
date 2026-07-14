import type { AiMessage } from "@/types";
import { normalizeTokenUsage, type TokenUsage, type TokenUsageInput } from "@/lib/token-usage";

import { ApiError } from "./client";
import * as client from "./client";

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

function hasConversationMessages(context: ContextSummary): boolean {
  return context.messageCount > 0;
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

export async function getProjectContexts(projectId: string): Promise<ContextSummary[]> {
  const result = (await client.apiGet("/api/projects/{projectId}/contexts", {
    params: { path: { projectId } },
  })) as ProjectContextSummaryResponse[];

  return result.map(toContextSummary).filter(hasConversationMessages);
}

export async function getProjectContextDetails(
  projectId: string,
  contextId: string,
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

export async function deleteProjectContext(projectId: string, contextId: string): Promise<boolean> {
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

export async function deleteAllProjectContexts(projectId: string): Promise<boolean> {
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
