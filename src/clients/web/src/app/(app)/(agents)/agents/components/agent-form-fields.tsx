import * as React from "react";
import { UseQueryResult } from "@tanstack/react-query";
import { ChevronDown, X } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemGroup,
  ItemTitle,
} from "@/components/ui/item";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";

import { AgentEnvironmentVariablesEditor } from "./agent-environment-variables-editor";
import type { AgentEnvironmentVariableEntry } from "./agent-environment-variables";
import { getAgentExtraSettingsError } from "./agent-extra-settings";
import {
  buildAppOptionLabel,
  getAppAuthorizationState,
  type AppInstanceOption,
} from "./app-selector";
import type { McpToolServerDto, ModelProviderDto, SkillDto, ToolInfo } from "./types";

type AgentFormMode = "create" | "edit";

interface AgentFormFieldsProps {
  mode: AgentFormMode;
  dialogPortalContainer: HTMLElement | null;
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
  extra: string;
  setExtra?: (value: string) => void;
  environmentVariables: AgentEnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: AgentEnvironmentVariableEntry[]) => void;
  selectedSkillIds: string[];
  appOptions: AppInstanceOption[];
  selectedAppInstanceIds: string[];
  appSearchTerm: string;
  setAppSearchTerm: (value: string) => void;
  filteredAppOptions: AppInstanceOption[];
  toggleAppInstance: (appInstanceId: string) => void;
  selectedTools: string[];
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  toggleSkill: (skillId: string) => void;
  toggleTool: (toolName: string) => void;
  selectedMcpToolServerIds: string[];
  toggleMcpToolServer: (mcpToolServerId: string) => void;
  idPrefix?: string;
}

type SelectedItem = {
  id: string;
  title: string;
  description?: string;
};

interface SelectedItemsListProps {
  items: SelectedItem[];
  emptyLabel: string;
  onRemove: (id: string) => void;
  readOnly?: boolean;
}

function SelectedItemsList({
  items,
  emptyLabel,
  onRemove,
  readOnly = false,
}: SelectedItemsListProps) {
  if (items.length === 0) {
    return (
      <div className="rounded-lg border border-dashed bg-muted/20 px-4 py-8 text-center text-sm text-muted-foreground">
        {emptyLabel}
      </div>
    );
  }

  return (
    <ItemGroup className="gap-2">
      {items.map((item) => (
        <Item key={item.id} variant="outline" size="sm" className="bg-background/70">
          <ItemContent className="min-w-0">
            <ItemTitle className="max-w-full truncate">{item.title}</ItemTitle>
            {item.description ? (
              <ItemDescription className="line-clamp-2 text-xs">{item.description}</ItemDescription>
            ) : null}
          </ItemContent>
          {!readOnly ? (
            <ItemActions>
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                aria-label={`Remove ${item.title}`}
                onClick={() => onRemove(item.id)}
              >
                <X />
              </Button>
            </ItemActions>
          ) : null}
        </Item>
      ))}
    </ItemGroup>
  );
}

function ExternalAgentNotice({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed bg-muted/30 px-4 py-3 text-sm text-muted-foreground">
      {children}
    </div>
  );
}

