import type { UseQueryResult } from "@agw/components/query";

import {
  ConnectionsPanel,
  EnvironmentVariablesPanel,
  McpToolServersPanel,
  SkillsPanel,
  ToolsPanel,
  type ConnectionOption,
  type EnvironmentVariableEntry,
  type McpToolServerDto,
  type SkillDto,
  type ToolInfo,
} from "@agw/integrations";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@agw/components";
import { Textarea } from "@agw/components";

export interface ProjectFormFieldsProps {
  name: string;
  setName: (value: string) => void;
  description: string;
  setDescription: (value: string) => void;
  workspace: string;
  setWorkspace: (value: string) => void;
  extraSetting: string;
  setExtraSetting: (value: string) => void;
  extraSettingError: string | null;
  environmentVariables: EnvironmentVariableEntry[];
  setEnvironmentVariables: (entries: EnvironmentVariableEntry[]) => void;
  selectedSkillIds: string[];
  connectionOptions: ConnectionOption[];
  selectedConnectionIds: string[];
  selectedTools: string[];
  skillsQuery: UseQueryResult<SkillDto[], Error>;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  mcpToolServersQuery: UseQueryResult<McpToolServerDto[], Error>;
  selectedMcpToolServerIds: string[];
  toggleSkill: (skillId: string) => void;
  toggleConnection: (connectionId: string) => void;
  toggleTool: (toolName: string) => void;
  toggleMcpToolServer: (mcpToolServerId: string) => void;
  idPrefix?: string;
}

export function ProjectFormFields({
  name,
  setName,
  description,
  setDescription,
  workspace,
  setWorkspace,
  extraSetting,
  setExtraSetting,
  extraSettingError,
  environmentVariables,
  setEnvironmentVariables,
  selectedSkillIds,
  connectionOptions,
  selectedConnectionIds,
  selectedTools,
  skillsQuery,
  toolsQuery,
  mcpToolServersQuery,
  selectedMcpToolServerIds,
  toggleSkill,
  toggleConnection,
  toggleTool,
  toggleMcpToolServer,
  idPrefix = "",
}: ProjectFormFieldsProps) {
  return (
    <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,45%)_minmax(0,1fr)] overflow-hidden lg:grid-cols-[360px_minmax(0,1fr)] lg:grid-rows-1">
      <div className="overflow-y-auto agw-scrollbar border-b bg-muted/20 p-4 lg:border-r lg:border-b-0">
        <div className="grid gap-5">
          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}name`}>Name</Label>
            <Input
              id={`${idPrefix}name`}
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="demo-project"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}description`}>Description</Label>
            <Textarea
              id={`${idPrefix}description`}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="A project groups ordered tasks."
              rows={4}
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}workspace`}>Workspace (Optional)</Label>
            <Input
              id={`${idPrefix}workspace`}
              value={workspace}
              onChange={(event) => setWorkspace(event.target.value)}
              placeholder="~/.agw/demo-project"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}projectType`}>Project Type</Label>
            <Input
              id={`${idPrefix}projectType`}
              value="User Defined"
              readOnly
              className="bg-muted/50"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}extraSetting`}>Extra Settings (JSON)</Label>
            <Textarea
              id={`${idPrefix}extraSetting`}
              value={extraSetting}
              onChange={(event) => setExtraSetting(event.target.value)}
              placeholder="{}"
              rows={9}
              aria-invalid={Boolean(extraSettingError)}
              className="font-mono text-xs"
            />
            {extraSettingError ? (
              <p className="text-xs text-destructive">{extraSettingError}</p>
            ) : (
              <p className="text-xs text-muted-foreground">
                Optional JSON settings. Objects, arrays, and scalar values are supported.
              </p>
            )}
          </div>
        </div>
      </div>

      <div className="min-h-0 overflow-hidden bg-background">
        <Tabs defaultValue="skills" className="flex h-full min-h-0 flex-col">
          <div className="shrink-0 overflow-x-auto agw-scrollbar border-b px-6 py-3">
            <TabsList className="h-auto w-max">
              <TabsTrigger value="skills">Skills</TabsTrigger>
              <TabsTrigger value="tools">Tools</TabsTrigger>
              <TabsTrigger value="mcp-tool-servers">MCP Tool Server</TabsTrigger>
              <TabsTrigger value="connections">Connections</TabsTrigger>
              <TabsTrigger value="environment-variables">Environment Variables</TabsTrigger>
            </TabsList>
          </div>

          <TabsContent
            value="skills"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <SkillsPanel
              idPrefix={idPrefix}
              ownerLabel="project"
              skillsQuery={skillsQuery}
              selectedSkillIds={selectedSkillIds}
              toggleSkill={toggleSkill}
            />
          </TabsContent>

          <TabsContent
            value="tools"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <ToolsPanel
              idPrefix={idPrefix}
              ownerLabel="project"
              toolsQuery={toolsQuery}
              selectedTools={selectedTools}
              toggleTool={toggleTool}
            />
          </TabsContent>

          <TabsContent
            value="mcp-tool-servers"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <McpToolServersPanel
              idPrefix={idPrefix}
              ownerLabel="project"
              mcpToolServersQuery={mcpToolServersQuery}
              selectedMcpToolServerIds={selectedMcpToolServerIds}
              toggleMcpToolServer={toggleMcpToolServer}
            />
          </TabsContent>

          <TabsContent
            value="connections"
            className="m-0 min-h-0 flex-1 overflow-y-auto agw-scrollbar p-6"
          >
            <ConnectionsPanel
              idPrefix={idPrefix}
              ownerLabel="project"
              connectionOptions={connectionOptions}
              selectedConnectionIds={selectedConnectionIds}
              toggleConnection={toggleConnection}
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
              ownerLabel="project"
            />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
