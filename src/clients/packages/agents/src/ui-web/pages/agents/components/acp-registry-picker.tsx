import { apiGet, getApiErrorMessage } from "@agw/api";
import { Button, SearchableSelect } from "@agw/components";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { parseAcpExtraObject, readAcpRegistryAgentId, withAcpRegistryAgent } from "./acp-settings";

const registryQueryKey = ["agents", "acp-registry"] as const;

export function AcpRegistryPicker({
  extra,
  setExtra,
  idPrefix = "",
}: {
  extra: string;
  setExtra(value: string): void;
  idPrefix?: string;
}) {
  const queryClient = useQueryClient();
  const registry = useQuery({
    queryKey: registryQueryKey,
    queryFn: ({ signal }) => apiGet("/api/agents/acp-registry", { signal }),
  });
  const refresh = useMutation({
    mutationFn: () => apiGet("/api/agents/acp-registry", { params: { query: { refresh: true } } }),
    onSuccess: (data) => queryClient.setQueryData(registryQueryKey, data),
  });
  const canSelect = parseAcpExtraObject(extra) !== null;
  const selectedAgentId = readAcpRegistryAgentId(extra);
  const options = (registry.data?.agents ?? []).map((agent) => ({
    value: agent.id,
    title: `${agent.name} ${agent.version}`,
    subtitle: [...agent.distributions, agent.description].join(" · "),
    keywords: [agent.id, agent.description, ...agent.distributions],
  }));
  const error = refresh.error ?? registry.error;
  const isFetching = registry.isFetching || refresh.isPending;

  return (
    <div className="grid gap-2">
      <div className="flex items-end gap-2">
        <div className="min-w-0 flex-1">
          <SearchableSelect
            id={`${idPrefix}acpRegistry`}
            label="ACP Registry"
            ariaLabel="ACP Registry"
            value={selectedAgentId ?? ""}
            onValueChange={(agentId) => {
              if (agentId) setExtra(withAcpRegistryAgent(extra, agentId));
            }}
            options={options}
            placeholder={selectedAgentId ?? "Select an ACP agent..."}
            searchPlaceholder="Search ACP agents..."
            disabled={!canSelect || (isFetching && !registry.data)}
            isLoading={isFetching}
            clearable={false}
          />
        </div>
        <Button
          type="button"
          variant="outline"
          disabled={isFetching}
          onClick={() => refresh.mutate()}
        >
          Refresh
        </Button>
      </div>
      {error ? (
        <p role="alert" className="text-xs text-destructive">
          {getApiErrorMessage(error)}
        </p>
      ) : null}
      {!canSelect ? (
        <p className="text-xs text-muted-foreground">
          Fix the Extra Settings JSON before selecting an agent.
        </p>
      ) : null}
      <p className="text-xs text-muted-foreground">
        The agent runs from the hosts' ACP install directory. Install it with &quot;
        <code>acp install &lt;id&gt;</code>&quot; when building the image.
      </p>
    </div>
  );
}
