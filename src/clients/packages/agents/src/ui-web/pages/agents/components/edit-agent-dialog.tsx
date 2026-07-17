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

import { getAgentExtraSettingsError, normalizeAgentExtraSettings } from "./agent-extra-settings";
import {
  getAgentEnvironmentVariablesError,
  normalizeAgentEnvironmentVariables,
  type AgentEnvironmentVariableEntry,
} from "./agent-environment-variables";
import { AgentFormFields } from "./agent-form-fields";
import type { ConnectionOption } from "./connection-selector";
import type {
  AgentDto,
  AgentUpdateRequest,
  McpToolServerDto,
  ModelProviderDto,
  SkillDto,
  ToolInfo,
} from "./types";

interface EditAgentDialogProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  editingAgent: AgentDto | null;
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
  environmentVariables: AgentEnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: AgentEnvironmentVariableEntry[]) => void;
  selectedSkillIds: string[];
  connectionOptions: ConnectionOption[];
  selectedConnectionIds: string[];
  selectedTools: string[];
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
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
  toggleTool: (toolName: string) => void;
  toggleMcpToolServer: (mcpToolServerId: string) => void;
}

export function EditAgentDialog({
  open,
  setOpen,
  editingAgent,
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
  environmentVariables,
  setEnvironmentVariables,
  selectedSkillIds,
  connectionOptions,
  selectedConnectionIds,
  selectedTools,
  modelProvidersQuery,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  updateAgentMutation,
  toggleSkill,
  toggleConnection,
  toggleTool,
  toggleMcpToolServer,
}: EditAgentDialogProps) {
  const isExternalAgent = editingAgent?.type === 1;
  const effectiveSummaryModelProviderId = isExternalAgent
    ? summaryModelProviderId
    : summaryModelProviderId || modelProviderId;
  const extraError = isExternalAgent ? getAgentExtraSettingsError(extra) : null;
  const environmentVariablesError = getAgentEnvironmentVariablesError(environmentVariables);

  const handleUpdate = () => {
    if (!editingAgent) {
      return;
    }

    const body: AgentUpdateRequest = isExternalAgent
      ? {
          displayName,
          description,
          modelProviderId: modelProviderId || null,
          extra: normalizeAgentExtraSettings(extra),
          environmentVariables: normalizeAgentEnvironmentVariables(environmentVariables),
        }
      : {
          displayName,
          description,
          systemPrompt,
          modelProviderId,
          summaryModelProviderId: summaryModelProviderId || null,
          enableSummary,
          tools: selectedTools.length > 0 ? JSON.stringify(selectedTools) : null,
          skillIds: selectedSkillIds.length > 0 ? selectedSkillIds : null,
          mcpToolServerIds: selectedMcpToolServerIds.length > 0 ? selectedMcpToolServerIds : null,
          connectionIds: selectedConnectionIds.length > 0 ? selectedConnectionIds : null,
          extra: null,
          environmentVariables: normalizeAgentEnvironmentVariables(environmentVariables),
        };

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
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-4">
            <div className="flex items-start justify-between gap-4">
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
                    Boolean(extraError) ||
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
            agentType={editingAgent?.type.toString() ?? "0"}
            extra={extra}
            setExtra={setExtra}
            environmentVariables={environmentVariables}
            setEnvironmentVariables={setEnvironmentVariables}
            selectedSkillIds={selectedSkillIds}
            connectionOptions={connectionOptions}
            selectedConnectionIds={selectedConnectionIds}
            toggleConnection={toggleConnection}
            selectedTools={selectedTools}
            modelProvidersQuery={modelProvidersQuery}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            toggleSkill={toggleSkill}
            toggleTool={toggleTool}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            toggleMcpToolServer={toggleMcpToolServer}
            idPrefix="edit-"
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
