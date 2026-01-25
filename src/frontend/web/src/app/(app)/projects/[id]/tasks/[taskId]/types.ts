
export type ProjectTaskDto = {
  id: string;
  projectId: string;
  agentflowId: string;
  status: number;
  description: string;
  input: string;
  outputJson?: string | null;
  errorMessage?: string | null;
  createTime?: string | null;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
};