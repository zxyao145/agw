import * as React from "react";
import { UseQueryResult } from "@tanstack/react-query";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Checkbox } from "@/components/ui/checkbox";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { ToolInfo, ModelProviderDto, McpToolServerDto } from "./types";

interface AgentFormFieldsProps {
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
  selectedTools: string[];
  setSelectedTools: React.Dispatch<React.SetStateAction<string[]>>;
  toolSearchTerm: string;
  setToolSearchTerm: (value: string) => void;
  filteredTools: ToolInfo[];
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  toggleTool: (toolName: string) => void;
  selectedMcpToolServerIds: string[];
  mcpToolServerSearchTerm: string;
  setMcpToolServerSearchTerm: (value: string) => void;
  filteredMcpToolServers: McpToolServerDto[];
  toggleMcpToolServer: (mcpToolServerId: string) => void;
  idPrefix?: string;
  disabledFields?: {
    displayName?: boolean;
    name?: boolean;
    description?: boolean;
    systemPrompt?: boolean;
    modelProviderId?: boolean;
    agentType?: boolean;
    extra?: boolean;
    tools?: boolean;
    mcpToolServers?: boolean;
  };
  hiddenFields?: {
    displayName?: boolean;
    name?: boolean;
    description?: boolean;
    systemPrompt?: boolean;
    modelProviderId?: boolean;
    agentType?: boolean;
    extra?: boolean;
    tools?: boolean;
    mcpToolServers?: boolean;
  };
}

