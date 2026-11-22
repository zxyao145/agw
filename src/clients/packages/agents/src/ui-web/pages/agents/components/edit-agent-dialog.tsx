import { UseMutationResult, UseQueryResult } from "@agw/components/query";

import { applyDialogOpenChange } from "@agw/integrations";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import { normalizeAgentExtraSettings } from "./agent-extra-settings";
import { normalizeAgentResponseSchema } from "./agent-response-schema";
import { getAgentSettingsErrors } from "./agent-settings-errors";
import {
  normalizeAgentEnvironmentVariables,
  type AgentEnvironmentVariableEntry,
} from "./agent-environment-variables";
import { AgentFormFields } from "./agent-form-fields";
import {
  createA2AEndpointDraft,
  getA2AEndpointError,
  isA2AAgent,
  toA2AEndpointRequest,
  type A2AEndpointDraft,
} from "./a2a-endpoint";
import { getExternalModelProviderError } from "./external-model-provider";
import type { ConnectionOption } from "./connection-selector";
import { AgentType } from "./types";
import type {
  A2AEndpointDto,
  AgentDto,
  AgentUpdateRequest,
  ExternalAgentOptionDto,
  McpToolServerDto,
  ModelProviderDto,
  SkillDto,
  ToolLiteInfo,
  ToolValueObject,
} from "./types";

interface EditAgentDialogProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  editingAgent: AgentDto | null;
  a2aEndpointDraft: A2AEndpointDraft | null;
  setA2AEndpointDraft: (draft: A2AEndpointDraft) => void;
  onA2AEndpointRefreshed: (agentId: string, endpoint: A2AEndpointDto) => void;
  displayName: string;
  setDisplayName: (value: string) => void;
  name: string;
  setName: (value: string) => void;
  description: string;
  setDescription: (value: string) => void;
  systemPrompt: string;
  setSystemPrompt: (value: string) => void;
  modelProviderId: string;
  setModelProviderId: (value: string) => void;
  summaryModelProviderId: string;
  setSummaryModelProviderId: (value: string) => void;
  enableSummary: boolean;
  setEnableSummary: (value: boolean) => void;
  extra: string;
  setExtra: (value: string) => void;
  responseSchema: string;
  setResponseSchema: (value: string) => void;
  environmentVariables: AgentEnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: AgentEnvironmentVariableEntry[]) => void;
  selectedSkillIds: string[];
  connectionOptions: ConnectionOption[];
  selectedConnectionIds: string[];
  tools: ToolValueObject[];
  setTools: (value: ToolValueObject[]) => void;
  agentOptions: Array<{ id: string; name: string; displayName?: string }>;
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  externalAgentOptionsQuery: UseQueryResult<ExternalAgentOptionDto[], Error>;
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolLiteInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  updateAgentMutation: UseMutationResult<
    unknown,
    Error,
    { id: string; body: AgentUpdateRequest },
    unknown
  >;
  toggleSkill: (skillId: string) => void;
  toggleConnection: (connectionId: string) => void;
  toggleMcpToolServer: (mcpToolServerId: string) => void;
}

