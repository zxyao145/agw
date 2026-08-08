import type { components } from "@agw/api";
import type { ToolValueObject } from "@agw/tools";

type ToolsField = { tools: ToolValueObject[] };

export type ProjectResponse = Omit<components["schemas"]["ProjectResponse"], "tools"> & ToolsField;
export type ProjectCreateRequest = Omit<components["schemas"]["ProjectCreateRequest"], "tools"> &
  ToolsField;
export type ProjectUpdateRequest = Omit<components["schemas"]["ProjectUpdateRequest"], "tools"> &
  ToolsField;

export interface ProjectUpdateMutationVariables {
  project: ProjectResponse;
  body: ProjectUpdateRequest;
}
