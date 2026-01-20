import * as React from "react";
import { UseQueryResult } from "@tanstack/react-query";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type { ToolInfo, ModelProviderApiKeyDto } from "./types";

interface AgentFormFieldsProps {
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
  toggleTool: (toolName: string) => void;
  idPrefix?: string;
}

export function AgentFormFields({
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
  toolSearchTerm,
  setToolSearchTerm,
  filteredTools,
  modelProviderApiKeysQuery,
  toolsQuery,
  toggleTool,
  idPrefix = "",
}: AgentFormFieldsProps) {
  return (
    <div className="grid gap-4 overflow-y-auto pr-2 -mr-2">
      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}name`}>Name</Label>
        <Input
          id={`${idPrefix}name`}
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="demo-agent"
        />
      </div>

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}description`}>Description</Label>
        <Input
          id={`${idPrefix}description`}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          placeholder="Agent description..."
        />
      </div>

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}modelProviderApiKeyId`}>
          Model Provider API Key
        </Label>
        <Select
          value={modelProviderApiKeyId}
          onValueChange={setModelProviderApiKeyId}
        >
          <SelectTrigger id={`${idPrefix}modelProviderApiKeyId`} className="w-full">
            <SelectValue placeholder="Select an API key..." />
          </SelectTrigger>
          <SelectContent position="popper" sideOffset={4}>
            <SelectGroup>
              <SelectLabel>Available API Keys</SelectLabel>
              {modelProviderApiKeysQuery.isLoading ? (
                <SelectItem value="loading" disabled>
                  Loading...
                </SelectItem>
              ) : modelProviderApiKeysQuery.data &&
                modelProviderApiKeysQuery.data.length > 0 ? (
                modelProviderApiKeysQuery.data.map((key) => (
                  <SelectItem key={key.id} value={key.id}>
                    {key.apiKeyName} (Model: {key.modelName}, Provider:{" "}
                    {key.providerName})
                  </SelectItem>
                ))
              ) : (
                <SelectItem value="no-keys" disabled>
                  No API keys available
                </SelectItem>
              )}
            </SelectGroup>
          </SelectContent>
        </Select>
      </div>

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}agentType`}>Agent Type</Label>
        <Select value={agentType} onValueChange={setAgentType}>
          <SelectTrigger id={`${idPrefix}agentType`} className="w-full">
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

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}extra`}>Extra (JSON)</Label>
        <Textarea
          id={`${idPrefix}extra`}
          value={extra}
          onChange={(e) => setExtra(e.target.value)}
          placeholder='{"env": {"VAR_NAME": "value"}}'
          rows={3}
        />
        <p className="text-xs text-muted-foreground">
          JSON object for additional data (e.g., environment variables)
        </p>
      </div>

      <div className="grid gap-2">
        <Label htmlFor={`${idPrefix}systemPrompt`}>System prompt</Label>
        <Textarea
          id={`${idPrefix}systemPrompt`}
          value={systemPrompt}
          onChange={(e) => setSystemPrompt(e.target.value)}
          rows={4}
        />
      </div>

      <div className="grid gap-2">
        <Label>Tools</Label>
        <Input
          placeholder="Search tools..."
          value={toolSearchTerm}
          onChange={(e) => setToolSearchTerm(e.target.value)}
          className="mb-2"
        />
        <div className="border rounded-md p-3 max-h-48 overflow-y-auto space-y-2">
          {toolsQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">
              Loading tools...
            </div>
          ) : filteredTools.length === 0 ? (
            <div className="text-sm text-muted-foreground">No tools found</div>
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
        <p className="text-xs text-muted-foreground mt-1">
          {selectedTools.length} tool(s) selected
        </p>
      </div>
    </div>
  );
}