export function EditAgentDialog({
  open,
  setOpen,
  editingAgent,
  a2aEndpointDraft,
  setA2AEndpointDraft,
  onA2AEndpointRefreshed,
  displayName,
  setDisplayName,
  name,
  setName,
  description,
  setDescription,
  systemPrompt,
  setSystemPrompt,
  modelProviderId,
  setModelProviderId,
  summaryModelProviderId,
  setSummaryModelProviderId,
  enableSummary,
  setEnableSummary,
  extra,
  setExtra,
  responseSchema,
  setResponseSchema,
  environmentVariables,
  setEnvironmentVariables,
  selectedSkillIds,
  connectionOptions,
  selectedConnectionIds,
  tools,
  setTools,
  agentOptions,
  modelProvidersQuery,
  externalAgentOptionsQuery,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  updateAgentMutation,
  toggleSkill,
  toggleConnection,
  toggleMcpToolServer,
}: EditAgentDialogProps) {
  const isExternalAgent = editingAgent?.type === AgentType.External;
  const isA2A = isA2AAgent(editingAgent?.type, editingAgent?.externalAgentKind);
  const storedEndpoint = editingAgent?.a2aEndpoint;
  const endpoint = a2aEndpointDraft ?? createA2AEndpointDraft(storedEndpoint);
  const endpointError = isA2A ? getA2AEndpointError(endpoint) : null;
  const effectiveSummaryModelProviderId = isExternalAgent
    ? summaryModelProviderId
    : summaryModelProviderId || modelProviderId;
  const modelProviderError = isExternalAgent
    ? getExternalModelProviderError(
        editingAgent?.externalAgentKind ?? 0,
        modelProviderId,
        modelProvidersQuery.data,
        externalAgentOptionsQuery.data,
      )
    : null;
  const { extraError, responseSchemaError, environmentVariablesError } = getAgentSettingsErrors({
    isExternalAgent,
    isA2A,
    extra,
    responseSchema,
    environmentVariables,
  });

  const handleUpdate = () => {
    if (!editingAgent) {
      return;
    }

    const metadata = { displayName, description };
    let body: AgentUpdateRequest;
    if (isA2A) {
      body = a2aEndpointDraft
        ? { ...metadata, a2aEndpoint: toA2AEndpointRequest(a2aEndpointDraft) }
        : metadata;
    } else {
      const settings = {
        ...metadata,
        environmentVariables: normalizeAgentEnvironmentVariables(environmentVariables),
        responseSchema: normalizeAgentResponseSchema(responseSchema),
      };
      body = isExternalAgent
        ? {
            ...settings,
            modelProviderId: modelProviderId || null,
            extra: normalizeAgentExtraSettings(extra),
          }
        : {
            ...settings,
            systemPrompt,
            modelProviderId,
            summaryModelProviderId: summaryModelProviderId || null,
            enableSummary,
            tools,
            skillIds: selectedSkillIds.length > 0 ? selectedSkillIds : null,
            mcpToolServerIds: selectedMcpToolServerIds.length > 0 ? selectedMcpToolServerIds : null,
            connectionIds: selectedConnectionIds.length > 0 ? selectedConnectionIds : null,
            extra: null,
          };
    }

    updateAgentMutation.mutate({
      id: editingAgent.id,
      body,
    });
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) =>
        applyDialogOpenChange({
          isPending: updateAgentMutation.isPending,
          nextOpen,
          setOpen,
        })
      }
    >
      <DialogContent
        size="fullscreen"
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex h-full min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-2">
            <div className="flex items-center justify-between gap-4">
              <div className="min-w-0">
                <DialogTitle>Edit agent</DialogTitle>
                <DialogDescription className="mt-1">
                  Update the agent metadata, instructions, and available capabilities.
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <DialogClose asChild>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={updateAgentMutation.isPending}
                  >
                    Cancel
                  </Button>
                </DialogClose>
                <Button
                  type="button"
                  size="sm"
                  onClick={handleUpdate}
                  disabled={
                    !editingAgent ||
                    (!isExternalAgent &&
                      (!displayName.trim() ||
                        !modelProviderId.trim() ||
                        (enableSummary && !effectiveSummaryModelProviderId))) ||
                    Boolean(modelProviderError) ||
                    Boolean(endpointError) ||
                    Boolean(extraError) ||
                    Boolean(responseSchemaError) ||
                    Boolean(environmentVariablesError) ||
                    updateAgentMutation.isPending
                  }
                >
                  {updateAgentMutation.isPending ? "Updating..." : "Update"}
                </Button>
              </div>
            </div>
          </DialogHeader>

          <AgentFormFields
            mode="edit"
            displayName={displayName}
            setDisplayName={setDisplayName}
            name={name}
            setName={setName}
            description={description}
            setDescription={setDescription}
            systemPrompt={systemPrompt}
            setSystemPrompt={setSystemPrompt}
            modelProviderId={modelProviderId}
            setModelProviderId={setModelProviderId}
            summaryModelProviderId={summaryModelProviderId}
            setSummaryModelProviderId={setSummaryModelProviderId}
            enableSummary={enableSummary}
            setEnableSummary={setEnableSummary}
            agentType={editingAgent?.type ?? 0}
            externalAgentKind={editingAgent?.externalAgentKind ?? 0}
            a2aEndpoint={{
              value: endpoint,
              onChange: setA2AEndpointDraft,
              storedEndpoint,
              agentId: editingAgent?.id,
              dirty: a2aEndpointDraft !== null,
              disabled: updateAgentMutation.isPending,
              onRefresh: (refreshed) => {
                if (editingAgent) onA2AEndpointRefreshed(editingAgent.id, refreshed);
              },
            }}
            externalAgentOptionsQuery={externalAgentOptionsQuery}
            extra={extra}
            setExtra={setExtra}
            responseSchema={responseSchema}
            setResponseSchema={setResponseSchema}
            environmentVariables={environmentVariables}
            setEnvironmentVariables={setEnvironmentVariables}
            selectedSkillIds={selectedSkillIds}
            connectionOptions={connectionOptions}
            selectedConnectionIds={selectedConnectionIds}
            toggleConnection={toggleConnection}
            tools={tools}
            setTools={setTools}
            agentOptions={agentOptions.filter((agent) => agent.id !== editingAgent?.id)}
            modelProvidersQuery={modelProvidersQuery}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            toggleSkill={toggleSkill}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            toggleMcpToolServer={toggleMcpToolServer}
            idPrefix="edit-"
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
