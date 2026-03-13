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
  ModelProviderApiKeyDto,
  McpToolServerDto,
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
  modelProviderApiKeyId: string;
  setModelProviderApiKeyId: (value: string) => void;
  agentType: string;
  setAgentType: (value: string) => void;
  extra: string;
  setExtra: (value: string) => void;
  selectedTools: string[];
  setSelectedTools: React.Dispatch<React.SetStateAction<string[]>>;
  toolSearchTerm: string;
  setToolSearchTerm: (value: string) => void;
  filteredTools: ToolInfo[];
  modelProviderApiKeysQuery: UseQueryResult<ModelProviderApiKeyDto[], Error>;
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
  modelProviderApiKeyId,
  setModelProviderApiKeyId,
  agentType,
  setAgentType,
  extra,
  setExtra,
  selectedTools,
  setSelectedTools,
  toolSearchTerm,
  setToolSearchTerm,
  filteredTools,
  modelProviderApiKeysQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  mcpToolServerSearchTerm,
  setMcpToolServerSearchTerm,
  filteredMcpToolServers,
  updateAgentMutation,
  toggleTool,
  toggleMcpToolServer,
}: EditAgentDialogProps) {
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent className="max-w-2xl max-h-[90vh] flex flex-col">
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
          modelProviderApiKeyId={modelProviderApiKeyId}
          setModelProviderApiKeyId={setModelProviderApiKeyId}
          agentType={agentType}
          setAgentType={setAgentType}
          extra={extra}
          setExtra={setExtra}
          selectedTools={selectedTools}
          setSelectedTools={setSelectedTools}
          toolSearchTerm={toolSearchTerm}
          setToolSearchTerm={setToolSearchTerm}
          filteredTools={filteredTools}
          modelProviderApiKeysQuery={modelProviderApiKeysQuery}
          toolsQuery={toolsQuery}
          mcpToolServersQuery={mcpToolServersQuery}
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
                    modelProviderApiKeyId,
                    tools:
                      selectedTools.length > 0
                        ? JSON.stringify(selectedTools)
                        : null,
                    mcpToolServerIds:
                      selectedMcpToolServerIds.length > 0
                        ? selectedMcpToolServerIds
                        : null,
                  },
                });
              }
            }}
            disabled={
              !displayName.trim() ||
              (agentType === "0" && !modelProviderApiKeyId?.trim()) ||
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
