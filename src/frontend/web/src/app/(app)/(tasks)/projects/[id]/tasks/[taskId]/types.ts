import type { AiMessage } from "@/types";
import type { ProjectTaskSummaryResponse } from "@/api/task-client";

export type ProjectTaskDto = ProjectTaskSummaryResponse & {
  input: string;
  messageCount: number;
  messages?: AiMessage[] | null;
};
