"use client";

import type { AiMessage } from "@/types";

export interface TaskSummary {
  taskId: string;
  projectId: string;
  title: string;
  createTime: string;
  updateTime?: string | null;
}

export interface TaskRecordDetails extends TaskSummary {
  messages: AiMessage[];
}

export const CLAUDE_CODE_PROJECT_ID = "11111111-1111-1111-1111-000000000002";
type ProjectTaskSummaryResponse = {
  id: string;
  projectId: string;
  contextId: string;
  title: string;
  createTime: string;
  updateTime?: string | null;
};

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

function toChatSessionSummary(task: ProjectTaskSummaryResponse): TaskSummary {
  return {
    taskId: task.id,
    projectId: task.projectId,
    title: task.title,
    createTime: task.createTime,
    updateTime: task.updateTime ?? null,
  };
}

function toChatSessionDetails(task: ProjectTaskHistoryResponse): TaskRecordDetails {
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

async function clearTaskByTaskId(taskId: string, projectId: string): Promise<boolean> {
  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(taskId)}`);
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

export async function getAllTasks(projectId: string): Promise<TaskSummary[]> {
  const url = buildTasksEndpoint(projectId);
  const tasks = await fetchJson<ProjectTaskSummaryResponse[]>(url);
  return tasks.map(toChatSessionSummary);
}

export async function getTaskSessions(
  projectId: string,
  taskId: string,
): Promise<ProjectTaskHistoryResponse> {
  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(taskId)}`);
  const task = await fetchJson<ProjectTaskHistoryResponse>(url);
  // TODO
  return task;
}

export async function getSessionByTaskId(
  taskId: string | null | undefined,
  projectId: string,
): Promise<TaskRecordDetails | null> {
  if (!taskId) {
    return null;
  }

  const task = await findTaskByTaskId(taskId, projectId);
  if (!task) {
    console.warn("task not found, sessionId:", taskId, projectId);
    console.warn("task not found, contextId:", taskId, ", projectId:", projectId);
    return null;
  }

  return toChatSessionDetails(task);
}

export async function deleteTaskById(
  taskId: string,
  projectId: string,
): Promise<boolean> {
  if (!taskId || !projectId) {
    return false;
  }
  return await clearTaskByTaskId(taskId, projectId);
}

export async function updateTaskTitle(
  taskId: string,
  newTitle: string,
  projectId: string,
): Promise<boolean> {
  if (!taskId || !newTitle.trim()) {
    return false;
  }

  const url = buildTasksEndpoint(projectId, `/${encodeURIComponent(taskId)}/title`);
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

export async function clearAllTasks(projectId: string): Promise<void> {
  const tasks = await getAllTasks(projectId);
  await Promise.all(tasks.map((session) => clearTaskByTaskId(session.taskId, projectId)));
}