export function AgentFormFields({
  mode,
  dialogPortalContainer,
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
  extra,
  setExtra,
  environmentVariables,
  setEnvironmentVariables,
  selectedSkillIds,
  appOptions,
  selectedAppInstanceIds,
  appSearchTerm,
  setAppSearchTerm,
  filteredAppOptions,
  toggleAppInstance,
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
}: AgentFormFieldsProps) {
  const [appPopoverOpen, setAppPopoverOpen] = React.useState(false);
  const isExternalAgent = agentType === "1";
  const canEditExtra = mode === "edit" && isExternalAgent;
  const extraError = canEditExtra ? getAgentExtraSettingsError(extra) : null;

  const selectedSkills =
    skillsQuery.data?.filter((skill) => selectedSkillIds.includes(skill.id)) ?? [];
  const selectedApps = appOptions.filter((app) => selectedAppInstanceIds.includes(app.id));
  const selectedToolItems = selectedTools.map((toolName) => {
    const tool = toolsQuery.data?.find((candidate) => candidate.name === toolName);
    return {
      id: toolName,
      title: toolName,
      description: tool ? [tool.category, tool.description].filter(Boolean).join(" · ") : undefined,
    };
  });
  const selectedMcpToolServers = selectedMcpToolServerIds.map((selectedId) => {
    const server = mcpToolServersQuery.data?.find((candidate) => candidate.id === selectedId);
    return {
      id: selectedId,
      title: server?.name ?? selectedId,
    };
  });
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
      } else {
        groups.set(category, [tool]);
      }
    }

    return Array.from(groups.entries()).sort(([left], [right]) => left.localeCompare(right));
  }, [toolsQuery.data]);

  return (
    <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,45%)_minmax(0,1fr)] overflow-hidden border-t lg:grid-cols-[400px_minmax(0,1fr)] lg:grid-rows-1">
      <div className="overflow-y-auto border-b bg-muted/20 p-6 lg:border-r lg:border-b-0">
        <div className="grid gap-5">
          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}displayName`}>Display Name</Label>
            <Input
              id={`${idPrefix}displayName`}
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              placeholder="Agent display name"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}name`}>Name (Optional)</Label>
            <Input
              id={`${idPrefix}name`}
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="agent id"
              readOnly={mode === "edit"}
              className={mode === "edit" ? "bg-muted/50" : undefined}
            />
            {mode === "edit" ? (
              <p className="text-xs text-muted-foreground">
                The agent name is a stable identifier.
              </p>
            ) : null}
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}description`}>Description</Label>
            <Input
              id={`${idPrefix}description`}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="Agent description..."
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}agentType`}>Agent Type</Label>
            <Input
              id={`${idPrefix}agentType`}
              value={isExternalAgent ? "External" : "System"}
              readOnly
              className="bg-muted/50"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}modelProviderId`}>
              Model Provider
              {isExternalAgent ? (
                <span className="ml-2 text-xs text-muted-foreground">(Optional)</span>
              ) : null}
            </Label>
            <Select value={modelProviderId} onValueChange={setModelProviderId}>
              <SelectTrigger id={`${idPrefix}modelProviderId`} className="w-full">
                <SelectValue
                  placeholder={
                    isExternalAgent
                      ? "Optional: Select a model provider..."
                      : "Select a model provider..."
                  }
                />
              </SelectTrigger>
              <SelectContent
                position="popper"
                sideOffset={4}
                portalContainer={dialogPortalContainer}
              >
                <SelectGroup>
                  <SelectLabel>Available Model Providers</SelectLabel>
                  {modelProvidersQuery.isLoading ? (
                    <SelectItem value="loading" disabled>
                      Loading...
                    </SelectItem>
                  ) : modelProvidersQuery.data && modelProvidersQuery.data.length > 0 ? (
                    modelProvidersQuery.data.map((modelProvider) => (
                      <SelectItem key={modelProvider.id} value={modelProvider.id}>
                        {modelProvider.modelName} ({modelProvider.providerName}-
                        {modelProvider.providerType})
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
            <Label htmlFor={`${idPrefix}extra`}>Extra Settings (JSON)</Label>
            <Textarea
              id={`${idPrefix}extra`}
              value={extra}
              onChange={(event) => setExtra?.(event.target.value)}
              placeholder="{}"
              rows={7}
              readOnly={!canEditExtra}
              aria-invalid={Boolean(extraError)}
              className={!canEditExtra ? "bg-muted/50 font-mono text-xs" : "font-mono text-xs"}
            />
            {extraError ? (
              <p className="text-xs text-destructive">{extraError}</p>
            ) : (
              <p className="text-xs text-muted-foreground">
                {canEditExtra
                  ? "Optional JSON object stored with this external agent definition."
                  : "Extra Settings can be edited only for external agents."}
              </p>
            )}
          </div>
        </div>
      </div>

      <div className="min-h-0 overflow-hidden bg-background">
        <Tabs defaultValue="system-prompt" className="flex h-full min-h-0 flex-col">
          <div className="shrink-0 overflow-x-auto border-b px-6 py-3">
            <TabsList className="h-auto w-max">
              <TabsTrigger value="system-prompt">System Prompt</TabsTrigger>
              <TabsTrigger value="skills">Skills</TabsTrigger>
              <TabsTrigger value="tools">Tools</TabsTrigger>
              <TabsTrigger value="mcp-tool-servers">MCP Tool Server</TabsTrigger>
              <TabsTrigger value="apps">Apps</TabsTrigger>
              <TabsTrigger value="environment-variables">Environment Variables</TabsTrigger>
            </TabsList>
          </div>

          <TabsContent
            value="system-prompt"
            className="m-0 flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-6"
          >
            <div>
              <h3 className="font-medium">System Prompt</h3>
              <p className="mt-1 text-sm text-muted-foreground">
                Define the instructions and operating context for this agent.
              </p>
            </div>
            {isExternalAgent ? (
              <ExternalAgentNotice>
                External agents do not support system prompt configuration.
              </ExternalAgentNotice>
            ) : null}
            <Textarea
              id={`${idPrefix}systemPrompt`}
              value={systemPrompt}
              onChange={(event) => setSystemPrompt(event.target.value)}
              readOnly={isExternalAgent}
              className="min-h-80 flex-1 resize-none font-mono text-sm"
            />
          </TabsContent>

          <TabsContent value="skills" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <div className="space-y-6">
              <div>
                <h3 className="font-medium">Skills</h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  Attach reusable instruction packages to the agent.
                </p>
              </div>
              {isExternalAgent ? (
                <ExternalAgentNotice>
                  External agents do not support skill configuration.
                </ExternalAgentNotice>
              ) : null}
              <DropdownMenu modal={false}>
                <DropdownMenuTrigger asChild>
                  <Button
                    id={`${idPrefix}skills`}
                    type="button"
                    variant="outline"
                    className="w-full justify-between font-normal"
                    disabled={isExternalAgent}
                  >
                    <span>
                      {selectedSkillIds.length > 0
                        ? `${selectedSkillIds.length} skill${selectedSkillIds.length === 1 ? "" : "s"} selected`
                        : "Select skills..."}
                    </span>
                    <ChevronDown className="size-4 opacity-50" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent
                  align="start"
                  className="max-h-72 w-[var(--radix-dropdown-menu-trigger-width)]"
                  portalContainer={dialogPortalContainer}
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
                            <div className="whitespace-normal break-words text-xs text-muted-foreground">
                              {skill.description}
                            </div>
                          ) : null}
                        </div>
                      </DropdownMenuCheckboxItem>
                    ))
                  ) : (
                    <div className="px-2 py-1.5 text-sm text-muted-foreground">No skills found</div>
                  )}
                </DropdownMenuContent>
              </DropdownMenu>
              <SelectedItemsList
                items={selectedSkills.map((skill) => ({
                  id: skill.id,
                  title: skill.name,
                  description: skill.description,
                }))}
                emptyLabel="No skills selected"
                onRemove={toggleSkill}
                readOnly={isExternalAgent}
              />
            </div>
          </TabsContent>

          <TabsContent value="tools" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <div className="space-y-6">
              <div>
                <h3 className="font-medium">Tools</h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  Give the agent access to registered application tools.
                </p>
              </div>
              {isExternalAgent ? (
                <ExternalAgentNotice>
                  External agents do not support tool configuration.
                </ExternalAgentNotice>
              ) : null}
              <DropdownMenu modal={false}>
                <DropdownMenuTrigger asChild>
                  <Button
                    id={`${idPrefix}tools`}
                    type="button"
                    variant="outline"
                    className="w-full justify-between font-normal"
                    disabled={isExternalAgent}
                  >
                    <span>
                      {selectedTools.length > 0
                        ? `${selectedTools.length} tool${selectedTools.length === 1 ? "" : "s"} selected`
                        : "Select tools..."}
                    </span>
                    <ChevronDown className="size-4 opacity-50" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent
                  align="start"
                  className="max-h-72 w-[var(--radix-dropdown-menu-trigger-width)]"
                  portalContainer={dialogPortalContainer}
                >
                  <DropdownMenuLabel>Available Tools</DropdownMenuLabel>
                  {toolsQuery.isLoading ? (
                    <div className="px-2 py-1.5 text-sm text-muted-foreground">
                      Loading tools...
                    </div>
                  ) : groupedTools.length > 0 ? (
                    groupedTools.map(([category, tools]) => (
                      <React.Fragment key={category}>
                        <DropdownMenuLabel>{category}</DropdownMenuLabel>
                        {tools.map((tool) => (
                          <DropdownMenuCheckboxItem
                            key={tool.name}
                            checked={selectedTools.includes(tool.name)}
                            className="items-start"
                            onCheckedChange={() => toggleTool(tool.name)}
                            onSelect={(event) => event.preventDefault()}
                          >
                            <div className="min-w-0">
                              <div className="truncate font-medium">{tool.name}</div>
                              {tool.description ? (
                                <div className="whitespace-normal break-words text-xs text-muted-foreground">
                                  {tool.description}
                                </div>
                              ) : null}
                            </div>
                          </DropdownMenuCheckboxItem>
                        ))}
                      </React.Fragment>
                    ))
                  ) : (
                    <div className="px-2 py-1.5 text-sm text-muted-foreground">No tools found</div>
                  )}
                </DropdownMenuContent>
              </DropdownMenu>
              <SelectedItemsList
                items={selectedToolItems}
                emptyLabel="No tools selected"
                onRemove={toggleTool}
                readOnly={isExternalAgent}
              />
            </div>
          </TabsContent>

          <TabsContent value="mcp-tool-servers" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <div className="space-y-6">
              <div>
                <h3 className="font-medium">MCP Tool Server</h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  Connect the agent to configured Model Context Protocol servers.
                </p>
              </div>
              <DropdownMenu modal={false}>
                <DropdownMenuTrigger asChild>
                  <Button
                    id={`${idPrefix}mcpToolServers`}
                    type="button"
                    variant="outline"
                    className="w-full justify-between font-normal"
                  >
                    <span>
                      {selectedMcpToolServerIds.length > 0
                        ? `${selectedMcpToolServerIds.length} server${selectedMcpToolServerIds.length === 1 ? "" : "s"} selected`
                        : "Select MCP tool servers..."}
                    </span>
                    <ChevronDown className="size-4 opacity-50" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent
                  align="start"
                  className="max-h-72 w-[var(--radix-dropdown-menu-trigger-width)]"
                  portalContainer={dialogPortalContainer}
                >
                  <DropdownMenuLabel>Available MCP Tool Servers</DropdownMenuLabel>
                  {mcpToolServersQuery.isLoading ? (
                    <div className="px-2 py-1.5 text-sm text-muted-foreground">
                      Loading MCP tool servers...
                    </div>
                  ) : mcpToolServersQuery.data && mcpToolServersQuery.data.length > 0 ? (
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
              <SelectedItemsList
                items={selectedMcpToolServers}
                emptyLabel="No MCP tool servers selected"
                onRemove={toggleMcpToolServer}
              />
            </div>
          </TabsContent>

          <TabsContent value="apps" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <div className="space-y-6">
              <div>
                <h3 className="font-medium">Apps</h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  Attach authorized integration connections to this agent.
                </p>
              </div>
              <Popover open={appPopoverOpen} onOpenChange={setAppPopoverOpen}>
                <PopoverTrigger asChild>
                  <Button
                    id={`${idPrefix}appInstances`}
                    type="button"
                    variant="outline"
                    className="w-full justify-between font-normal"
                    disabled={appOptions.length === 0}
                  >
                    <span>
                      {selectedAppInstanceIds.length > 0
                        ? `${selectedAppInstanceIds.length} app${selectedAppInstanceIds.length === 1 ? "" : "s"} selected`
                        : "Select apps..."}
                    </span>
                    <ChevronDown className="size-4 opacity-50" />
                  </Button>
                </PopoverTrigger>
                <PopoverContent
                  className="w-[var(--radix-popover-trigger-width)] p-0"
                  align="start"
                  portalContainer={dialogPortalContainer}
                >
                  <div className="border-b p-2">
                    <Input
                      value={appSearchTerm}
                      onChange={(event) => setAppSearchTerm(event.target.value)}
                      placeholder="Search apps..."
                    />
                  </div>
                  <div className="max-h-72 overflow-y-auto p-1">
                    {filteredAppOptions.length > 0 ? (
                      filteredAppOptions.map((app) => (
                        <button
                          key={app.id}
                          type="button"
                          className="flex w-full items-start justify-between rounded-md px-2 py-2 text-left hover:bg-muted"
                          onClick={() => toggleAppInstance(app.id)}
                        >
                          <div className="min-w-0">
                            <div className="truncate font-medium">{buildAppOptionLabel(app)}</div>
                            <div className="text-xs text-muted-foreground">
                              {app.provider} · {getAppAuthorizationState(app)}
                            </div>
                          </div>
                          <input
                            tabIndex={-1}
                            type="checkbox"
                            checked={selectedAppInstanceIds.includes(app.id)}
                            readOnly
                          />
                        </button>
                      ))
                    ) : (
                      <div className="px-2 py-3 text-sm text-muted-foreground">No apps found</div>
                    )}
                  </div>
                </PopoverContent>
              </Popover>
              <SelectedItemsList
                items={selectedApps.map((app) => ({
                  id: app.id,
                  title: buildAppOptionLabel(app),
                  description: `${app.provider} · ${getAppAuthorizationState(app)}`,
                }))}
                emptyLabel="No apps selected"
                onRemove={toggleAppInstance}
              />
              {appOptions.length === 0 ? (
                <p className="text-sm text-muted-foreground">
                  No app connections found. Create one on the integrations page first.
                </p>
              ) : null}
            </div>
          </TabsContent>

          <TabsContent
            value="environment-variables"
            className="m-0 min-h-0 flex-1 overflow-y-auto p-6"
          >
            <AgentEnvironmentVariablesEditor
              entries={environmentVariables}
              setEntries={setEnvironmentVariables}
              idPrefix={idPrefix}
            />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
