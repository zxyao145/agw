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

function buildTasksEndpoint(projectId: string, suffix = ""): string {
  const value = projectId.trim();
  if (!value) {
    throw new Error("projectId is required");
  }
  return `/api/projects/${encodeURIComponent(value)}/tasks${suffix}`;
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

async function findTaskBySessionId(
  sessionId: string,
  projectId: string,
): Promise<ChatSessionRecordSummary | null> {
  if (!sessionId) {
    return null;
  }

  const sessions = await getAllSessions(projectId);
  return (
    sessions.find((session) => session.sessionId === sessionId || session.id === sessionId) ?? null
  );
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

export async function getAllSessions(projectId: string): Promise<ChatSessionRecordSummary[]> {
  const url = buildTasksEndpoint(projectId);
  const tasks = await fetchJson<ProjectTaskHistoryResponse[]>(url);
  return tasks.map(toChatSessionSummary);
}

export async function getSessionBySessionId(
  sessionId: string,
  projectId: string,
): Promise<ChatSessionRecordDetails | null> {
  if (!sessionId) {
    return null;
  }

  const task = await findTaskBySessionId(sessionId, projectId);
  if (!task) {
    return null;
  }

  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(task.id)}`);
  const response = await fetch(url);
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    const message = await response.text().catch(() => "");
    throw new Error(message || `Request failed: ${response.status}`);
  }
  return toChatSessionDetails((await response.json()) as ProjectTaskHistoryResponse);
}

export async function deleteSessionBySessionId(
  sessionId: string,
  projectId: string,
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }
  const task = await findTaskBySessionId(sessionId, projectId);
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
  const task = await findTaskBySessionId(sessionId, projectId);
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
  const sessions = await getAllSessions(projectId);
  await Promise.all(sessions.map((session) => clearTaskSessionById(session.id, projectId)));
}
