"use client";

import type { AiMessage } from "@/types";

export interface ChatSessionRecordSummary {
  id: string;
  projectId: string;
  sessionId: string;
  title: string;
  messageCount: number;
  createTime: string;
  updateTime?: string | null;
}

export interface ChatSessionRecordDetails extends ChatSessionRecordSummary {
  messages: AiMessage[];
}

export const CLAUDE_CODE_PROJECT_ID = "claude-code";
type ProjectTaskHistoryResponse = {
  id: string;
  projectId: string;
  contextId: string;
  sessionId: string;
  title: string;
  messageCount: number;
  createTime: string;
  updateTime?: string | null;
  messages?: AiMessage[] | null;
};

function buildTasksEndpoint(projectId: string, taskId = ""): string {
  const value = projectId.trim();
  if (!value) {
    throw new Error("projectId is required");
  }
  return `/api/projects/${encodeURIComponent(value)}/tasks${taskId}`;
}

async function fetchJson<T>(input: RequestInfo, init?: RequestInit): Promise<T> {
  const response = await fetch(input, init);
  if (!response.ok) {
    const message = await response.text().catch(() => "");
    throw new Error(message || `Request failed: ${response.status}`);
  }
  return (await response.json()) as T;
}

function toChatSessionSummary(task: ProjectTaskHistoryResponse): ChatSessionRecordSummary {
  return {
    id: task.id,
    projectId: task.projectId,
    sessionId: task.sessionId || task.contextId,
    title: task.title,
    messageCount: task.messageCount ?? 0,
    createTime: task.createTime,
    updateTime: task.updateTime ?? null,
  };
}

function toChatSessionDetails(task: ProjectTaskHistoryResponse): ChatSessionRecordDetails {
  const summary = toChatSessionSummary(task);
  return {
    ...summary,
    messages: task.messages ?? [],
  };
}

async function findTaskByTaskId(
  taskId: string,
  projectId: string,
): Promise<ProjectTaskHistoryResponse | null> {
  if (!taskId) {
    return null;
  }

  const session = await getTaskSessions(projectId, taskId);
  return session;
}

async function clearTaskSessionById(taskId: string, projectId: string): Promise<boolean> {
  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(taskId)}/session`);
  const response = await fetch(url, { method: "DELETE" });
  if (response.status === 404) {
    return false;
  }
  if (!response.ok) {
    const message = await response.text().catch(() => "");
    throw new Error(message || `Request failed: ${response.status}`);
  }
  return true;
}

export async function getAllTasksSessions(projectId: string): Promise<ChatSessionRecordSummary[]> {
  const url = buildTasksEndpoint(projectId);
  const tasks = await fetchJson<ProjectTaskHistoryResponse[]>(url);
  return tasks.map(toChatSessionSummary);
}

export async function getTaskSessions(projectId: string, taskId: string): Promise<ProjectTaskHistoryResponse> {
  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(taskId)}`);
  const task = await fetchJson<ProjectTaskHistoryResponse>(url);
  // TODO
  return task;
}

export async function getSessionByTaskId(
  taskId: string | null | undefined,
  projectId: string,
): Promise<ChatSessionRecordDetails | null> {
  if (!taskId) {
    return null;
  }

  const task = await findTaskByTaskId(taskId, projectId);
  if (!task) {
    console.warn("task not found, sessionId:", taskId, projectId)
    console.warn("task not found, contextId:", taskId, ", projectId:", projectId)
    return null;
  }

  return toChatSessionDetails(task);
}

export async function deleteSessionBySessionId(
  sessionId: string,
  projectId: string,
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }
  const task = await findTaskByTaskId(sessionId, projectId);
  if (!task) {
    return false;
  }

  return await clearTaskSessionById(task.id, projectId);
}

export async function updateSessionTitle(
  sessionId: string,
  newTitle: string,
  projectId: string,
): Promise<boolean> {
  if (!sessionId || !newTitle.trim()) {
    return false;
  }
  const task = await findTaskByTaskId(sessionId, projectId);
  if (!task) {
    return false;
  }

  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(task.id)}/title`);
  const response = await fetch(url, {
    method: "PUT",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ title: newTitle.trim() }),
  });
  if (response.status === 404) {
    return false;
  }
  if (!response.ok) {
    const message = await response.text().catch(() => "");
    throw new Error(message || `Request failed: ${response.status}`);
  }
  return true;
}

export async function clearAllSessions(projectId: string): Promise<void> {
  const sessions = await getAllTasksSessions(projectId);
  await Promise.all(sessions.map((session) => clearTaskSessionById(session.id, projectId)));
}
