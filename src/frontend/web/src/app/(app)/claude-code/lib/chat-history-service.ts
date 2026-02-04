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

const DEFAULT_PROJECT_ID = "00000000-0000-0000-0000-000000000000";
const LIST_ENDPOINT = "/api/session-records";

function buildProjectQuery(projectId?: string) {
  const value = projectId?.trim() || DEFAULT_PROJECT_ID;
  return `projectId=${encodeURIComponent(value)}`;
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
  projectId?: string,
): Promise<ChatSessionRecordSummary[]> {
  const url = `${LIST_ENDPOINT}?${buildProjectQuery(projectId)}`;
  return await fetchJson<ChatSessionRecordSummary[]>(url);
}

export async function getSessionByThreadId(
  sessionId: string,
  projectId?: string,
): Promise<ChatSessionRecordDetails | null> {
  if (!sessionId) {
    return null;
  }

  const url = `${LIST_ENDPOINT}/${encodeURIComponent(sessionId)}?${buildProjectQuery(projectId)}`;
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

export async function deleteSessionByThreadId(
  sessionId: string,
  projectId?: string,
): Promise<boolean> {
  if (!sessionId) {
    return false;
  }
  const url = `${LIST_ENDPOINT}/${encodeURIComponent(sessionId)}?${buildProjectQuery(projectId)}`;
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
  projectId?: string,
): Promise<boolean> {
  if (!sessionId || !newTitle.trim()) {
    return false;
  }
  const url = `${LIST_ENDPOINT}/${encodeURIComponent(sessionId)}/title?${buildProjectQuery(projectId)}`;
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

export async function clearAllSessions(projectId?: string): Promise<void> {
  const sessions = await getAllSessions(projectId);
  await Promise.all(
    sessions.map((session) => deleteSessionByThreadId(session.sessionId, projectId)),
  );
}

export function subscribeToSessions(
  callback: (sessions: ChatSessionRecordSummary[]) => void,
  projectId?: string,
): () => void {
  let active = true;
  const poll = async () => {
    try {
      const sessions = await getAllSessions(projectId);
      if (active) {
        callback(sessions);
      }
    } catch (error) {
      console.error("Failed to refresh chat history:", error);
    }
  };

  void poll();
  const intervalId = window.setInterval(poll, 200000);

  return () => {
    active = false;
    window.clearInterval(intervalId);
  };
}
