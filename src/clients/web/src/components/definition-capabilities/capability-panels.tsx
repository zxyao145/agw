import * as React from "react";
import type { UseQueryResult } from "@tanstack/react-query";

import {
  SearchableSelect,
  type SearchableSelectOption,
} from "@/components/SearchableSelect/searchable-select";

import { buildConnectionSelectOptions, buildSelectedConnectionItems } from "./connection-selector";
import { EnvironmentVariablesEditor } from "./environment-variables-editor";
import type { EnvironmentVariableEntry } from "./environment-variables";
import { SelectedItemsList } from "./selected-items-list";
import { buildSelectedSkillItems } from "./selection-items";
import type { ConnectionOption, McpToolServerDto, SkillDto, ToolInfo } from "./types";

interface SharedPanelProps {
  idPrefix?: string;
  ownerLabel?: string;
}

function applySelectionChange(
  currentValues: string[],
  nextValues: string[],
  toggleValue: (value: string) => void,
) {
  const changedValue =
    nextValues.find((value) => !currentValues.includes(value)) ??
    currentValues.find((value) => !nextValues.includes(value));

  if (changedValue) {
    toggleValue(changedValue);
  }
}

interface SkillsPanelProps extends SharedPanelProps {
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  selectedSkillIds: string[];
  toggleSkill: (skillId: string) => void;
  disabled?: boolean;
  notice?: React.ReactNode;
}

export function SkillsPanel({
  idPrefix = "",
  ownerLabel = "agent",
  skillsQuery,
  selectedSkillIds,
  toggleSkill,
  disabled = false,
  notice,
}: SkillsPanelProps) {
  const selectedSkills = buildSelectedSkillItems(selectedSkillIds, skillsQuery.data ?? []);
  const skillOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (skillsQuery.data ?? []).map((skill) => ({
        value: skill.id,
        title: skill.name,
        subtitle: skill.description,
        group: "Available Skills",
      })),
    [skillsQuery.data],
  );

  return (
    <div className="space-y-6">
      <div>
        <h3 className="font-medium">Skills</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Attach reusable instruction packages to the {ownerLabel}.
        </p>
      </div>
      {notice}
      <SearchableSelect
        multiple
        id={`${idPrefix}skills`}
        ariaLabel="Skills"
        value={selectedSkillIds}
        onValueChange={(values) => applySelectionChange(selectedSkillIds, values, toggleSkill)}
        options={skillOptions}
        placeholder="Select skills..."
        selectionText={
          selectedSkillIds.length > 0
            ? `${selectedSkillIds.length} skill${selectedSkillIds.length === 1 ? "" : "s"} selected`
            : undefined
        }
        searchPlaceholder="Search skills..."
        disabled={disabled}
        isLoading={skillsQuery.isLoading}
        clearable={false}
      />
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
  idPrefix = "",
  ownerLabel = "agent",
  toolsQuery,
  selectedTools,
  toggleTool,
  disabled = false,
  notice,
}: ToolsPanelProps) {
  const toolOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (toolsQuery.data ?? [])
        .map((tool) => ({
          value: tool.name,
          title: tool.name,
          subtitle: tool.description,
          group: tool.category.trim() || "Uncategorized",
        }))
        .sort((left, right) => (left.group ?? "").localeCompare(right.group ?? "")),
    [toolsQuery.data],
  );
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
      <SearchableSelect
        multiple
        id={`${idPrefix}tools`}
        ariaLabel="Tools"
        value={selectedTools}
        onValueChange={(values) => applySelectionChange(selectedTools, values, toggleTool)}
        options={toolOptions}
        placeholder="Select tools..."
        selectionText={
          selectedTools.length > 0
            ? `${selectedTools.length} tool${selectedTools.length === 1 ? "" : "s"} selected`
            : undefined
        }
        searchPlaceholder="Search tools..."
        disabled={disabled}
        isLoading={toolsQuery.isLoading}
        clearable={false}
      />
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
  idPrefix = "",
  ownerLabel = "agent",
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  toggleMcpToolServer,
}: McpToolServersPanelProps) {
  const serverOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (mcpToolServersQuery.data ?? []).map((server) => ({
        value: server.id,
        title: server.name,
        group: "Available MCP Tool Servers",
      })),
    [mcpToolServersQuery.data],
  );
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
      <SearchableSelect
        multiple
        id={`${idPrefix}mcpToolServers`}
        ariaLabel="MCP Tool Servers"
        value={selectedMcpToolServerIds}
        onValueChange={(values) =>
          applySelectionChange(selectedMcpToolServerIds, values, toggleMcpToolServer)
        }
        options={serverOptions}
        placeholder="Select MCP tool servers..."
        selectionText={
          selectedMcpToolServerIds.length > 0
            ? `${selectedMcpToolServerIds.length} server${selectedMcpToolServerIds.length === 1 ? "" : "s"} selected`
            : undefined
        }
        searchPlaceholder="Search MCP tool servers..."
        isLoading={mcpToolServersQuery.isLoading}
        clearable={false}
      />
      <SelectedItemsList
        items={selectedServers}
        emptyLabel="No MCP tool servers selected"
        onRemove={toggleMcpToolServer}
      />
    </div>
  );
}

interface ConnectionsPanelProps extends SharedPanelProps {
  connectionOptions: ConnectionOption[];
  selectedConnectionIds: string[];
  toggleConnection: (connectionId: string) => void;
}

export function ConnectionsPanel({
  idPrefix = "",
  ownerLabel = "agent",
  connectionOptions,
  selectedConnectionIds,
  toggleConnection,
}: ConnectionsPanelProps) {
  const connectionSelectOptions = React.useMemo<SearchableSelectOption[]>(
    () => buildConnectionSelectOptions(connectionOptions, selectedConnectionIds),
    [connectionOptions, selectedConnectionIds],
  );
  const selectedConnections = buildSelectedConnectionItems(
    selectedConnectionIds,
    connectionOptions,
  );

  return (
    <div className="space-y-6">
      <div>
        <h3 className="font-medium">Connections</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Attach ready external accounts or service endpoints to this {ownerLabel}.
        </p>
      </div>
      <SearchableSelect
        multiple
        id={`${idPrefix}connections`}
        ariaLabel="Connections"
        value={selectedConnectionIds}
        onValueChange={(values) =>
          applySelectionChange(selectedConnectionIds, values, toggleConnection)
        }
        options={connectionSelectOptions}
        placeholder="Select ready connections..."
        selectionText={
          selectedConnectionIds.length > 0
            ? `${selectedConnectionIds.length} connection${selectedConnectionIds.length === 1 ? "" : "s"} selected`
            : undefined
        }
        searchPlaceholder="Search connections..."
        disabled={connectionSelectOptions.length === 0}
        clearable={false}
      />
      <SelectedItemsList
        items={selectedConnections}
        emptyLabel="No connections selected"
        onRemove={toggleConnection}
      />
      {connectionSelectOptions.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No ready connections found. Create and validate one on the integrations page first.
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
