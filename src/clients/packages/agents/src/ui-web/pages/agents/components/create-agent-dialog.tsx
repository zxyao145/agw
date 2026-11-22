import type { Dispatch, SetStateAction } from "react";
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
  DialogTrigger,
} from "@agw/components";

import { AgentFormFields } from "./agent-form-fields";
import {
  getA2ACreateSettings,
  getA2AEndpointError,
  isA2AAgent,
  toA2AEndpointRequest,
  type A2AEndpointDraft,
} from "./a2a-endpoint";
import { getExternalModelProviderError } from "./external-model-provider";
import { normalizeAgentExtraSettings } from "./agent-extra-settings";
import { normalizeAgentResponseSchema } from "./agent-response-schema";
import { getAgentSettingsErrors } from "./agent-settings-errors";
import {
  normalizeAgentEnvironmentVariables,
  type AgentEnvironmentVariableEntry,
} from "./agent-environment-variables";
import type { ConnectionOption } from "./connection-selector";
import { AgentType, type ExternalAgentKind } from "./types";
import type {
  ExternalAgentOptionDto,
  AgentCreateRequest,
  McpToolServerDto,
  ModelProviderDto,
  SkillDto,
  ToolLiteInfo,
  ToolValueObject,
} from "./types";

interface CreateAgentDialogProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  displayName: string;
  setDisplayName: Dispatch<SetStateAction<string>>;
  name: string;
  setName: (value: string) => void;
  description: string;
  setDescription: Dispatch<SetStateAction<string>>;
  systemPrompt: string;
  setSystemPrompt: (value: string) => void;
  modelProviderId: string;
  setModelProviderId: (value: string) => void;
  summaryModelProviderId: string;
  setSummaryModelProviderId: (value: string) => void;
  enableSummary: boolean;
  setEnableSummary: (value: boolean) => void;
  agentType: AgentType;
  setAgentType: (value: AgentType) => void;
  externalAgentKind: ExternalAgentKind;
  setExternalAgentKind: (value: ExternalAgentKind) => void;
  a2aEndpoint: A2AEndpointDraft;
  a2aInterfaceUrl?: string;
  setA2AEndpoint: (draft: A2AEndpointDraft) => void;
  externalAgentOptionsQuery: UseQueryResult<ExternalAgentOptionDto[], Error>;
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
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolLiteInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  createAgentMutation: UseMutationResult<unknown, Error, AgentCreateRequest, unknown>;
  toggleSkill: (skillId: string) => void;
  toggleConnection: (connectionId: string) => void;
  toggleMcpToolServer: (mcpToolServerId: string) => void;
}

export function CreateAgentDialog({
  open,
  setOpen,
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
  agentType,
  setAgentType,
  externalAgentKind,
  setExternalAgentKind,
  a2aEndpoint,
  a2aInterfaceUrl,
  setA2AEndpoint,
  externalAgentOptionsQuery,
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
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  createAgentMutation,
  toggleSkill,
  toggleConnection,
  toggleMcpToolServer,
}: CreateAgentDialogProps) {
  const isExternalAgent = agentType === AgentType.External;
  const isA2A = isA2AAgent(agentType, externalAgentKind);
  const endpointError = isA2A ? getA2AEndpointError(a2aEndpoint) : null;
  const modelProviderError = isExternalAgent
    ? getExternalModelProviderError(
        externalAgentKind,
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

  const handleCreate = () => {
    const settings = isA2A
      ? { ...getA2ACreateSettings(), a2aEndpoint: toA2AEndpointRequest(a2aEndpoint) }
      : {
          modelProviderId: modelProviderId || null,
          environmentVariables: normalizeAgentEnvironmentVariables(environmentVariables),
          extra: isExternalAgent ? normalizeAgentExtraSettings(extra) : null,
          responseSchema: normalizeAgentResponseSchema(responseSchema),
        };
    const common = {
      displayName,
      name: name.trim(),
      description,
      type: agentType,
      externalAgentKind,
      ...settings,
    };
    const body: AgentCreateRequest = isExternalAgent
      ? {
          ...common,
          systemPrompt: "",
          summaryModelProviderId: null,
          enableSummary: false,
          tools: [],
          skillIds: null,
          mcpToolServerIds: null,
          connectionIds: null,
        }
      : {
          ...common,
          systemPrompt,
          summaryModelProviderId: summaryModelProviderId || null,
          enableSummary,
          tools,
          skillIds: selectedSkillIds.length > 0 ? selectedSkillIds : null,
          mcpToolServerIds: selectedMcpToolServerIds.length > 0 ? selectedMcpToolServerIds : null,
          connectionIds: selectedConnectionIds.length > 0 ? selectedConnectionIds : null,
        };
    createAgentMutation.mutate(body);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) =>
        applyDialogOpenChange({
          isPending: createAgentMutation.isPending,
          nextOpen,
          setOpen,
        })
      }
    >
      <DialogTrigger asChild>
        <Button>Create</Button>
      </DialogTrigger>

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
                <DialogTitle>Create agent</DialogTitle>
                <DialogDescription className="mt-1">
                  Define the agent metadata, instructions, and available capabilities.
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <DialogClose asChild>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={createAgentMutation.isPending}
                  >
                    Cancel
                  </Button>
                </DialogClose>
                <Button
                  type="button"
                  size="sm"
                  onClick={handleCreate}
                  disabled={
                    (!isExternalAgent && (!displayName.trim() || !modelProviderId.trim())) ||
                    (isExternalAgent && externalAgentKind === 0) ||
                    Boolean(endpointError) ||
                    Boolean(modelProviderError) ||
                    Boolean(extraError) ||
                    Boolean(responseSchemaError) ||
                    Boolean(environmentVariablesError) ||
                    createAgentMutation.isPending
                  }
                >
                  {createAgentMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </div>
            </div>
          </DialogHeader>

          <AgentFormFields
            mode="create"
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
            agentType={agentType}
            setAgentType={setAgentType}
            externalAgentKind={externalAgentKind}
            setExternalAgentKind={setExternalAgentKind}
            a2aEndpoint={{
              value: a2aEndpoint,
              interfaceUrl: a2aInterfaceUrl,
              onChange: setA2AEndpoint,
              disabled: createAgentMutation.isPending,
              onPreview: (cardName, cardDescription) => {
                setDisplayName((current) => (current.trim() ? current : cardName));
                setDescription((current) => (current.trim() ? current : cardDescription));
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
            agentOptions={agentOptions}
            modelProvidersQuery={modelProvidersQuery}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            toggleSkill={toggleSkill}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            toggleMcpToolServer={toggleMcpToolServer}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
