import * as React from "react";
import type { UseQueryResult } from "@tanstack/react-query";
import { Check, ChevronDown } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

import {
  buildAppOptionLabel,
  buildSelectedAppItems,
  buildSelectedSkillItems,
  getAppAuthorizationState,
} from "./app-selector";
import { EnvironmentVariablesEditor } from "./environment-variables-editor";
import type { EnvironmentVariableEntry } from "./environment-variables";
import { SelectedItemsList } from "./selected-items-list";
import type { AppInstanceOption, McpToolServerDto, SkillDto, ToolInfo } from "./types";

interface SharedPanelProps {
  dialogPortalContainer: HTMLElement | null;
  idPrefix?: string;
  ownerLabel?: string;
}

interface SkillsPanelProps extends SharedPanelProps {
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  selectedSkillIds: string[];
  toggleSkill: (skillId: string) => void;
  disabled?: boolean;
  notice?: React.ReactNode;
}

export function SkillsPanel({
  dialogPortalContainer,
  idPrefix = "",
  ownerLabel = "agent",
  skillsQuery,
  selectedSkillIds,
  toggleSkill,
  disabled = false,
  notice,
}: SkillsPanelProps) {
  const selectedSkills = buildSelectedSkillItems(selectedSkillIds, skillsQuery.data ?? []);

  return (
    <div className="space-y-6">
      <div>
        <h3 className="font-medium">Skills</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Attach reusable instruction packages to the {ownerLabel}.
        </p>
      </div>
      {notice}
      <DropdownMenu modal={false}>
        <DropdownMenuTrigger asChild>
          <Button
            id={`${idPrefix}skills`}
            type="button"
            variant="outline"
            className="w-full justify-between font-normal"
            disabled={disabled}
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
          className="max-h-72 overflow-y-auto w-[var(--radix-dropdown-menu-trigger-width)]"
          portalContainer={dialogPortalContainer}
        >
          <DropdownMenuLabel>Available Skills</DropdownMenuLabel>
          {skillsQuery.isLoading ? (
            <div className="px-2 py-1.5 text-sm text-muted-foreground">Loading skills...</div>
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
        items={selectedSkills}
        emptyLabel="No skills selected"
        onRemove={toggleSkill}
        readOnly={disabled}
      />
    </div>
  );
}

interface ToolsPanelProps extends SharedPanelProps {
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  selectedTools: string[];
  toggleTool: (toolName: string) => void;
  disabled?: boolean;
  notice?: React.ReactNode;
}

export function ToolsPanel({
  dialogPortalContainer,
  idPrefix = "",
  ownerLabel = "agent",
  toolsQuery,
  selectedTools,
  toggleTool,
  disabled = false,
  notice,
}: ToolsPanelProps) {
  const groupedTools = React.useMemo(() => {
    const groups = new Map<string, ToolInfo[]>();
    for (const tool of toolsQuery.data ?? []) {
      const category = tool.category.trim() || "Uncategorized";
      groups.set(category, [...(groups.get(category) ?? []), tool]);
    }

    return Array.from(groups.entries()).sort(([left], [right]) => left.localeCompare(right));
  }, [toolsQuery.data]);
  const selectedToolItems = selectedTools.map((toolName) => {
    const tool = toolsQuery.data?.find((candidate) => candidate.name === toolName);
    return {
      id: toolName,
      title: toolName,
      description: tool ? [tool.category, tool.description].filter(Boolean).join(" · ") : undefined,
    };
  });

  return (
    <div className="space-y-6">
      <div>
        <h3 className="font-medium">Tools</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Give the {ownerLabel} access to registered application tools.
        </p>
      </div>
      {notice}
      <DropdownMenu modal={false}>
        <DropdownMenuTrigger asChild>
          <Button
            id={`${idPrefix}tools`}
            type="button"
            variant="outline"
            className="w-full justify-between font-normal"
            disabled={disabled}
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
          className="max-h-72 overflow-y-auto w-[var(--radix-dropdown-menu-trigger-width)]"
          portalContainer={dialogPortalContainer}
        >
          <DropdownMenuLabel>Available Tools</DropdownMenuLabel>
          {toolsQuery.isLoading ? (
            <div className="px-2 py-1.5 text-sm text-muted-foreground">Loading tools...</div>
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
        readOnly={disabled}
      />
    </div>
  );
}

interface McpToolServersPanelProps extends SharedPanelProps {
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  toggleMcpToolServer: (mcpToolServerId: string) => void;
}

export function McpToolServersPanel({
  dialogPortalContainer,
  idPrefix = "",
  ownerLabel = "agent",
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  toggleMcpToolServer,
}: McpToolServersPanelProps) {
  const selectedServers = selectedMcpToolServerIds.map((selectedId) => ({
    id: selectedId,
    title:
      mcpToolServersQuery.data?.find((candidate) => candidate.id === selectedId)?.name ??
      selectedId,
  }));

  return (
    <div className="space-y-6">
      <div>
        <h3 className="font-medium">MCP Tool Server</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Connect the {ownerLabel} to configured Model Context Protocol servers.
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
          className="max-h-72 overflow-y-auto w-[var(--radix-dropdown-menu-trigger-width)]"
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
        items={selectedServers}
        emptyLabel="No MCP tool servers selected"
        onRemove={toggleMcpToolServer}
      />
    </div>
  );
}

interface AppsPanelProps extends SharedPanelProps {
  appOptions: AppInstanceOption[];
  selectedAppInstanceIds: string[];
  appSearchTerm: string;
  setAppSearchTerm: (value: string) => void;
  filteredAppOptions: AppInstanceOption[];
  toggleAppInstance: (appInstanceId: string) => void;
}

export function AppsPanel({
  dialogPortalContainer,
  idPrefix = "",
  ownerLabel = "agent",
  appOptions,
  selectedAppInstanceIds,
  appSearchTerm,
  setAppSearchTerm,
  filteredAppOptions,
  toggleAppInstance,
}: AppsPanelProps) {
  const [appPopoverOpen, setAppPopoverOpen] = React.useState(false);
  const selectedApps = buildSelectedAppItems(selectedAppInstanceIds, appOptions);

  return (
    <div className="space-y-6">
      <div>
        <h3 className="font-medium">Apps</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Attach authorized integration connections to this {ownerLabel}.
        </p>
      </div>
      <Popover modal={false} open={appPopoverOpen} onOpenChange={setAppPopoverOpen}>
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
                  aria-pressed={selectedAppInstanceIds.includes(app.id)}
                >
                  <div className="min-w-0">
                    <div className="truncate font-medium">{buildAppOptionLabel(app)}</div>
                    <div className="text-xs text-muted-foreground">
                      {app.provider} · {getAppAuthorizationState(app)}
                    </div>
                  </div>
                  <span
                    aria-hidden="true"
                    className="ml-3 flex size-4 shrink-0 items-center justify-center"
                  >
                    {selectedAppInstanceIds.includes(app.id) ? (
                      <Check aria-hidden="true" className="size-4" />
                    ) : null}
                  </span>
                </button>
              ))
            ) : (
              <div className="px-2 py-3 text-sm text-muted-foreground">No apps found</div>
            )}
          </div>
        </PopoverContent>
      </Popover>
      <SelectedItemsList
        items={selectedApps}
        emptyLabel="No apps selected"
        onRemove={toggleAppInstance}
      />
      {appOptions.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No app connections found. Create one on the integrations page first.
        </p>
      ) : null}
    </div>
  );
}

interface EnvironmentVariablesPanelProps {
  entries: EnvironmentVariableEntry[];
  setEntries: (entries: EnvironmentVariableEntry[]) => void;
  idPrefix?: string;
  ownerLabel?: string;
}

export function EnvironmentVariablesPanel({
  entries,
  setEntries,
  idPrefix,
  ownerLabel,
}: EnvironmentVariablesPanelProps) {
  return (
    <EnvironmentVariablesEditor
      entries={entries}
      setEntries={setEntries}
      idPrefix={idPrefix}
      scopeLabel={ownerLabel}
    />
  );
}
