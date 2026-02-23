"use client";

import type { AiMessage } from "@/types";

export interface ChatSessionRecordSummary {
  id: number;
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

const LIST_ENDPOINT = "/api/session-records";
export const CLAUDE_CODE_PROJECT_ID = "claude-code";

function buildProjectQuery(projectId: string) {
  const value = projectId.trim();
  if (!value) {
    throw new Error("projectId is required");
  }
  return `projectId=${encodeURIComponent(value)}`;
}

function appendProjectQuery(url: string, projectId: string): string {
  const query = buildProjectQuery(projectId);
  return `${url}?${query}`;
}

async function fetchJson<T>(input: RequestInfo, init?: RequestInit): Promise<T> {
  const response = await fetch(input, init);
  if (!response.ok) {
    const message = await response.text().catch(() => "");
    throw new Error(message || `Request failed: ${response.status}`);
  }
  return (await response.json()) as T;
}

export async function getAllSessions(
  projectId: string,
): Promise<ChatSessionRecordSummary[]> {
  const url = appendProjectQuery(LIST_ENDPOINT, projectId);
  return await fetchJson<ChatSessionRecordSummary[]>(url);
}

export async function getSessionBySessionId(
  sessionId: string,
  projectId: string,
): Promise<ChatSessionRecordDetails | null> {
  if (!sessionId) {
    return null;
  }

  const url = appendProjectQuery(
    `${LIST_ENDPOINT}/${encodeURIComponent(sessionId)}`,
    projectId,
  );
  const response = await fetch(url);
  if (response.status === 404) {
    return null;
  }
  if (!response.ok) {
    const message = await response.text().catch(() => "");
    throw new Error(message || `Request failed: ${response.status}`);
  }
  return (await response.json()) as ChatSessionRecordDetails;
}

export async function deleteSessionBySessionId(
  sessionId: string,
  projectId: string,
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }
  const url = appendProjectQuery(
    `${LIST_ENDPOINT}/${encodeURIComponent(sessionId)}`,
    projectId,
  );
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

export async function updateSessionTitle(
  sessionId: string,
  newTitle: string,
  projectId: string,
): Promise<boolean> {
  if (!sessionId || !newTitle.trim()) {
    return false;
  }
  const url = appendProjectQuery(
    `${LIST_ENDPOINT}/${encodeURIComponent(sessionId)}/title`,
    projectId,
  );
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
  await Promise.all(
    sessions.map((session) => deleteSessionBySessionId(session.sessionId, projectId)),
  );
}
