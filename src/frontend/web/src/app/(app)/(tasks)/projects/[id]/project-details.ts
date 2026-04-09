export type ProjectDetails = {
  id: string;
  name: string;
  description: string | null;
  workspace?: string | null;
  extraSetting?: string | null;
  enable: boolean;
  createTime?: string | null;
  updateTime?: string | null;
};

export type ProjectDetailItem = {
  label: string;
  value: string;
  mono?: boolean;
};

export type QuickTaskJobRequest = {
  projectId: string;
  agentType: null;
  agentId: null;
  name: string;
  prompt: null;
  triggerType: number;
  triggerValue: string;
  maxRetryCount: number;
  isEnabled: boolean;
};

export const DETAILS_BUTTON_LABEL = "Details";
export const CREATE_TASK_BUTTON_LABEL = "Create Task";
export const PROJECT_DETAILS_DIALOG_TITLE = "Project Details";
export const QUICK_TASK_TRIGGER_TYPE = 1;
export const QUICK_TASK_DELAY_MS = 10_000;

function normalizeProjectText(value?: string | null): string {
  const trimmedValue = value?.trim();
  return trimmedValue && trimmedValue.length > 0 ? trimmedValue : "-";
}

function createQuickTaskName(projectName: string, now: Date): string {
  const safeProjectName = projectName.trim() || "Project";
  return `${safeProjectName} - Quick Task - ${now.toISOString().replace(/\.\d{3}Z$/, "Z")}`;
}

export function createQuickTaskTriggerValue(now = new Date()): string {
  return new Date(now.getTime() + QUICK_TASK_DELAY_MS).toISOString();
}

export function buildQuickTaskJobRequest(
  project: Pick<ProjectDetails, "id" | "name">,
  now = new Date(),
): QuickTaskJobRequest {
  return {
    projectId: project.id,
    agentType: null,
    agentId: null,
    name: createQuickTaskName(project.name, now),
    prompt: null,
    triggerType: QUICK_TASK_TRIGGER_TYPE,
    triggerValue: createQuickTaskTriggerValue(now),
    maxRetryCount: 0,
    isEnabled: true,
  };
}

export function getProjectDetailItems(
  project: Pick<ProjectDetails, "description" | "workspace" | "extraSetting">,
): ProjectDetailItem[] {
  return [
    {
      label: "Description",
      value: normalizeProjectText(project.description),
    },
    {
      label: "Workspace",
      value: normalizeProjectText(project.workspace),
      mono: true,
    },
    {
      label: "Extra Setting",
      value: normalizeProjectText(project.extraSetting),
      mono: true,
    },
  ];
}