export function AgentFormFields({
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
  selectedTools,
  toolSearchTerm,
  setToolSearchTerm,
  filteredTools,
  modelProvidersQuery,
  toolsQuery,
  mcpToolServersQuery,
  toggleTool,
  selectedMcpToolServerIds,
  mcpToolServerSearchTerm,
  setMcpToolServerSearchTerm,
  filteredMcpToolServers,
  toggleMcpToolServer,
  idPrefix = "",
  disabledFields = {},
  hiddenFields = {},
}: AgentFormFieldsProps) {
  return (
    <div className="grid gap-4 overflow-y-auto pr-2 -mr-2">
      {!hiddenFields.displayName && (
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}displayName`}>Display Name</Label>
          <Input
            id={`${idPrefix}displayName`}
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            placeholder="Agent display name"
            disabled={disabledFields.displayName}
          />
        </div>
      )}

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}name`}>Id (Optional)</Label>
        <Input
          id={`${idPrefix}name`}
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="agent id"
          disabled={disabledFields.name}
        />
      </div>

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}description`}>Description</Label>
        <Input
          id={`${idPrefix}description`}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          placeholder="Agent description..."
          disabled={disabledFields.description}
        />
      </div>

      {!hiddenFields.agentType && (
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}agentType`}>Agent Type</Label>
          <Select
            value={agentType}
            onValueChange={setAgentType}
            disabled={disabledFields.agentType}
          >
            <SelectTrigger
              id={`${idPrefix}agentType`}
              className="w-full"
              disabled={disabledFields.agentType}
            >
              <SelectValue placeholder="Select agent type..." />
            </SelectTrigger>
            <SelectContent position="popper" sideOffset={4}>
              <SelectGroup>
                <SelectLabel>Agent Type</SelectLabel>
                <SelectItem value="0">System</SelectItem>
                <SelectItem value="1">External</SelectItem>
              </SelectGroup>
            </SelectContent>
          </Select>
        </div>
      )}

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}modelProviderId`}>
          Model Provider
          {agentType === "1" && (
            <span className="text-xs text-muted-foreground ml-2">
              (Optional)
            </span>
          )}
        </Label>
        <Select
          value={modelProviderId}
          onValueChange={setModelProviderId}
          disabled={disabledFields.modelProviderId}
        >
          <SelectTrigger
            id={`${idPrefix}modelProviderId`}
            className="w-full"
            disabled={disabledFields.modelProviderId}
          >
            <SelectValue
              placeholder={
                agentType === "1"
                  ? "Optional: Select a model provider..."
                  : "Select a model provider..."
              }
            />
          </SelectTrigger>
          <SelectContent position="popper" sideOffset={4}>
            <SelectGroup>
              <SelectLabel>Available Model Providers</SelectLabel>
              {modelProvidersQuery.isLoading ? (
                <SelectItem value="loading" disabled>
                  Loading...
                </SelectItem>
              ) : modelProvidersQuery.data &&
                modelProvidersQuery.data.length > 0 ? (
                modelProvidersQuery.data.map((mp) => (
                  <SelectItem key={mp.id} value={mp.id}>
                    {mp.modelName} ({mp.providerName})
                  </SelectItem>
                ))
              ) : (
                <SelectItem value="no-providers" disabled>
                  No model providers available
                </SelectItem>
              )}
            </SelectGroup>
          </SelectContent>
        </Select>
      </div>

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}extra`}>Extra (JSON)</Label>
        <Textarea
          id={`${idPrefix}extra`}
          value={extra}
          onChange={(e) => setExtra(e.target.value)}
          placeholder='{"env": {"VAR_NAME": "value"}}'
          rows={3}
          disabled={disabledFields.extra}
        />
        <p className="text-xs text-muted-foreground">
          JSON object for additional data (e.g., environment variables)
        </p>
      </div>

      {!hiddenFields.systemPrompt && (
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}systemPrompt`}>System prompt</Label>
          <Textarea
            id={`${idPrefix}systemPrompt`}
            value={systemPrompt}
            onChange={(e) => setSystemPrompt(e.target.value)}
            rows={4}
            disabled={disabledFields.systemPrompt}
          />
        </div>
      )}

      {!hiddenFields.tools && (
        <div className="grid gap-2">
          <Label>Tools</Label>
          <Input
            placeholder="Search tools..."
            value={toolSearchTerm}
            onChange={(e) => setToolSearchTerm(e.target.value)}
            disabled={disabledFields.tools}
          />
          <div
            className={`rounded-md px-2 max-h-48 overflow-y-auto space-y-2 ${disabledFields.tools ? "opacity-50 pointer-events-none" : ""}`}
          >
            {toolsQuery.isLoading ? (
              <div className="text-sm text-muted-foreground">
                Loading tools...
              </div>
            ) : filteredTools.length === 0 ? (
              <div className="text-sm text-muted-foreground">
                No tools found
              </div>
            ) : (
              filteredTools.map((tool) => (
                <div key={tool.name} className="flex items-start space-x-2">
                  <Checkbox
                    id={`${idPrefix}tool-${tool.name}`}
                    checked={selectedTools.includes(tool.name)}
                    onCheckedChange={() => toggleTool(tool.name)}
                  />
                  <div className="grid gap-1 leading-none">
                    <label
                      htmlFor={`${idPrefix}tool-${tool.name}`}
                      className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70 cursor-pointer"
                    >
                      {tool.name}
                      <span className="ml-2 text-xs text-muted-foreground">
                        ({tool.category})
                      </span>
                    </label>
                    <p className="text-xs text-muted-foreground">
                      {tool.description}
                    </p>
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      )}

      {!hiddenFields.mcpToolServers && (
        <div className="grid gap-2">
          <Label>MCP Tool Servers</Label>
          <Input
            placeholder="Search MCP tool servers..."
            value={mcpToolServerSearchTerm}
            onChange={(e) => setMcpToolServerSearchTerm(e.target.value)}
            className="mb-2"
            disabled={disabledFields.mcpToolServers}
          />
          <Select
            onValueChange={(value) => toggleMcpToolServer(value)}
            disabled={disabledFields.mcpToolServers}
          >
            <SelectTrigger
              id={`${idPrefix}mcpToolServers`}
              className="w-full"
              disabled={disabledFields.mcpToolServers}
            >
              <SelectValue placeholder="Select MCP tool server..." />
            </SelectTrigger>
            <SelectContent position="popper" sideOffset={4}>
              <SelectGroup>
                <SelectLabel>Available MCP Tool Servers</SelectLabel>
                {mcpToolServersQuery.isLoading ? (
                  <SelectItem value="loading" disabled>
                    Loading MCP tool servers...
                  </SelectItem>
                ) : filteredMcpToolServers.length === 0 ? (
                  <SelectItem value="none" disabled>
                    No MCP tool servers found
                  </SelectItem>
                ) : (
                  filteredMcpToolServers.map((server) => (
                    <SelectItem key={server.id} value={server.id}>
                      {server.name}
                    </SelectItem>
                  ))
                )}
              </SelectGroup>
            </SelectContent>
          </Select>
          <div className="rounded-md px-2">
            {selectedMcpToolServerIds.length === 0 ? (
              <div className="text-sm text-muted-foreground">
                No MCP Tool servers selected
              </div>
            ) : (
              <div className="flex flex-wrap gap-2">
                {selectedMcpToolServerIds.map((selectedId) => {
                  const selectedServer = mcpToolServersQuery.data?.find(
                    (server) => server.id === selectedId
                  );
                  return (
                    <Badge
                      key={selectedId}
                      variant="secondary"
                      className="flex items-center gap-1"
                    >
                      <span>{selectedServer?.name ?? selectedId}</span>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        className="h-4 w-4 p-0"
                        onClick={() => toggleMcpToolServer(selectedId)}
                        disabled={disabledFields.mcpToolServers}
                      >
                        x
                      </Button>
                    </Badge>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
