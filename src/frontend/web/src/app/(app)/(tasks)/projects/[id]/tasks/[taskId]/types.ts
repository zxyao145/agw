import type { AiMessage } from "@/types";

export type ProjectTaskDto = {
  id: string;
  projectId: string;
  contextId: string;
  jobId?: string | null;
  status: number;
  title: string;
  input: string;
  errorMessage?: string | null;
  createTime?: string | null;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
  messageCount: number;
  messages?: AiMessage[] | null;
};
