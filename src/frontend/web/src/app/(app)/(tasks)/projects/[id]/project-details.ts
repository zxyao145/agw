import { parseTargetValue } from "../../../../../lib/chat-target-options.ts";

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

export type CreateTaskJobRequest = {
  projectId: string;
  agentType: 0 | 1;
  agentId: string;
  name: string;
  prompt: string;
  triggerType: number;
  triggerValue: string;
  maxRetryCount: number;
  isEnabled: boolean;
};

export const CREATE_TASK_DIALOG_TITLE = "Create Task";
export const CREATE_TASK_DIALOG_DESCRIPTION = "Create a one-time job for the current project.";
export const CREATE_TASK_PROMPT_HELPER_TEXT = "Prompt is required for task execution.";
export const QUICK_TASK_TRIGGER_TYPE = 1;
export const QUICK_TASK_DELAY_MS = 10_000;
export const DETAILS_BUTTON_LABEL = "Details";
export const CREATE_TASK_BUTTON_LABEL = "Create Task";
export const PROJECT_DETAILS_DIALOG_TITLE = "Project Details";

function normalizeProjectText(value?: string | null): string {
  const trimmedValue = value?.trim();
  return trimmedValue && trimmedValue.length > 0 ? trimmedValue : "-";
}

function padNumber(value: number, length = 2): string {
  return String(value).padStart(length, "0");
}

export function createDefaultTaskJobName(now = new Date(), randomNumber?: number): string {
  const safeRandomNumber = randomNumber ?? Math.floor(Math.random() * 10_000);
  const randomSuffix = padNumber(Math.max(0, safeRandomNumber) % 10_000, 4);

  const year = now.getUTCFullYear();
  const month = padNumber(now.getUTCMonth() + 1);
  const day = padNumber(now.getUTCDate());

  // const hour = padNumber(now.getUTCHours());
  // const minute = padNumber(now.getUTCMinutes());
  // const second = padNumber(now.getUTCSeconds());
  // return `Job-${year}${month}${day}-${hour}${minute}${second}-${randomSuffix}`;

  return `Job-${year}${month}${day}-${randomSuffix}`;
}

export function createQuickTaskTriggerValue(now = new Date()): string {
  return new Date(now.getTime() + QUICK_TASK_DELAY_MS).toISOString();
}

export function buildCreateTaskJobRequest({
  projectId,
  targetValue,
  jobName,
  prompt,
  now = new Date(),
}: {
  projectId: string;
  targetValue: string;
  jobName: string;
  prompt: string;
  now?: Date;
}): CreateTaskJobRequest {
  const parsedTarget = parseTargetValue(targetValue);
  if (!parsedTarget) {
    throw new Error("A valid agent or agentflow target is required.");
  }

  const trimmedJobName = jobName.trim();
  const trimmedPrompt = prompt.trim();
  if (!trimmedJobName) {
    throw new Error("Job name is required.");
  }
  if (!trimmedPrompt) {
    throw new Error("Prompt is required.");
  }

  return {
    projectId,
    agentType: parsedTarget.type === "agent" ? 0 : 1,
    agentId: parsedTarget.id,
    name: trimmedJobName,
    prompt: trimmedPrompt,
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
