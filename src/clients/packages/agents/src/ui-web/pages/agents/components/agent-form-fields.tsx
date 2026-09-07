import * as React from "react";
import { UseQueryResult } from "@agw/components/query";

import {
  ConnectionsPanel,
  EnvironmentVariablesPanel,
  McpToolServersPanel,
  SkillsPanel,
  type ConnectionOption,
  type EnvironmentVariableEntry,
  type McpToolServerDto,
  type SkillDto,
} from "@agw/integrations";
import { SearchableSelect, type SearchableSelectOption } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Switch } from "@agw/components";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@agw/components";
import { Textarea } from "@agw/components";
import { ToolsPanel, type ToolInfo, type ToolValueObject } from "@agw/tools";

import { getAgentExtraSettingsError } from "./agent-extra-settings";
import {
  AgentType,
  ExternalAgentKind,
  type AgentType as AgentTypeValue,
  type ExternalAgentKind as ExternalAgentKindValue,
  type ExternalAgentOptionDto,
  type ModelProviderDto,
} from "./types";

type AgentFormMode = "create" | "edit";

interface AgentFormFieldsProps {
  mode: AgentFormMode;
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
  summaryModelProviderId: string;
  setSummaryModelProviderId: (value: string) => void;
  enableSummary: boolean;
  setEnableSummary: (value: boolean) => void;
  agentType: AgentTypeValue;
  setAgentType?: (value: AgentTypeValue) => void;
  externalAgentKind: ExternalAgentKindValue;
  setExternalAgentKind?: (value: ExternalAgentKindValue) => void;
  externalAgentOptionsQuery: UseQueryResult<ExternalAgentOptionDto[], Error>;
  extra: string;
  setExtra: (value: string) => void;
  environmentVariables: EnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: EnvironmentVariableEntry[]) => void;
  selectedSkillIds: string[];
  connectionOptions: ConnectionOption[];
  selectedConnectionIds: string[];
  toggleConnection: (connectionId: string) => void;
  tools: ToolValueObject[];
  setTools: (value: ToolValueObject[]) => void;
  agentOptions: Array<{ id: string; name: string; displayName?: string }>;
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  toggleSkill: (skillId: string) => void;
  selectedMcpToolServerIds: string[];
  toggleMcpToolServer: (mcpToolServerId: string) => void;
  idPrefix?: string;
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
  summaryModelProviderId,
  setSummaryModelProviderId,
  enableSummary,
  setEnableSummary,
  agentType,
  setAgentType,
  externalAgentKind,
  setExternalAgentKind,
  externalAgentOptionsQuery,
  extra,
  setExtra,
  environmentVariables,
  setEnvironmentVariables,
  selectedSkillIds,
  connectionOptions,
  selectedConnectionIds,
  toggleConnection,
  tools,
  setTools,
  agentOptions,
  modelProvidersQuery,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  toggleSkill,
  selectedMcpToolServerIds,
  toggleMcpToolServer,
  idPrefix = "",
}: AgentFormFieldsProps) {
  const isExternalAgent = agentType === AgentType.External;
  const effectiveSummaryModelProviderId = isExternalAgent
    ? summaryModelProviderId
    : summaryModelProviderId || modelProviderId;
  const canEditExtra = isExternalAgent;
  const extraError = canEditExtra ? getAgentExtraSettingsError(extra) : null;
  const modelProviderOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (modelProvidersQuery.data ?? []).map((modelProvider) => ({
        value: modelProvider.id,
        title: `${modelProvider.modelName} (${modelProvider.providerName}-${modelProvider.providerType})`,
        group: "Available Model Providers",
      })),
    [modelProvidersQuery.data],
  );
  const externalAgentKindOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (externalAgentOptionsQuery.data ?? []).map((option) => ({
        value: option.kind.toString(),
        title: option.displayName,
      })),
    [externalAgentOptionsQuery.data],
  );
  const externalAgentKindDisplayName = externalAgentOptionsQuery.data?.find(
    (option) => option.kind === externalAgentKind,
  )?.displayName;

  return (
    <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,45%)_minmax(0,1fr)] overflow-hidden lg:grid-cols-[360px_minmax(0,1fr)] lg:grid-rows-1">
      <div className="overflow-y-auto agw-scrollbar border-b bg-muted/20 p-4 lg:border-r lg:border-b-0">
        <div className="grid gap-5">
          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}displayName`}>
              Display Name
              {isExternalAgent ? (
                <span className="ml-2 text-xs text-muted-foreground">(Optional)</span>
              ) : null}
            </Label>
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
            <Label htmlFor={`${idPrefix}description`}>
              Description
              {isExternalAgent ? (
                <span className="ml-2 text-xs text-muted-foreground">(Optional)</span>
              ) : null}
            </Label>
            <Input
              id={`${idPrefix}description`}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="Agent description..."
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}agentType`}>Agent Type</Label>
            {mode === "create" ? (
              <SearchableSelect
                id={`${idPrefix}agentType`}
                ariaLabel="Agent Type"
                value={agentType.toString()}
                onValueChange={(value) => setAgentType?.(Number(value) as AgentTypeValue)}
                options={[
                  { value: AgentType.System.toString(), title: "System" },
                  { value: AgentType.External.toString(), title: "External" },
                ]}
                placeholder="Select an agent type..."
                searchPlaceholder="Search agent types..."
              />
            ) : (
              <Input
                id={`${idPrefix}agentType`}
                value={isExternalAgent ? "External" : "System"}
                readOnly
                className="bg-muted/50"
              />
            )}
          </div>

          {isExternalAgent ? (
            <div className="grid gap-2">
              <Label htmlFor={`${idPrefix}externalAgentKind`}>External Agent</Label>
              {mode === "create" ? (
                <SearchableSelect
                  id={`${idPrefix}externalAgentKind`}
                  ariaLabel="External Agent"
                  value={
                    externalAgentKind === ExternalAgentKind.None ? "" : externalAgentKind.toString()
                  }
                  onValueChange={(value) =>
                    setExternalAgentKind?.(Number(value) as ExternalAgentKindValue)
                  }
                  options={externalAgentKindOptions}
                  placeholder="Select an external agent..."
                  searchPlaceholder="Search external agents..."
                  isLoading={externalAgentOptionsQuery.isLoading}
                />
              ) : (
                <Input
                  id={`${idPrefix}externalAgentKind`}
                  value={externalAgentKindDisplayName ?? "Unknown"}
                  readOnly
                  className="bg-muted/50"
                />
              )}
              {mode === "edit" ? (
                <p className="text-xs text-muted-foreground">
                  The external agent kind is fixed after creation.
                </p>
              ) : null}
            </div>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}modelProviderId`}>
              Model Provider
              {isExternalAgent ? (
                <span className="ml-2 text-xs text-muted-foreground">(Optional)</span>
              ) : null}
            </Label>
            <SearchableSelect
              id={`${idPrefix}modelProviderId`}
              ariaLabel="Model Provider"
              value={modelProviderId}
              onValueChange={setModelProviderId}
              options={modelProviderOptions}
              placeholder={
                isExternalAgent
                  ? "Optional: Select a model provider..."
                  : "Select a model provider..."
              }
              searchPlaceholder="Search model providers..."
              isLoading={modelProvidersQuery.isLoading}
              clearable={isExternalAgent}
            />
          </div>

          {isExternalAgent ? (
            <ExternalAgentNotice>
              External agents do not support turn summary configuration.
            </ExternalAgentNotice>
          ) : null}

          <div className="flex items-start justify-between gap-4 rounded-lg border bg-background px-4 py-3">
            <div className="space-y-1">
              <Label htmlFor={`${idPrefix}enableSummary`} className="cursor-pointer">
                Generate Turn Summary
              </Label>
              <p className="text-xs text-muted-foreground">
                Append a Markdown summary after each successful turn using the selected Summary
                Model Provider.
              </p>
            </div>
            <Switch
              id={`${idPrefix}enableSummary`}
              checked={enableSummary}
              onCheckedChange={setEnableSummary}
              disabled={isExternalAgent}
            />
          </div>

          {enableSummary ? (
            <div className="grid gap-2">
              <Label htmlFor={`${idPrefix}summaryModelProviderId`}>
                Summary Model Provider
                {!isExternalAgent && !summaryModelProviderId ? (
                  <span className="ml-2 text-xs text-muted-foreground">
                    (Defaults to Agent Model Provider)
                  </span>
                ) : null}
              </Label>
              <SearchableSelect
                id={`${idPrefix}summaryModelProviderId`}
                ariaLabel="Summary Model Provider"
                value={effectiveSummaryModelProviderId}
                onValueChange={setSummaryModelProviderId}
                options={modelProviderOptions}
                placeholder="Select a summary model provider..."
                searchPlaceholder="Search model providers..."
                isLoading={modelProvidersQuery.isLoading}
                clearable={!isExternalAgent && Boolean(summaryModelProviderId)}
                disabled={isExternalAgent}
              />
            </div>
          ) : null}
        </div>
      </div>

      <div className="min-h-0 overflow-hidden bg-background">
        <Tabs defaultValue="system-prompt" className="flex h-full min-h-0 flex-col">
          <div className="shrink-0 overflow-x-auto agw-scrollbar border-b px-6 py-3">
            <TabsList className="h-auto w-max">
              <TabsTrigger value="system-prompt">Instructions</TabsTrigger>
              <TabsTrigger value="tools">Tools</TabsTrigger>
              <TabsTrigger value="skills">Skills</TabsTrigger>
              <TabsTrigger value="mcp-tool-servers">MCP Tool Server</TabsTrigger>
              <TabsTrigger value="connections">Integrations</TabsTrigger>
              <TabsTrigger value="environment-variables">Environment Variables</TabsTrigger>
              <TabsTrigger value="extra-settings">Extra Settings</TabsTrigger>
            </TabsList>
            <p className="mt-2 max-w-4xl text-xs text-muted-foreground">
              Agw recommends configuring Skills, Tools, MCP Tool Servers, Integrations, and
              Environment Variables in the Project. When the agent runs, it merges the
              configurations from both the agent and the project.
            </p>
          </div>

          <TabsContent
            value="system-prompt"
            className="m-0 flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto agw-scrollbar p-6"
          >
            <div>
              <h3 className="font-medium">Instructions</h3>
              <p className="mt-1 text-sm text-muted-foreground">
                Define the instructions and operating context for this agent.
              </p>
            </div>
            {isExternalAgent ? (
              <ExternalAgentNotice>
                External agents do not support instructions configuration.
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

          <TabsContent
            value="skills"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <SkillsPanel
              idPrefix={idPrefix}
              skillsQuery={skillsQuery}
              selectedSkillIds={selectedSkillIds}
              toggleSkill={toggleSkill}
              disabled={isExternalAgent}
              notice={
                isExternalAgent ? (
                  <ExternalAgentNotice>
                    External agents do not support skill configuration.
                  </ExternalAgentNotice>
                ) : null
              }
            />
          </TabsContent>

          <TabsContent
            value="tools"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <ToolsPanel
              scope="agent"
              idPrefix={idPrefix}
              toolsQuery={toolsQuery}
              values={tools}
              setValues={setTools}
              agentOptions={agentOptions}
              disabled={isExternalAgent}
              notice={
                isExternalAgent ? (
                  <ExternalAgentNotice>
                    External agents do not support tool configuration.
                  </ExternalAgentNotice>
                ) : null
              }
            />
          </TabsContent>

          <TabsContent
            value="mcp-tool-servers"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <McpToolServersPanel
              idPrefix={idPrefix}
              mcpToolServersQuery={mcpToolServersQuery}
              selectedMcpToolServerIds={selectedMcpToolServerIds}
              toggleMcpToolServer={toggleMcpToolServer}
              disabled={isExternalAgent}
              notice={
                isExternalAgent ? (
                  <ExternalAgentNotice>
                    External agents do not support MCP tool server configuration.
                  </ExternalAgentNotice>
                ) : null
              }
            />
          </TabsContent>

          <TabsContent
            value="connections"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <ConnectionsPanel
              idPrefix={idPrefix}
              connectionOptions={connectionOptions}
              selectedConnectionIds={selectedConnectionIds}
              toggleConnection={toggleConnection}
              disabled={isExternalAgent}
              notice={
                isExternalAgent ? (
                  <ExternalAgentNotice>
                    External agents do not support integration configuration.
                  </ExternalAgentNotice>
                ) : null
              }
            />
          </TabsContent>

          <TabsContent
            value="environment-variables"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <EnvironmentVariablesPanel
              entries={environmentVariables}
              setEntries={setEnvironmentVariables}
              idPrefix={idPrefix}
              ownerLabel="agent"
            />
          </TabsContent>

          <TabsContent
            value="extra-settings"
            className="m-0 flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto agw-scrollbar p-6"
          >
            <div>
              <h3 className="font-medium">Extra Settings</h3>
              <p className="mt-1 text-sm text-muted-foreground">
                {canEditExtra
                  ? "Configure the JSON options for this external agent."
                  : "Extra Settings are available for external agents."}
              </p>
            </div>
            <div className="flex min-h-0 flex-1 flex-col gap-2">
              <Label htmlFor={`${idPrefix}extra`}>JSON object</Label>
              <Textarea
                id={`${idPrefix}extra`}
                value={extra}
                onChange={(event) => setExtra(event.target.value)}
                placeholder="{}"
                readOnly={!canEditExtra}
                aria-invalid={Boolean(extraError)}
                className={`min-h-80 flex-1 resize-none font-mono text-xs ${
                  canEditExtra ? "" : "bg-muted/50"
                }`}
              />
              {extraError ? <p className="text-xs text-destructive">{extraError}</p> : null}
            </div>
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
