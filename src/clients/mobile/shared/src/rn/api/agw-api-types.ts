export type AgwProject = {
  id: string;
  name: string;
  description?: string | null;
  workspace?: string | null;
  extraSetting?: string | null;
};

export type AgwAgent = {
  id: string;
  displayName: string;
  name: string;
};

export type AgwAgentflow = {
  id: string;
  name: string;
};

export type AgwTargetType = "agent" | "agentflow";

export type AgwTarget = {
  id: string;
  label: string;
  type: AgwTargetType;
  agentType: 0 | 1;
};

export type AgwTaskSummary = {
  id: string;
  projectId: string;
  contextId: string;
  jobId?: string | null;
  status: number;
  title: string;
  errorMessage?: string | null;
  createTime: string;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
};

export type AgwMessageContent = {
  type: string;
  content?: string | null;
  additionalProperties?: Record<string, unknown> | null;
};

export type AgwMessage = {
  messageId: string;
  author?: string | null;
  role: string;
  contents: AgwMessageContent[];
  additionalProperties?: Record<string, unknown> | null;
};

export type AgwContextSummary = {
  projectId: string;
  contextId: string;
  title: string;
  latestTaskId?: string | null;
  latestStatus?: number | null;
  taskCount: number;
  messageCount: number;
  createTime: string;
  updateTime?: string | null;
  errorMessage?: string | null;
};

export type AgwContextDetails = AgwContextSummary & {
  tasks: AgwTaskSummary[];
  messages?: AgwMessage[] | null;
};

export type AgwFileItem = {
  name: string;
  path: string;
  type: "file" | "directory" | string;
  size?: number | null;
  modifiedTime?: string | null;
  gitStatus?: string | null;
  children?: AgwFileItem[];
};

export type AgwFileListResponse = {
  items: AgwFileItem[];
};

export type AgwFileActionResponse = {
  message: string;
  success: boolean;
};

export type AgwGitDiffResponse = {
  diff: string;
  unchanged: boolean;
  message?: string | null;
  originalContent?: string | null;
};

export type AgwExecutionResponse = {
  taskId?: string | null;
  messages: AgwMessage[];
};
