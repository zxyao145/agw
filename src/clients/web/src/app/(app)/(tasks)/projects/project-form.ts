const RESERVED_WINDOWS_FOLDER_NAMES = new Set([
  "CON",
  "PRN",
  "AUX",
  "NUL",
  "COM1",
  "COM2",
  "COM3",
  "COM4",
  "COM5",
  "COM6",
  "COM7",
  "COM8",
  "COM9",
  "LPT1",
  "LPT2",
  "LPT3",
  "LPT4",
  "LPT5",
  "LPT6",
  "LPT7",
  "LPT8",
  "LPT9",
]);

export function formatProjectFolderName(value: string): string {
  const formatted = value
    .trim()
    .replace(/[^\p{L}\p{N}._-]+/gu, "_")
    .replace(/_+/g, "_")
    .replace(/^[_.\s]+|[_.\s]+$/g, "");

  if (!formatted || formatted === "." || formatted === ".." || formatted.length > 255) {
    return "";
  }

  const baseName = formatted.split(".")[0].toUpperCase();
  if (RESERVED_WINDOWS_FOLDER_NAMES.has(baseName)) {
    return "";
  }

  return formatted;
}

export function getDefaultProjectWorkspace(projectName: string): string {
  const folderName = formatProjectFolderName(projectName);
  return folderName ? `~/.agw/${folderName}` : "";
}

export function syncDefaultProjectWorkspace({
  previousName,
  nextName,
  currentWorkspace,
}: {
  previousName: string;
  nextName: string;
  currentWorkspace: string;
}): string {
  const previousDefaultWorkspace = getDefaultProjectWorkspace(previousName);
  const nextDefaultWorkspace = getDefaultProjectWorkspace(nextName);
  const currentWorkspaceValue = currentWorkspace.trim();

  if (!currentWorkspaceValue || currentWorkspaceValue === previousDefaultWorkspace) {
    return nextDefaultWorkspace;
  }

  return currentWorkspace;
}

export function resolveCreateProjectWorkspace(
  projectName: string,
  workspace: string,
): string | null {
  const workspaceValue = workspace.trim();
  if (workspaceValue) {
    return workspaceValue;
  }

  return getDefaultProjectWorkspace(projectName) || null;
}

export function getProjectExtraSettingsError(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }

  try {
    JSON.parse(trimmed);
    return null;
  } catch {
    return "Settings must be valid JSON.";
  }
}

export function normalizeProjectExtraSettings(value: string): string | null {
  const error = getProjectExtraSettingsError(value);
  if (error) {
    throw new Error(error);
  }

  const trimmed = value.trim();
  return trimmed || null;
}

interface ProjectCapabilitiesInput {
  selectedTools: string[];
  selectedSkillIds: string[];
  selectedMcpToolServerIds: string[];
  selectedAppInstanceIds: string[];
  environmentVariables: Record<string, string>;
}

export function serializeProjectCapabilities({
  selectedTools,
  selectedSkillIds,
  selectedMcpToolServerIds,
  selectedAppInstanceIds,
  environmentVariables,
}: ProjectCapabilitiesInput) {
  return {
    tools: JSON.stringify(selectedTools),
    skillIds: selectedSkillIds,
    mcpToolServerIds: selectedMcpToolServerIds,
    appInstanceIds: selectedAppInstanceIds,
    environmentVariables,
  };
}

interface ProjectCapabilityResponse {
  tools?: string | null;
  projectSkillRelations?: Array<{ skillId: string }> | null;
  projectMcpToolServers?: Array<{ mcpToolServerId: string }> | null;
  projectAppRelations?: Array<{ appInstanceId: string }> | null;
  environmentVariables?: Record<string, string> | null;
}

export function toProjectCapabilityFormState(project: ProjectCapabilityResponse) {
  let selectedTools: string[] = [];
  try {
    const tools = project.tools ? JSON.parse(project.tools) : [];
    selectedTools =
      Array.isArray(tools) && tools.every((tool) => typeof tool === "string") ? tools : [];
  } catch {
    selectedTools = [];
  }

  return {
    selectedTools,
    selectedSkillIds: project.projectSkillRelations?.map((relation) => relation.skillId) ?? [],
    selectedMcpToolServerIds:
      project.projectMcpToolServers?.map((relation) => relation.mcpToolServerId) ?? [],
    selectedAppInstanceIds:
      project.projectAppRelations?.map((relation) => relation.appInstanceId) ?? [],
    environmentVariables: project.environmentVariables ?? {},
  };
}
