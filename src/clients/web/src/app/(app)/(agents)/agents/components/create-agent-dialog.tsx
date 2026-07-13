import * as React from "react";
import { UseMutationResult, UseQueryResult } from "@tanstack/react-query";

import { applyDialogOpenChange } from "@/components/definition-capabilities";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";

import { AgentFormFields } from "./agent-form-fields";
import {
  getAgentEnvironmentVariablesError,
  normalizeAgentEnvironmentVariables,
  type AgentEnvironmentVariableEntry,
} from "./agent-environment-variables";
import type { AppInstanceOption } from "./app-selector";
import type {
  AgentCreateRequest,
  McpToolServerDto,
  ModelProviderDto,
  SkillDto,
  ToolInfo,
} from "./types";

interface CreateAgentDialogProps {
  open: boolean;
  setOpen: (open: boolean) => void;
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
  environmentVariables: AgentEnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: AgentEnvironmentVariableEntry[]) => void;
  selectedSkillIds: string[];
  appOptions: AppInstanceOption[];
  selectedAppInstanceIds: string[];
  appSearchTerm: string;
  setAppSearchTerm: (value: string) => void;
  filteredAppOptions: AppInstanceOption[];
  selectedTools: string[];
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  createAgentMutation: UseMutationResult<unknown, Error, AgentCreateRequest, unknown>;
  toggleSkill: (skillId: string) => void;
  toggleAppInstance: (appInstanceId: string) => void;
  toggleTool: (toolName: string) => void;
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
  environmentVariables,
  setEnvironmentVariables,
  selectedSkillIds,
  appOptions,
  selectedAppInstanceIds,
  appSearchTerm,
  setAppSearchTerm,
  filteredAppOptions,
  selectedTools,
  modelProvidersQuery,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  createAgentMutation,
  toggleSkill,
  toggleAppInstance,
  toggleTool,
  toggleMcpToolServer,
}: CreateAgentDialogProps) {
  const [dialogPortalContainer, setDialogPortalContainer] = React.useState<HTMLDivElement | null>(
    null,
  );
  const environmentVariablesError = getAgentEnvironmentVariablesError(environmentVariables);

  const handleCreate = () => {
    createAgentMutation.mutate({
      displayName,
      name: name.trim(),
      description,
      systemPrompt,
      modelProviderId,
      summaryModelProviderId: summaryModelProviderId || null,
      enableSummary,
      tools: selectedTools.length > 0 ? JSON.stringify(selectedTools) : null,
      skillIds: selectedSkillIds.length > 0 ? selectedSkillIds : null,
      mcpToolServerIds: selectedMcpToolServerIds.length > 0 ? selectedMcpToolServerIds : null,
      appInstanceIds: selectedAppInstanceIds.length > 0 ? selectedAppInstanceIds : null,
      environmentVariables: normalizeAgentEnvironmentVariables(environmentVariables),
    });
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
        ref={setDialogPortalContainer}
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-4">
            <div className="flex items-start justify-between gap-4">
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
                    !displayName.trim() ||
                    !modelProviderId.trim() ||
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
            dialogPortalContainer={dialogPortalContainer}
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
            agentType="0"
            extra=""
            environmentVariables={environmentVariables}
            setEnvironmentVariables={setEnvironmentVariables}
            selectedSkillIds={selectedSkillIds}
            appOptions={appOptions}
            selectedAppInstanceIds={selectedAppInstanceIds}
            appSearchTerm={appSearchTerm}
            setAppSearchTerm={setAppSearchTerm}
            filteredAppOptions={filteredAppOptions}
            toggleAppInstance={toggleAppInstance}
            selectedTools={selectedTools}
            modelProvidersQuery={modelProvidersQuery}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            toggleSkill={toggleSkill}
            toggleTool={toggleTool}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            toggleMcpToolServer={toggleMcpToolServer}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
