import { apiGet } from "@agw/api";
import type { AgentflowDetailResponse } from "../../../../types/agentflow";

export const AGENTFLOW_RUN_PROJECT_ID = "11111111-1111-1111-1111-000000000001";

export async function fetchAgentflowDetails(
  id: string,
  revision: number,
): Promise<AgentflowDetailResponse> {
  const detail = await apiGet("/api/agentflows/detail", { params: { query: { id, revision } } });
  if (!detail) throw new Error("Agentflow details were not returned.");
  return detail;
}
