import * as React from "react";
import { UseQueryResult } from "@tanstack/react-query";
import { ChevronDown } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import type {
  ToolInfo,
  ModelProviderDto,
  McpToolServerDto,
  SkillDto,
} from "./types";

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
  toggleSkill: (skillId: string) => void;
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
    skills?: boolean;
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
    skills?: boolean;
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
  selectedSkillIds,
  selectedTools,
  modelProvidersQuery,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  toggleSkill,
  toggleTool,
  selectedMcpToolServerIds,
  toggleMcpToolServer,
  idPrefix = "",
  disabledFields = {},
  hiddenFields = {},
}: AgentFormFieldsProps) {
  const selectedSkills = skillsQuery.data?.filter((skill) =>
    selectedSkillIds.includes(skill.id),
  );
  const groupedTools = React.useMemo(() => {
    if (!toolsQuery.data) {
      return [];
    }

    const groups = new Map<string, ToolInfo[]>();

    for (const tool of toolsQuery.data) {
      const category = tool.category.trim() || "Uncategorized";
      const existing = groups.get(category);

      if (existing) {
        existing.push(tool);
        continue;
      }

      groups.set(category, [tool]);
    }

    return Array.from(groups.entries()).sort(([left], [right]) =>
      left.localeCompare(right),
    );
  }, [toolsQuery.data]);
  const selectedSkillCount = selectedSkillIds.length;
  const selectedToolCount = selectedTools.length;
  const selectedMcpToolServerCount = selectedMcpToolServerIds.length;

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
        <Label htmlFor={`${idPrefix}name`}>Name (Optional)</Label>
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

      {!hiddenFields.skills && (
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}skills`}>Skills</Label>
          <DropdownMenu modal={false}>
            <DropdownMenuTrigger asChild>
              <Button
                id={`${idPrefix}skills`}
                type="button"
                variant="outline"
                className="w-full justify-between font-normal"
                disabled={disabledFields.skills}
              >
                <span className="truncate">
                  {selectedSkillCount > 0
                    ? `${selectedSkillCount} skill${selectedSkillCount === 1 ? "" : "s"} selected`
                    : "Select skills..."}
                </span>
                <ChevronDown className="size-4 opacity-50" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent
              align="start"
              className="w-[var(--radix-dropdown-menu-trigger-width)] max-h-72"
            >
              <DropdownMenuLabel>Available Skills</DropdownMenuLabel>
              {skillsQuery.isLoading ? (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">
                  Loading skills...
                </div>
              ) : skillsQuery.data && skillsQuery.data.length > 0 ? (
                skillsQuery.data.map((skill) => (
                  <DropdownMenuCheckboxItem
                    key={skill.id}
                    checked={selectedSkillIds.includes(skill.id)}
                    className="items-start"
                    onCheckedChange={() => toggleSkill(skill.id)}
                    onSelect={(event) => event.preventDefault()}
                  >
                    <div className="min-w-0">
                      <div className="truncate font-medium">{skill.name}</div>
                      {skill.description ? (
                        <div className="text-xs text-muted-foreground whitespace-normal break-words">
                          {skill.description}
                        </div>
                      ) : null}
                    </div>
                  </DropdownMenuCheckboxItem>
                ))
              ) : (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">
                  No skills found
                </div>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
          <div className="flex flex-wrap gap-2">
            {selectedSkills && selectedSkills.length > 0 ? (
              selectedSkills.map((skill) => (
                <Badge key={skill.id} variant="secondary">
                  {skill.name}
                </Badge>
              ))
            ) : (
              <p className="text-xs text-muted-foreground">
                No skills selected
              </p>
            )}
          </div>
        </div>
      )}

      {!hiddenFields.tools && (
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}tools`}>Tools</Label>
          <DropdownMenu modal={false}>
            <DropdownMenuTrigger asChild>
              <Button
                id={`${idPrefix}tools`}
                type="button"
                variant="outline"
                className="w-full justify-between font-normal"
                disabled={disabledFields.tools}
              >
                <span className="truncate">
                  {selectedToolCount > 0
                    ? `${selectedToolCount} tool${selectedToolCount === 1 ? "" : "s"} selected`
                    : "Select tools..."}
                </span>
                <ChevronDown className="size-4 opacity-50" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent
              align="start"
              className="w-[var(--radix-dropdown-menu-trigger-width)] max-h-72"
            >
              <DropdownMenuLabel>Available Tools</DropdownMenuLabel>
              {toolsQuery.isLoading ? (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">
                  Loading tools...
                </div>
              ) : groupedTools.length > 0 ? (
                groupedTools.map(([category, tools]) => (
                  <SelectGroup key={category}>
                    <SelectLabel>{category}</SelectLabel>
                    {tools.map((tool) => (
                      <DropdownMenuCheckboxItem
                        key={tool.name}
                        checked={selectedTools.includes(tool.name)}
                        className="items-start"
                        onCheckedChange={() => toggleTool(tool.name)}
                        onSelect={(event) => event.preventDefault()}
                      >
                        <div className="min-w-0">
                          <div className="truncate font-medium">
                            {tool.name}
                          </div>
                          {tool.description ? (
                            <div className="text-xs text-muted-foreground whitespace-normal break-words">
                              {tool.description}
                            </div>
                          ) : null}
                        </div>
                      </DropdownMenuCheckboxItem>
                    ))}
                  </SelectGroup>
                ))
              ) : (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">
                  No tools found
                </div>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
          <div className="flex flex-wrap gap-2">
            {selectedTools.length > 0 ? (
              selectedTools.map((toolName) => (
                <Badge key={toolName} variant="secondary">
                  {toolName}
                </Badge>
              ))
            ) : (
              <p className="text-xs text-muted-foreground">No tools selected</p>
            )}
          </div>
        </div>
      )}

      {!hiddenFields.mcpToolServers && (
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}mcpToolServers`}>MCP Tool Servers</Label>
          <DropdownMenu modal={false}>
            <DropdownMenuTrigger asChild>
              <Button
                id={`${idPrefix}mcpToolServers`}
                type="button"
                variant="outline"
                className="w-full justify-between font-normal"
                disabled={disabledFields.mcpToolServers}
              >
                <span className="truncate">
                  {selectedMcpToolServerCount > 0
                    ? `${selectedMcpToolServerCount} server${selectedMcpToolServerCount === 1 ? "" : "s"} selected`
                    : "Select MCP tool servers..."}
                </span>
                <ChevronDown className="size-4 opacity-50" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent
              align="start"
              className="w-[var(--radix-dropdown-menu-trigger-width)] max-h-72"
            >
              <DropdownMenuLabel>Available MCP Tool Servers</DropdownMenuLabel>
              {mcpToolServersQuery.isLoading ? (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">
                  Loading MCP tool servers...
                </div>
              ) : mcpToolServersQuery.data &&
                mcpToolServersQuery.data.length > 0 ? (
                mcpToolServersQuery.data.map((server) => (
                  <DropdownMenuCheckboxItem
                    key={server.id}
                    checked={selectedMcpToolServerIds.includes(server.id)}
                    onCheckedChange={() => toggleMcpToolServer(server.id)}
                    onSelect={(event) => event.preventDefault()}
                  >
                    {server.name}
                  </DropdownMenuCheckboxItem>
                ))
              ) : (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">
                  No MCP tool servers found
                </div>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
          <div className="flex flex-wrap gap-2">
            {selectedMcpToolServerIds.length > 0 ? (
              selectedMcpToolServerIds.map((selectedId) => {
                const selectedServer = mcpToolServersQuery.data?.find(
                  (server) => server.id === selectedId,
                );

                return (
                  <Badge key={selectedId} variant="secondary">
                    {selectedServer?.name ?? selectedId}
                  </Badge>
                );
              })
            ) : (
              <p className="text-xs text-muted-foreground">
                No MCP tool servers selected
              </p>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
