import * as React from "react";
import type { UseQueryResult } from "@agw/components/query";

import { SearchableSelect, type SearchableSelectOption } from "@agw/components";

import { buildConnectionSelectOptions, buildSelectedConnectionItems } from "./connection-selector";
import { EnvironmentVariablesEditor } from "./environment-variables-editor";
import type { EnvironmentVariableEntry } from "./environment-variables";
import { SelectedItemsList } from "./selected-items-list";
import { buildSelectedSkillItems } from "./selection-items";
import type { ConnectionOption, McpToolServerDto, SkillDto } from "./types";

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

interface McpToolServersPanelProps extends SharedPanelProps {
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  toggleMcpToolServer: (mcpToolServerId: string) => void;
  disabled?: boolean;
  notice?: React.ReactNode;
}

export function McpToolServersPanel({
  idPrefix = "",
  ownerLabel = "agent",
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  toggleMcpToolServer,
  disabled = false,
  notice,
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
      {notice}
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
        disabled={disabled}
        isLoading={mcpToolServersQuery.isLoading}
        clearable={false}
      />
      <SelectedItemsList
        items={selectedServers}
        emptyLabel="No MCP tool servers selected"
        onRemove={toggleMcpToolServer}
        readOnly={disabled}
      />
    </div>
  );
}

interface ConnectionsPanelProps extends SharedPanelProps {
  connectionOptions: ConnectionOption[];
  selectedConnectionIds: string[];
  toggleConnection: (connectionId: string) => void;
  disabled?: boolean;
  notice?: React.ReactNode;
}

export function ConnectionsPanel({
  idPrefix = "",
  ownerLabel = "agent",
  connectionOptions,
  selectedConnectionIds,
  toggleConnection,
  disabled = false,
  notice,
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
        <h3 className="font-medium">Integrations</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Attach ready external accounts or service endpoints to this {ownerLabel}.
        </p>
      </div>
      {notice}
      <SearchableSelect
        multiple
        id={`${idPrefix}connections`}
        ariaLabel="Integrations"
        value={selectedConnectionIds}
        onValueChange={(values) =>
          applySelectionChange(selectedConnectionIds, values, toggleConnection)
        }
        options={connectionSelectOptions}
        placeholder="Select ready integrations..."
        selectionText={
          selectedConnectionIds.length > 0
            ? `${selectedConnectionIds.length} integration${selectedConnectionIds.length === 1 ? "" : "s"} selected`
            : undefined
        }
        searchPlaceholder="Search integrations..."
        disabled={disabled || connectionSelectOptions.length === 0}
        clearable={false}
      />
      <SelectedItemsList
        items={selectedConnections}
        emptyLabel="No integrations selected"
        onRemove={toggleConnection}
        readOnly={disabled}
      />
      {!disabled && connectionSelectOptions.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No ready integrations found. Create and validate one on the integrations page first.
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
