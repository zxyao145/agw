import type { ProjectCreateRequest, ProjectResponse } from "./components/types";

const MAX_PROJECT_NAME_LENGTH = 200;
const COPY_SUFFIX_LENGTH = 8;

function createProjectCopyName(name: string, uniqueSuffix: string): string {
  const normalizedSuffix = uniqueSuffix.replaceAll("-", "").slice(0, COPY_SUFFIX_LENGTH);
  const copySuffix = `-copy-${normalizedSuffix}`;
  const base = name.trimEnd().slice(0, MAX_PROJECT_NAME_LENGTH - copySuffix.length);
  return `${base}${copySuffix}`;
}

export function createProjectCopyRequest(
  project: ProjectResponse,
  uniqueSuffix: string,
): ProjectCreateRequest {
  const mcpToolServerIds =
    project.projectMcpToolServers?.map((relation) => relation.mcpToolServerId) ?? [];
  const skillIds = project.projectSkillRelations?.map((relation) => relation.skillId) ?? [];
  const connectionIds =
    project.projectConnectionRelations?.map((relation) => relation.connectionId) ?? [];

  return {
    name: createProjectCopyName(project.name, uniqueSuffix),
    description: project.description,
    workspace: null,
    extraSetting: project.extraSetting,
    tools: [...project.tools],
    mcpToolServerIds: mcpToolServerIds.length > 0 ? mcpToolServerIds : null,
    skillIds: skillIds.length > 0 ? skillIds : null,
    connectionIds: connectionIds.length > 0 ? connectionIds : null,
    environmentVariables: { ...project.environmentVariables },
  };
}
