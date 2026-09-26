import type { AiMessage, ConversationHistoryTurn, components } from "@agw/api";
import { normalizeTokenUsage, type TokenUsage, type TokenUsageInput } from "@agw/api";

import { ApiError, type AgwApiClient } from "@agw/api";
import * as browserClient from "@agw/api";

type ProjectConversationApiClient = Pick<AgwApiClient, "apiGet" | "apiPut" | "apiDelete">;

export type ConversationMessageDirection = "newer" | "older";

export type ConversationTurnInputSummary = Pick<
  components["schemas"]["ConversationTurnResponse"],
  "inputMessageId" | "inputSummary"
>;

export async function getConversationTurnInputs(
  conversationId: string,
  signal?: AbortSignal,
): Promise<ConversationTurnInputSummary[]> {
  const inputs: ConversationTurnInputSummary[] = [];
  let beforeSequence: number | string | undefined;
  let beforeTurnId: string | undefined;
  while (true) {
    const page = await browserClient.apiGet("/api/projects/conversation-turns", {
      params: { query: { conversationId, beforeSequence, beforeTurnId, limit: 100 } },
      signal,
    });
    if (!page) throw new Error("The conversation turn page is missing.");
    inputs.push(
      ...page.items
        .filter((turn) => turn.inputMessageId !== browserClient.EMPTY_GUID)
        .map(({ inputMessageId, inputSummary }) => ({ inputMessageId, inputSummary })),
    );
    if (!page.hasMore) break;
    if (page.nextBeforeSequence === null || page.nextBeforeTurnId === null) {
      throw new Error("The conversation turn page is missing its next cursor.");
    }
    beforeSequence = page.nextBeforeSequence;
    beforeTurnId = page.nextBeforeTurnId;
  }
  return inputs.reverse();
}

export type ConversationActivityStatus = "running" | "failed" | "interrupted";

export type ConversationActivityItem = {
  conversationId: string;
  turnId: string;
  status: ConversationActivityStatus;
};

/**
 * 读取项目中状态不是 idle 的会话，响应中没有的会话为 idle。
 * Reads the project's conversations whose status is not idle; conversations missing from the response are idle.
 */
export async function getConversationActivity(
  projectId: string,
  signal?: AbortSignal,
): Promise<ConversationActivityItem[]> {
  const response = await browserClient.apiGet("/api/projects/conversation-activity", {
    params: { query: { projectId } },
    signal,
  });
  if (!response) throw new Error("The conversation activity response is missing.");
  return response.items.map(({ conversationId, turnId, status }) => {
    if (status !== "running" && status !== "failed" && status !== "interrupted") {
      throw new Error(`The conversation status '${status}' is not supported.`);
    }
    return { conversationId, turnId, status };
  });
}

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

export type ConversationPage = {
  items: ConversationSummary[];
  total: number;
  pageIndex: number;
  pageSize: number;
};

export type ConversationPageOptions = {
  pageIndex?: number;
  pageSize?: 10 | 20 | 50;
  contextId?: string;
  signal?: AbortSignal;
};

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
  turns: ConversationHistoryTurn[];
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

export type ProjectConversationPageResponse = {
  items: ProjectConversationSummaryResponse[];
  total: number;
  pageIndex: number;
  pageSize: number;
};

export type ProjectConversationResponse = ProjectConversationSummaryResponse & {
  usage?: TokenUsageInput | null;
  resumeState?: ConversationResumeState | null;
};

export type ProjectConversationMessagePageResponse = {
  items?: AiMessage[] | null;
  nextCursor?: string | null;
  hasMore: boolean;
  turns: ConversationHistoryTurn[];
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
  options: ConversationPageOptions = {},
  client: ProjectConversationApiClient = browserClient,
): Promise<ConversationPage> {
  const result = (await client.apiGet("/api/projects/{projectId}/conversations", {
    params: {
      path: { projectId },
      query: {
        pageIndex: options.pageIndex ?? 1,
        pageSize: options.pageSize ?? 20,
        contextId: options.contextId,
      },
    },
    signal: options.signal,
  })) as ProjectConversationPageResponse;

  return {
    items: result.items.map(toConversationSummary),
    total: result.total,
    pageIndex: result.pageIndex,
    pageSize: result.pageSize,
  };
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
    turns: response.turns,
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
  getProjectConversations(
    projectId: string,
    options?: ConversationPageOptions,
  ): Promise<ConversationPage>;
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
    signal?: AbortSignal,
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
    getProjectConversations: (projectId, options) =>
      getProjectConversations(projectId, options, client),
    getProjectConversationDetails: (projectId, conversationId) =>
      getProjectConversationDetails(projectId, conversationId, client),
    getProjectConversationMessages: (projectId, conversationId, options) =>
      getProjectConversationMessages(projectId, conversationId, options, client),
    getProjectConversationHistory: (projectId, conversationId, signal) =>
      getProjectConversationHistory(projectId, conversationId, client, signal),
    updateProjectConversationTitle: (projectId, conversationId, title) =>
      updateProjectConversationTitle(projectId, conversationId, title, client),
    deleteProjectConversation: (projectId, conversationId) =>
      deleteProjectConversation(projectId, conversationId, client),
    clearProjectConversationRecords: (projectId, conversationId) =>
      clearProjectConversationRecords(projectId, conversationId, client),
    deleteAllProjectConversations: (projectId) => deleteAllProjectConversations(projectId, client),
  };
}
