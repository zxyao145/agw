
export type ProjectTaskDto = {
  id: string;
  projectId: string;
  agentType?: number;
  agentflowId?: string | null;
  agentId?: string | null;
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
