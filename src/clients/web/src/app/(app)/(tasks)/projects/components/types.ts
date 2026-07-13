import type { components } from "@/api/openapi";

export type ProjectResponse = components["schemas"]["ProjectResponse"];
export type ProjectCreateRequest = components["schemas"]["ProjectCreateRequest"];
export type ProjectUpdateRequest = components["schemas"]["ProjectUpdateRequest"];

export interface ProjectUpdateMutationVariables {
  project: ProjectResponse;
  body: ProjectUpdateRequest;
}
