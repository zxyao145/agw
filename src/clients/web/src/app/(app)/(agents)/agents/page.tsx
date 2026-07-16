"use client";

import * as React from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { TablePagination } from "@/components/table-pagination";
import { Button } from "@/components/ui/button";
import { DEFAULT_PAGE_SIZE, getClampedPageIndex, type PagedResult } from "@/lib/pagination";

import type {
  AgentDto,
  AgentCreateRequest,
  AgentUpdateRequest,
  ToolInfo,
  ModelProviderDto,
  McpToolServerDto,
  SkillDto,
} from "./components/types";
import { getApiErrorMessage } from "@/api/utils";
import type { ConnectionOption } from "./components/connection-selector";
import {
  toAgentEnvironmentVariableEntries,
  type AgentEnvironmentVariableEntry,
} from "./components/agent-environment-variables";
import { CreateAgentDialog } from "./components/create-agent-dialog";
import { EditAgentDialog } from "./components/edit-agent-dialog";
import { DeleteAgentDialog } from "./components/delete-agent-dialog";
import { ExecuteAgentDrawer } from "./components/execute-agent-drawer";
import { AgentsTable } from "./components/agents-table";

export default function AgentsPage() {
  const queryClient = useQueryClient();
  const [pageIndex, setPageIndex] = React.useState(1);
  const [pageSize, setPageSize] = React.useState(DEFAULT_PAGE_SIZE);

  const agentsQuery = useQuery({
    queryKey: ["agents", "paged", pageIndex, pageSize],
    queryFn: async () => {
      return (await apiGet("/api/agents/paged", {
        params: { query: { pageIndex, pageSize } },
      })) as unknown as PagedResult<AgentDto>;
    },
    placeholderData: keepPreviousData,
  });

  const total = Number(agentsQuery.data?.total ?? 0);

  React.useEffect(() => {
    if (!agentsQuery.data) return;
    const clampedPageIndex = getClampedPageIndex(total, pageIndex, pageSize);
    if (clampedPageIndex !== pageIndex) {
      setPageIndex(clampedPageIndex);
    }
  }, [agentsQuery.data, pageIndex, pageSize, total]);

  const modelProvidersQuery = useQuery({
    queryKey: ["modelProviders"],
    queryFn: async () => {
      return (await apiGet("/api/model-providers")) as unknown as ModelProviderDto[];
    },
  });

  const toolsQuery = useQuery({
    queryKey: ["tools"],
    queryFn: async () => {
      return (await apiGet("/api/tools")) as unknown as ToolInfo[];
    },
  });

  const mcpToolServersQuery = useQuery({
    queryKey: ["mcpToolServers"],
    queryFn: async () => {
      return (await apiGet("/api/mcp-tool-servers")) as unknown as McpToolServerDto[];
    },
  });

  const skillsQuery = useQuery({
    queryKey: ["skills"],
    queryFn: async () => {
      return (await apiGet("/api/skills")) as unknown as SkillDto[];
    },
  });

  const connectionsQuery = useQuery({
    queryKey: ["connections"],
    queryFn: async () => {
      return (await apiGet("/api/integrations/connections")) as unknown as ConnectionOption[];
    },
  });

  // Create dialog state
  const [createOpen, setCreateOpen] = React.useState(false);
  const [displayName, setDisplayName] = React.useState("");
  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [systemPrompt, setSystemPrompt] = React.useState("");
  const [modelProviderId, setModelProviderId] = React.useState("");
  const [summaryModelProviderId, setSummaryModelProviderId] = React.useState("");
  const [enableSummary, setEnableSummary] = React.useState(false);
  const [selectedSkillIds, setSelectedSkillIds] = React.useState<string[]>([]);
  const [selectedConnectionIds, setSelectedConnectionIds] = React.useState<string[]>([]);
  const [selectedTools, setSelectedTools] = React.useState<string[]>([]);
  const [selectedMcpToolServerIds, setSelectedMcpToolServerIds] = React.useState<string[]>([]);
  const [environmentVariables, setEnvironmentVariables] = React.useState<
    AgentEnvironmentVariableEntry[]
  >([]);

  // Edit dialog state
  const [editOpen, setEditOpen] = React.useState(false);
  const [editingAgent, setEditingAgent] = React.useState<AgentDto | null>(null);
  const [editDisplayName, setEditDisplayName] = React.useState("");
  const [editName, setEditName] = React.useState("");
  const [editDescription, setEditDescription] = React.useState("");
  const [editSystemPrompt, setEditSystemPrompt] = React.useState("");
  const [editModelProviderId, setEditModelProviderId] = React.useState("");
  const [editSummaryModelProviderId, setEditSummaryModelProviderId] = React.useState("");
  const [editEnableSummary, setEditEnableSummary] = React.useState(false);
  const [editSelectedSkillIds, setEditSelectedSkillIds] = React.useState<string[]>([]);
  const [editSelectedConnectionIds, setEditSelectedConnectionIds] = React.useState<string[]>([]);
  const [editSelectedTools, setEditSelectedTools] = React.useState<string[]>([]);
  const [editSelectedMcpToolServerIds, setEditSelectedMcpToolServerIds] = React.useState<string[]>(
    [],
  );
  const [editExtra, setEditExtra] = React.useState("");
  const [editEnvironmentVariables, setEditEnvironmentVariables] = React.useState<
    AgentEnvironmentVariableEntry[]
  >([]);

  // Delete dialog state
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [deletingAgent, setDeletingAgent] = React.useState<AgentDto | null>(null);

  // Execute sheet state
  const [executeOpen, setExecuteOpen] = React.useState(false);
  const [executingAgent, setExecutingAgent] = React.useState<AgentDto | null>(null);

  const createAgentMutation = useMutation({
    mutationFn: async (body: AgentCreateRequest) => {
      return await apiPost("/api/agents", { body });
    },
    onSuccess: async () => {
      toast.success("Agent created");
      setPageIndex(1);
      setCreateOpen(false);
      setDisplayName("");
      setName("");
      setDescription("");
      setSystemPrompt("");
      setModelProviderId("");
      setSummaryModelProviderId("");
      setEnableSummary(false);
      setSelectedSkillIds([]);
      setSelectedConnectionIds([]);
      setSelectedTools([]);
      setSelectedMcpToolServerIds([]);
      setEnvironmentVariables([]);
      await queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateAgentMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: AgentUpdateRequest }) => {
      return await apiPut("/api/agents/{id}", {
        params: { path: { id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Agent updated");
      setPageIndex(1);
      setEditOpen(false);
      setEditingAgent(null);
      setEditSummaryModelProviderId("");
      setEditEnableSummary(false);
      setEditSelectedSkillIds([]);
      setEditSelectedConnectionIds([]);
      setEditEnvironmentVariables([]);
      await queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteAgentMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/agents/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Agent deleted");
      setDeleteOpen(false);
      setDeletingAgent(null);
      setPageIndex(getClampedPageIndex(Math.max(0, total - 1), pageIndex, pageSize));
      await queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const handleEdit = (agent: AgentDto) => {
    if (updateAgentMutation.isPending) {
      return;
    }

    setEditingAgent(agent);
    setEditDisplayName(agent.displayName);
    setEditName(agent.name);
    setEditDescription(agent.description);
    setEditSystemPrompt(agent.systemPrompt);
    setEditModelProviderId(agent.modelProviderId ?? "");
    setEditSummaryModelProviderId(agent.summaryModelProviderId ?? "");
    setEditEnableSummary(agent.enableSummary);
    setEditExtra(agent.extra || "");
    setEditEnvironmentVariables(toAgentEnvironmentVariableEntries(agent.environmentVariables));
    setEditSelectedSkillIds(agent.agentSkillRelations?.map((relation) => relation.skillId) ?? []);
    setEditSelectedConnectionIds(
      agent.agentConnectionRelations?.map((relation) => relation.connectionId) ?? [],
    );
    try {
      const tools = agent.tools ? JSON.parse(agent.tools) : [];
      setEditSelectedTools(Array.isArray(tools) ? tools : []);
    } catch {
      setEditSelectedTools([]);
    }
    setEditSelectedMcpToolServerIds(agent.agentMcpToolServers?.map((x) => x.mcpToolServerId) ?? []);
    setEditOpen(true);
  };

  const handleDelete = (agent: AgentDto) => {
    setDeletingAgent(agent);
    setDeleteOpen(true);
  };

  const handleExecute = (agent: AgentDto) => {
    setExecutingAgent(agent);
    setExecuteOpen(true);
  };

  const toggleTool = (toolName: string, isEdit: boolean = false) => {
    if (isEdit) {
      setEditSelectedTools((prev) =>
        prev.includes(toolName) ? prev.filter((t) => t !== toolName) : [...prev, toolName],
      );
    } else {
      setSelectedTools((prev) =>
        prev.includes(toolName) ? prev.filter((t) => t !== toolName) : [...prev, toolName],
      );
    }
  };

  const toggleSkill = (skillId: string, isEdit: boolean = false) => {
    if (isEdit) {
      setEditSelectedSkillIds((prev) =>
        prev.includes(skillId) ? prev.filter((id) => id !== skillId) : [...prev, skillId],
      );
      return;
    }

    setSelectedSkillIds((prev) =>
      prev.includes(skillId) ? prev.filter((id) => id !== skillId) : [...prev, skillId],
    );
  };

  const toggleConnection = (connectionId: string, isEdit: boolean = false) => {
    if (isEdit) {
      setEditSelectedConnectionIds((prev) =>
        prev.includes(connectionId)
          ? prev.filter((id) => id !== connectionId)
          : [...prev, connectionId],
      );
      return;
    }

    setSelectedConnectionIds((prev) =>
      prev.includes(connectionId)
        ? prev.filter((id) => id !== connectionId)
        : [...prev, connectionId],
    );
  };

  const toggleMcpToolServer = (mcpToolServerId: string, isEdit: boolean = false) => {
    if (isEdit) {
      setEditSelectedMcpToolServerIds((prev) =>
        prev.includes(mcpToolServerId)
          ? prev.filter((id) => id !== mcpToolServerId)
          : [...prev, mcpToolServerId],
      );
      return;
    }

    setSelectedMcpToolServerIds((prev) =>
      prev.includes(mcpToolServerId)
        ? prev.filter((id) => id !== mcpToolServerId)
        : [...prev, mcpToolServerId],
    );
  };

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Agents</h1>
          <p className="text-sm text-muted-foreground">
            Manage agents. Creating an agent requires a Model Provider.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => agentsQuery.refetch()}
            disabled={agentsQuery.isFetching}
          >
            Refresh
          </Button>

          <CreateAgentDialog
            open={createOpen}
            setOpen={setCreateOpen}
            displayName={displayName}
            setDisplayName={setDisplayName}
            name={name}
            setName={setName}
            description={description}
            setDescription={setDescription}
            systemPrompt={systemPrompt}
            setSystemPrompt={setSystemPrompt}
            modelProviderId={modelProviderId}
            setModelProviderId={setModelProviderId}
            summaryModelProviderId={summaryModelProviderId}
            setSummaryModelProviderId={setSummaryModelProviderId}
            enableSummary={enableSummary}
            setEnableSummary={setEnableSummary}
            environmentVariables={environmentVariables}
            setEnvironmentVariables={setEnvironmentVariables}
            selectedSkillIds={selectedSkillIds}
            connectionOptions={connectionsQuery.data ?? []}
            selectedConnectionIds={selectedConnectionIds}
            selectedTools={selectedTools}
            modelProvidersQuery={modelProvidersQuery}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            createAgentMutation={createAgentMutation}
            toggleSkill={(skillId) => toggleSkill(skillId, false)}
            toggleConnection={(connectionId) => toggleConnection(connectionId, false)}
            toggleTool={(toolName) => toggleTool(toolName, false)}
            toggleMcpToolServer={(mcpToolServerId) => toggleMcpToolServer(mcpToolServerId, false)}
          />
        </div>
      </div>

      <AgentsTable
        agentsQuery={agentsQuery}
        onEdit={handleEdit}
        onDelete={handleDelete}
        onExecute={handleExecute}
      />

      <TablePagination
        pageIndex={pageIndex}
        pageSize={pageSize}
        total={total}
        isFetching={agentsQuery.isFetching}
        onPageIndexChange={setPageIndex}
        onPageSizeChange={(value) => {
          setPageSize(value);
          setPageIndex(1);
        }}
      />

      <EditAgentDialog
        open={editOpen}
        setOpen={setEditOpen}
        editingAgent={editingAgent}
        displayName={editDisplayName}
        setDisplayName={setEditDisplayName}
        name={editName}
        setName={setEditName}
        description={editDescription}
        setDescription={setEditDescription}
        systemPrompt={editSystemPrompt}
        setSystemPrompt={setEditSystemPrompt}
        modelProviderId={editModelProviderId}
        setModelProviderId={setEditModelProviderId}
        summaryModelProviderId={editSummaryModelProviderId}
        setSummaryModelProviderId={setEditSummaryModelProviderId}
        enableSummary={editEnableSummary}
        setEnableSummary={setEditEnableSummary}
        extra={editExtra}
        setExtra={setEditExtra}
        environmentVariables={editEnvironmentVariables}
        setEnvironmentVariables={setEditEnvironmentVariables}
        selectedSkillIds={editSelectedSkillIds}
        connectionOptions={connectionsQuery.data ?? []}
        selectedConnectionIds={editSelectedConnectionIds}
        selectedTools={editSelectedTools}
        modelProvidersQuery={modelProvidersQuery}
        skillsQuery={skillsQuery}
        toolsQuery={toolsQuery}
        mcpToolServersQuery={mcpToolServersQuery}
        selectedMcpToolServerIds={editSelectedMcpToolServerIds}
        updateAgentMutation={updateAgentMutation}
        toggleSkill={(skillId) => toggleSkill(skillId, true)}
        toggleConnection={(connectionId) => toggleConnection(connectionId, true)}
        toggleTool={(toolName) => toggleTool(toolName, true)}
        toggleMcpToolServer={(mcpToolServerId) => toggleMcpToolServer(mcpToolServerId, true)}
      />

      <DeleteAgentDialog
        open={deleteOpen}
        setOpen={setDeleteOpen}
        deletingAgent={deletingAgent}
        deleteAgentMutation={deleteAgentMutation}
      />

      <ExecuteAgentDrawer
        open={executeOpen}
        setOpen={setExecuteOpen}
        executingAgent={executingAgent}
      />
    </div>
  );
}
