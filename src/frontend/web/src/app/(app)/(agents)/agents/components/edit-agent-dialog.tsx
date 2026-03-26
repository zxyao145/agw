import * as React from "react";
import { UseMutationResult, UseQueryResult } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { AgentFormFields } from "./agent-form-fields";
import type {
  AgentDto,
  AgentUpdateRequest,
  ToolInfo,
  ModelProviderDto,
  McpToolServerDto,
  SkillDto,
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
  agentType: string;
  setAgentType: (value: string) => void;
  extra: string;
  setExtra: (value: string) => void;
  selectedSkillIds: string[];
  selectedTools: string[];
  setSelectedTools: React.Dispatch<React.SetStateAction<string[]>>;
  toolSearchTerm: string;
  setToolSearchTerm: (value: string) => void;
  filteredTools: ToolInfo[];
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  mcpToolServerSearchTerm: string;
  setMcpToolServerSearchTerm: (value: string) => void;
  filteredMcpToolServers: McpToolServerDto[];
  updateAgentMutation: UseMutationResult<
    unknown,
    Error,
    { id: string; body: AgentUpdateRequest },
    unknown
  >;
  toggleSkill: (skillId: string) => void;
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
  agentType,
  setAgentType,
  extra,
  setExtra,
  selectedSkillIds,
  selectedTools,
  setSelectedTools,
  toolSearchTerm,
  setToolSearchTerm,
  filteredTools,
  modelProvidersQuery,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  mcpToolServerSearchTerm,
  setMcpToolServerSearchTerm,
  filteredMcpToolServers,
  updateAgentMutation,
  toggleSkill,
  toggleTool,
  toggleMcpToolServer,
}: EditAgentDialogProps) {
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent size="xl" className="max-h-[90vh] flex flex-col">
        <DialogHeader>
          <DialogTitle>Edit agent</DialogTitle>
          <DialogDescription>Update the agent configuration</DialogDescription>
        </DialogHeader>

        <AgentFormFields
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
          agentType={agentType}
          setAgentType={setAgentType}
          extra={extra}
          setExtra={setExtra}
          selectedSkillIds={selectedSkillIds}
          selectedTools={selectedTools}
          setSelectedTools={setSelectedTools}
          toolSearchTerm={toolSearchTerm}
          setToolSearchTerm={setToolSearchTerm}
          filteredTools={filteredTools}
          modelProvidersQuery={modelProvidersQuery}
          skillsQuery={skillsQuery}
          toolsQuery={toolsQuery}
          mcpToolServersQuery={mcpToolServersQuery}
          toggleSkill={toggleSkill}
          toggleTool={toggleTool}
          selectedMcpToolServerIds={selectedMcpToolServerIds}
          mcpToolServerSearchTerm={mcpToolServerSearchTerm}
          setMcpToolServerSearchTerm={setMcpToolServerSearchTerm}
          filteredMcpToolServers={filteredMcpToolServers}
          toggleMcpToolServer={toggleMcpToolServer}
          idPrefix="edit-"
          disabledFields={
            agentType === "1"
              ? {
                  name: true,
                  systemPrompt: true,
                  agentType: true,
                  skills: true,
                  tools: true,
                }
              : {
                  name: true,
                }
          }
          hiddenFields={
            agentType === "1"
              ? {
                  name: true,
                  systemPrompt: true,
                  agentType: true,
                  skills: true,
                  tools: true,
                }
              : {
                  name: true,
                }
          }
        />

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>
          <Button
            type="button"
            onClick={() => {
              if (editingAgent) {
                updateAgentMutation.mutate({
                  id: editingAgent.id,
                  body: {
                    displayName,
                    description,
                    systemPrompt,
                    modelProviderId,
                    tools: selectedTools.length > 0 ? JSON.stringify(selectedTools) : null,
                    skillIds: selectedSkillIds.length > 0 ? selectedSkillIds : null,
                    mcpToolServerIds:
                      selectedMcpToolServerIds.length > 0 ? selectedMcpToolServerIds : null,
                  },
                });
              }
            }}
            disabled={
              !displayName.trim() ||
              (agentType === "0" && !modelProviderId?.trim()) ||
              updateAgentMutation.isPending
            }
          >
            {updateAgentMutation.isPending ? "Updating..." : "Update"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
