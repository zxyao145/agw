import type { AiMessage } from "@/types";

import { ApiError } from "./client";
import * as client from "./client";
import type { components } from "./openapi";

type SessionRecordTitleUpdateRequest = components["schemas"]["SessionRecordTitleUpdateRequest"];

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

export type ProjectTaskSummaryResponse = {
  id: string;
  projectId: string;
  contextId: string;
  jobId?: string | null;
  status?: number;
  title: string;
  errorMessage?: string | null;
  createTime: string;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
};

type ProjectTaskHistoryResponse = ProjectTaskSummaryResponse & {
  input?: string;
  messageCount: number;
  messages?: AiMessage[] | null;
};

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

function isNotFoundError(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

export async function getAllTasks(projectId: string): Promise<TaskSummary[]> {
  const result = (await client.apiGet("/api/projects/{projectId}/tasks", {
    params: { path: { projectId } },
  })) as ProjectTaskSummaryResponse[];

  return result.map(toChatSessionSummary);
}

export async function getTaskDetails(
  projectId: string,
  taskId: string,
): Promise<TaskRecordDetails> {
  const response = (await client.apiGet("/api/projects/{projectId}/tasks/{taskId}", {
    params: { path: { projectId, taskId } },
  })) as ProjectTaskHistoryResponse;
  return toChatSessionDetails(response);
}

export async function deleteTaskById(taskId: string, projectId: string): Promise<boolean> {
  if (!taskId || !projectId) {
    return false;
  }

  try {
    await client.apiDelete("/api/projects/{projectId}/tasks/{taskId}", {
      params: { path: { projectId, taskId } },
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function updateTaskTitle(
  taskId: string,
  newTitle: string,
  projectId: string,
): Promise<boolean> {
  if (!taskId || !newTitle.trim()) {
    return false;
  }

  const body: SessionRecordTitleUpdateRequest = {
    title: newTitle.trim(),
  };

  try {
    await client.apiPut("/api/projects/{projectId}/tasks/{taskId}/title", {
      params: { path: { projectId, taskId } },
      body,
    });
    return true;
  } catch (error) {
    if (isNotFoundError(error)) {
      return false;
    }
    throw error;
  }
}

export async function deleteAllTasks(projectId: string): Promise<void> {
  const tasks = await getAllTasks(projectId);
  await Promise.all(tasks.map((task) => deleteTaskById(task.taskId, projectId)));
}
