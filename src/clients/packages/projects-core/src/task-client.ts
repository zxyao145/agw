import type { AiMessage } from "@agw/api";
import { normalizeTokenUsage, type TokenUsage, type TokenUsageInput } from "@agw/api";

import { ApiError, type AgwApiClient } from "@agw/api";
import * as browserClient from "@agw/api";

type ProjectConversationApiClient = Pick<
  AgwApiClient,
  "apiGet" | "apiPost" | "apiPut" | "apiDelete"
>;

export type ConversationMessageDirection = "newer" | "older";

export interface ConversationSummary {
  projectId: string;
  conversationId: string;
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

export type ConversationResumeState = {
  targetType?: string | null;
  targetId?: string | null;
  agentMode?: string | null;
};

export interface ConversationDetails extends ConversationSummary {
  usage: TokenUsage;
  resumeState: ConversationResumeState | null;
}

export interface ConversationHistory extends ConversationDetails {
  messages: AiMessage[];
}

export type ConversationMessagePage = {
  items: AiMessage[];
  nextCursor: string | null;
  hasMore: boolean;
};

export type ProjectConversationSummaryResponse = {
  projectId: string;
  conversationId: string;
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

export type ProjectConversationResponse = ProjectConversationSummaryResponse & {
  usage?: TokenUsageInput | null;
  resumeState?: ConversationResumeState | null;
};

export type ProjectConversationMessagePageResponse = {
  items?: AiMessage[] | null;
  nextCursor?: string | null;
  hasMore: boolean;
};

export type ConversationMessagePageOptions = {
  direction: ConversationMessageDirection;
  cursor?: string | null;
  pageSize?: number;
  signal?: AbortSignal;
};

function toConversationSummary(
  conversation: ProjectConversationSummaryResponse,
): ConversationSummary {
  return {
    projectId: conversation.projectId,
    conversationId: conversation.conversationId,
    contextId: conversation.contextId,
    jobId: conversation.jobId ?? null,
    title: conversation.title,
    latestStatus: conversation.latestStatus ?? null,
    executionCount: conversation.executionCount,
    messageCount: conversation.messageCount,
    createTime: conversation.createTime,
    updateTime: conversation.updateTime ?? null,
    errorMessage: conversation.errorMessage ?? null,
  };
}

function toConversationDetails(conversation: ProjectConversationResponse): ConversationDetails {
  return {
    ...toConversationSummary(conversation),
    usage: normalizeTokenUsage(conversation.usage),
    resumeState: conversation.resumeState ?? null,
  };
}

function isNotFoundError(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

export async function getProjectConversations(
  projectId: string,
  client: ProjectConversationApiClient = browserClient,
): Promise<ConversationSummary[]> {
  const result = (await client.apiGet("/api/projects/{projectId}/conversations", {
    params: { path: { projectId } },
  })) as ProjectConversationSummaryResponse[];

  return result.map(toConversationSummary);
}

export async function createProjectConversation(
  projectId: string,
  client: ProjectConversationApiClient = browserClient,
): Promise<ConversationSummary> {
  const response = (await client.apiPost("/api/projects/{projectId}/conversations", {
    params: { path: { projectId } },
    body: { contextId: null },
  })) as ProjectConversationSummaryResponse;

  return toConversationSummary(response);
}

export async function getProjectConversationDetails(
  projectId: string,
  conversationId: string,
  client: ProjectConversationApiClient = browserClient,
  signal?: AbortSignal,
): Promise<ConversationDetails> {
  const response = (await client.apiGet(
    "/api/projects/{projectId}/conversations/{conversationId}",
    {
      params: { path: { projectId, conversationId } },
      signal,
    },
  )) as ProjectConversationResponse;

  return toConversationDetails(response);
}

export async function getProjectConversationMessages(
  projectId: string,
  conversationId: string,
  options: ConversationMessagePageOptions,
  client: ProjectConversationApiClient = browserClient,
): Promise<ConversationMessagePage> {
  const response = (await client.apiGet(
    "/api/projects/{projectId}/conversations/{conversationId}/messages",
    {
      params: {
        path: { projectId, conversationId },
        query: {
          direction: options.direction,
          cursor: options.cursor ?? undefined,
          pageSize: options.pageSize ?? 50,
        },
      },
      signal: options.signal,
    },
  )) as ProjectConversationMessagePageResponse;

  return {
    items: response.items ?? [],
    nextCursor: response.nextCursor ?? null,
    hasMore: response.hasMore,
  };
}

export async function getProjectConversationHistory(
  projectId: string,
  conversationId: string,
  client: ProjectConversationApiClient = browserClient,
  signal?: AbortSignal,
): Promise<ConversationHistory> {
  const [details, firstPage] = await Promise.all([
    getProjectConversationDetails(projectId, conversationId, client, signal),
    getProjectConversationMessages(
      projectId,
      conversationId,
      { direction: "newer", pageSize: 100, signal },
      client,
    ),
  ]);
  const messages: AiMessage[] = [];
  let page = firstPage;

  while (true) {
    messages.push(...page.items);
    if (!page.hasMore || !page.nextCursor) {
      break;
    }

    page = await getProjectConversationMessages(
      projectId,
      conversationId,
      { direction: "newer", cursor: page.nextCursor, pageSize: 100, signal },
      client,
    );
  }

  return { ...details, messages };
}

export async function updateProjectConversationTitle(
  projectId: string,
  conversationId: string,
  title: string,
  client: ProjectConversationApiClient = browserClient,
): Promise<boolean> {
  const normalizedTitle = title.trim();
  if (!projectId || !conversationId || !normalizedTitle) {
    return false;
  }

  try {
    await client.apiPut("/api/projects/{projectId}/conversations/{conversationId}/title", {
      params: { path: { projectId, conversationId } },
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

export async function deleteProjectConversation(
  projectId: string,
  conversationId: string,
  client: ProjectConversationApiClient = browserClient,
): Promise<boolean> {
  if (!projectId || !conversationId) {
    return false;
  }

  try {
    await client.apiDelete("/api/projects/{projectId}/conversations/{conversationId}", {
      params: { path: { projectId, conversationId } },
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function clearProjectConversationRecords(
  projectId: string,
  conversationId: string,
  client: ProjectConversationApiClient = browserClient,
): Promise<boolean> {
  if (!projectId || !conversationId) {
    return false;
  }

  try {
    await client.apiDelete(
      "/api/projects/{projectId}/conversations/{conversationId}/clear-records",
      {
        params: { path: { projectId, conversationId } },
      },
    );
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function deleteAllProjectConversations(
  projectId: string,
  client: ProjectConversationApiClient = browserClient,
): Promise<boolean> {
  if (!projectId) {
    return false;
  }

  try {
    await client.apiDelete("/api/projects/{projectId}/conversations", {
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

export type ProjectConversationService = {
  getProjectConversations(projectId: string): Promise<ConversationSummary[]>;
  createProjectConversation(projectId: string): Promise<ConversationSummary>;
  getProjectConversationDetails(
    projectId: string,
    conversationId: string,
  ): Promise<ConversationDetails>;
  getProjectConversationMessages(
    projectId: string,
    conversationId: string,
    options: ConversationMessagePageOptions,
  ): Promise<ConversationMessagePage>;
  getProjectConversationHistory(
    projectId: string,
    conversationId: string,
  ): Promise<ConversationHistory>;
  updateProjectConversationTitle(
    projectId: string,
    conversationId: string,
    title: string,
  ): Promise<boolean>;
  deleteProjectConversation(projectId: string, conversationId: string): Promise<boolean>;
  clearProjectConversationRecords(projectId: string, conversationId: string): Promise<boolean>;
  deleteAllProjectConversations(projectId: string): Promise<boolean>;
};

export function createProjectConversationService(
  client: ProjectConversationApiClient,
): ProjectConversationService {
  return {
    getProjectConversations: (projectId) => getProjectConversations(projectId, client),
    createProjectConversation: (projectId) => createProjectConversation(projectId, client),
    getProjectConversationDetails: (projectId, conversationId) =>
      getProjectConversationDetails(projectId, conversationId, client),
    getProjectConversationMessages: (projectId, conversationId, options) =>
      getProjectConversationMessages(projectId, conversationId, options, client),
    getProjectConversationHistory: (projectId, conversationId) =>
      getProjectConversationHistory(projectId, conversationId, client),
    updateProjectConversationTitle: (projectId, conversationId, title) =>
      updateProjectConversationTitle(projectId, conversationId, title, client),
    deleteProjectConversation: (projectId, conversationId) =>
      deleteProjectConversation(projectId, conversationId, client),
    clearProjectConversationRecords: (projectId, conversationId) =>
      clearProjectConversationRecords(projectId, conversationId, client),
    deleteAllProjectConversations: (projectId) => deleteAllProjectConversations(projectId, client),
  };
}
