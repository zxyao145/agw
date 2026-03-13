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
  DialogTrigger,
} from "@/components/ui/dialog";
import { AgentFormFields } from "./agent-form-fields";
import type {
  AgentCreateRequest,
  ToolInfo,
  ModelProviderApiKeyDto,
  McpToolServerDto,
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
  createAgentMutation: UseMutationResult<unknown, Error, AgentCreateRequest, unknown>;
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
  createAgentMutation,
  toggleTool,
  toggleMcpToolServer,
}: CreateAgentDialogProps) {
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button>Create agent</Button>
      </DialogTrigger>

      <DialogContent className="max-w-2xl max-h-[90vh] flex flex-col">
        <DialogHeader>
          <DialogTitle>Create agent</DialogTitle>
          <DialogDescription>
            Uses <code>/api/tools</code> for available tools.
          </DialogDescription>
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
        />

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>
          <Button
            type="button"
            onClick={() =>
              createAgentMutation.mutate({
                displayName,
                name: name.trim(),
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
              })
            }
            disabled={
              !displayName.trim() ||
              (agentType === "0" && !modelProviderApiKeyId?.trim()) ||
              createAgentMutation.isPending
            }
          >
            {createAgentMutation.isPending ? "Creating..." : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
