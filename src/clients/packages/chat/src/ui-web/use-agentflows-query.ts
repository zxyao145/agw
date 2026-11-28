import { apiGet } from "@agw/api";
import { useQuery } from "@agw/components/query";

export function useAgentflowsQuery(enabled = true) {
  return useQuery({
    queryKey: ["agentflows"],
    queryFn: () => apiGet("/api/agentflows"),
    enabled,
  });
}
