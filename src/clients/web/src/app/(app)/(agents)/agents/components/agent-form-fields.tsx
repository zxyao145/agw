import * as React from "react";
import { UseQueryResult } from "@tanstack/react-query";

import {
  AppsPanel,
  EnvironmentVariablesPanel,
  McpToolServersPanel,
  SkillsPanel,
  ToolsPanel,
  type AppInstanceOption,
  type EnvironmentVariableEntry,
  type McpToolServerDto,
  type SkillDto,
  type ToolInfo,
} from "@/components/definition-capabilities";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";

import { getAgentExtraSettingsError } from "./agent-extra-settings";
import type { ModelProviderDto } from "./types";

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
  summaryModelProviderId: string;
  setSummaryModelProviderId: (value: string) => void;
  enableSummary: boolean;
  setEnableSummary: (value: boolean) => void;
  agentType: string;
  extra: string;
  setExtra?: (value: string) => void;
  environmentVariables: EnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: EnvironmentVariableEntry[]) => void;
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
  summaryModelProviderId,
  setSummaryModelProviderId,
  enableSummary,
  setEnableSummary,
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
  const isExternalAgent = agentType === "1";
  const effectiveSummaryModelProviderId = isExternalAgent
    ? summaryModelProviderId
    : summaryModelProviderId || modelProviderId;
  const canEditExtra = mode === "edit" && isExternalAgent;
  const extraError = canEditExtra ? getAgentExtraSettingsError(extra) : null;

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
              <Select
                value={effectiveSummaryModelProviderId}
                onValueChange={setSummaryModelProviderId}
              >
                <SelectTrigger id={`${idPrefix}summaryModelProviderId`} className="w-full">
                  <SelectValue placeholder="Select a summary model provider..." />
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
          ) : null}

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
              <TabsTrigger value="system-prompt">Instructions</TabsTrigger>
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

          <TabsContent value="skills" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <SkillsPanel
              dialogPortalContainer={dialogPortalContainer}
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

          <TabsContent value="tools" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <ToolsPanel
              dialogPortalContainer={dialogPortalContainer}
              idPrefix={idPrefix}
              toolsQuery={toolsQuery}
              selectedTools={selectedTools}
              toggleTool={toggleTool}
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

          <TabsContent value="mcp-tool-servers" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <McpToolServersPanel
              dialogPortalContainer={dialogPortalContainer}
              idPrefix={idPrefix}
              mcpToolServersQuery={mcpToolServersQuery}
              selectedMcpToolServerIds={selectedMcpToolServerIds}
              toggleMcpToolServer={toggleMcpToolServer}
            />
          </TabsContent>

          <TabsContent value="apps" className="m-0 min-h-0 flex-1 overflow-y-auto p-6">
            <AppsPanel
              dialogPortalContainer={dialogPortalContainer}
              idPrefix={idPrefix}
              appOptions={appOptions}
              selectedAppInstanceIds={selectedAppInstanceIds}
              appSearchTerm={appSearchTerm}
              setAppSearchTerm={setAppSearchTerm}
              filteredAppOptions={filteredAppOptions}
              toggleAppInstance={toggleAppInstance}
            />
          </TabsContent>

          <TabsContent
            value="environment-variables"
            className="m-0 min-h-0 flex-1 overflow-y-auto p-6"
          >
            <EnvironmentVariablesPanel
              entries={environmentVariables}
              setEntries={setEnvironmentVariables}
              idPrefix={idPrefix}
              ownerLabel="agent"
            />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
