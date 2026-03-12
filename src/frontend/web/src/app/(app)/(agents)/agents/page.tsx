"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { Button } from "@/components/ui/button";

import type {
  AgentDto,
  AgentCreateRequest,
  AgentUpdateRequest,
  ToolInfo,
  ModelProviderApiKeyDto,
  McpToolServerDto,
} from "./components/types";
import { getApiErrorMessage } from "./components/utils";
import { CreateAgentDialog } from "./components/create-agent-dialog";
import { EditAgentDialog } from "./components/edit-agent-dialog";
import { DeleteAgentDialog } from "./components/delete-agent-dialog";
import { ExecuteAgentDrawer } from "./components/execute-agent-drawer";
import { AgentsTable } from "./components/agents-table";

export default function AgentsPage() {
  const queryClient = useQueryClient();

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      return (await apiGet("/api/agents")) as unknown as AgentDto[];
    },
  });

  const modelProviderApiKeysQuery = useQuery({
    queryKey: ["modelProviderApiKeys"],
    queryFn: async () => {
      return (await apiGet(
        "/api/model-provider-keys"
      )) as unknown as ModelProviderApiKeyDto[];
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

  // Create dialog state
  const [createOpen, setCreateOpen] = React.useState(false);
  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [systemPrompt, setSystemPrompt] = React.useState("");
  const [modelProviderApiKeyId, setModelProviderApiKeyId] = React.useState("");
  const [selectedTools, setSelectedTools] = React.useState<string[]>([]);
  const [toolSearchTerm, setToolSearchTerm] = React.useState("");
  const [selectedMcpToolServerIds, setSelectedMcpToolServerIds] = React.useState<string[]>([]);
  const [mcpToolServerSearchTerm, setMcpToolServerSearchTerm] = React.useState("");
  const [agentType, setAgentType] = React.useState<string>("0");
  const [extra, setExtra] = React.useState("");

  // Edit dialog state
  const [editOpen, setEditOpen] = React.useState(false);
  const [editingAgent, setEditingAgent] = React.useState<AgentDto | null>(null);
  const [editName, setEditName] = React.useState("");
  const [editDescription, setEditDescription] = React.useState("");
  const [editSystemPrompt, setEditSystemPrompt] = React.useState("");
  const [editModelProviderApiKeyId, setEditModelProviderApiKeyId] =
    React.useState("");
  const [editSelectedTools, setEditSelectedTools] = React.useState<string[]>(
    []
  );
  const [editToolSearchTerm, setEditToolSearchTerm] = React.useState("");
  const [editSelectedMcpToolServerIds, setEditSelectedMcpToolServerIds] = React.useState<string[]>([]);
  const [editMcpToolServerSearchTerm, setEditMcpToolServerSearchTerm] = React.useState("");
  const [editAgentType, setEditAgentType] = React.useState<string>("0");
  const [editExtra, setEditExtra] = React.useState("");

  // Delete dialog state
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [deletingAgent, setDeletingAgent] = React.useState<AgentDto | null>(
    null
  );

  // Execute sheet state
  const [executeOpen, setExecuteOpen] = React.useState(false);
  const [executingAgent, setExecutingAgent] = React.useState<AgentDto | null>(
    null
  );

  const createAgentMutation = useMutation({
    mutationFn: async (body: AgentCreateRequest) => {
      return await apiPost("/api/agents", { body });
    },
    onSuccess: async () => {
      toast.success("Agent created");
      setCreateOpen(false);
      setName("");
      setDescription("");
      setSystemPrompt("");
      setModelProviderApiKeyId("");
      setSelectedTools([]);
      setToolSearchTerm("");
      setSelectedMcpToolServerIds([]);
      setMcpToolServerSearchTerm("");
      setAgentType("0");
      setExtra("");
      await queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateAgentMutation = useMutation({
    mutationFn: async ({
      id,
      body,
    }: {
      id: string;
      body: AgentUpdateRequest;
    }) => {
      return await apiPut("/api/agents/{id}", {
        params: { path: { id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Agent updated");
      setEditOpen(false);
      setEditingAgent(null);
      await queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteAgentMutation = useMutation({
    mutationFn: async (id: string) => {
      // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
      return await apiDelete("/api/agents/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Agent deleted");
      setDeleteOpen(false);
      setDeletingAgent(null);
      await queryClient.invalidateQueries({ queryKey: ["agents"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const handleEdit = (agent: AgentDto) => {
    setEditingAgent(agent);
    setEditName(agent.name);
    setEditDescription(agent.description);
    setEditSystemPrompt(agent.systemPrompt);
    setEditModelProviderApiKeyId(agent.modelProviderApiKeyId);
    setEditAgentType(agent.type.toString());
    setEditExtra(agent.extra || "");
    try {
      const tools = agent.tools ? JSON.parse(agent.tools) : [];
      setEditSelectedTools(Array.isArray(tools) ? tools : []);
    } catch {
      setEditSelectedTools([]);
    }
    setEditSelectedMcpToolServerIds(
      agent.agentMcpToolServers?.map((x) => x.mcpToolServerId) ?? []
    );
    setEditToolSearchTerm("");
    setEditMcpToolServerSearchTerm("");
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
        prev.includes(toolName)
          ? prev.filter((t) => t !== toolName)
          : [...prev, toolName]
      );
    } else {
      setSelectedTools((prev) =>
        prev.includes(toolName)
          ? prev.filter((t) => t !== toolName)
          : [...prev, toolName]
      );
    }
  };

  const toggleMcpToolServer = (mcpToolServerId: string, isEdit: boolean = false) => {
    if (isEdit) {
      setEditSelectedMcpToolServerIds((prev) =>
        prev.includes(mcpToolServerId)
          ? prev.filter((id) => id !== mcpToolServerId)
          : [...prev, mcpToolServerId]
      );
      return;
    }

    setSelectedMcpToolServerIds((prev) =>
      prev.includes(mcpToolServerId)
        ? prev.filter((id) => id !== mcpToolServerId)
        : [...prev, mcpToolServerId]
    );
  };

  const filteredTools = React.useMemo(() => {
    if (!toolsQuery.data) return [];
    return toolsQuery.data.filter(
      (tool) =>
        tool.name.toLowerCase().includes(toolSearchTerm.toLowerCase()) ||
        tool.description.toLowerCase().includes(toolSearchTerm.toLowerCase()) ||
        tool.category.toLowerCase().includes(toolSearchTerm.toLowerCase())
    );
  }, [toolsQuery.data, toolSearchTerm]);

  const filteredEditTools = React.useMemo(() => {
    if (!toolsQuery.data) return [];
    return toolsQuery.data.filter(
      (tool) =>
        tool.name.toLowerCase().includes(editToolSearchTerm.toLowerCase()) ||
        tool.description
          .toLowerCase()
          .includes(editToolSearchTerm.toLowerCase()) ||
        tool.category.toLowerCase().includes(editToolSearchTerm.toLowerCase())
    );
  }, [toolsQuery.data, editToolSearchTerm]);

  const filteredMcpToolServers = React.useMemo(() => {
    if (!mcpToolServersQuery.data) return [];
    return mcpToolServersQuery.data.filter((server) =>
      server.name.toLowerCase().includes(mcpToolServerSearchTerm.toLowerCase())
    );
  }, [mcpToolServersQuery.data, mcpToolServerSearchTerm]);

  const filteredEditMcpToolServers = React.useMemo(() => {
    if (!mcpToolServersQuery.data) return [];
    return mcpToolServersQuery.data.filter((server) =>
      server.name
        .toLowerCase()
        .includes(editMcpToolServerSearchTerm.toLowerCase())
    );
  }, [mcpToolServersQuery.data, editMcpToolServerSearchTerm]);

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Agents</h1>
          <p className="text-sm text-muted-foreground">
            Manage agents. Creating an agent requires a Model Provider API Key.
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
            name={name}
            setName={setName}
            description={description}
            setDescription={setDescription}
            systemPrompt={systemPrompt}
            setSystemPrompt={setSystemPrompt}
            modelProviderApiKeyId={modelProviderApiKeyId}
            setModelProviderApiKeyId={setModelProviderApiKeyId}
            agentType={agentType}
            setAgentType={setAgentType}
            extra={extra}
            setExtra={setExtra}
            selectedTools={selectedTools}
            setSelectedTools={setSelectedTools}
            toolSearchTerm={toolSearchTerm}
            setToolSearchTerm={setToolSearchTerm}
            filteredTools={filteredTools}
            modelProviderApiKeysQuery={modelProviderApiKeysQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            mcpToolServerSearchTerm={mcpToolServerSearchTerm}
            setMcpToolServerSearchTerm={setMcpToolServerSearchTerm}
            filteredMcpToolServers={filteredMcpToolServers}
            createAgentMutation={createAgentMutation}
            toggleTool={(toolName) => toggleTool(toolName, false)}
            toggleMcpToolServer={(mcpToolServerId) =>
              toggleMcpToolServer(mcpToolServerId, false)
            }
          />
        </div>
      </div>

      <AgentsTable
        agentsQuery={agentsQuery}
        onEdit={handleEdit}
        onDelete={handleDelete}
        onExecute={handleExecute}
      />

      <EditAgentDialog
        open={editOpen}
        setOpen={setEditOpen}
        editingAgent={editingAgent}
        name={editName}
        setName={setEditName}
        description={editDescription}
        setDescription={setEditDescription}
        systemPrompt={editSystemPrompt}
        setSystemPrompt={setEditSystemPrompt}
        modelProviderApiKeyId={editModelProviderApiKeyId}
        setModelProviderApiKeyId={setEditModelProviderApiKeyId}
        agentType={editAgentType}
        setAgentType={setEditAgentType}
        extra={editExtra}
        setExtra={setEditExtra}
        selectedTools={editSelectedTools}
        setSelectedTools={setEditSelectedTools}
        toolSearchTerm={editToolSearchTerm}
        setToolSearchTerm={setEditToolSearchTerm}
        filteredTools={filteredEditTools}
        modelProviderApiKeysQuery={modelProviderApiKeysQuery}
        toolsQuery={toolsQuery}
        mcpToolServersQuery={mcpToolServersQuery}
        selectedMcpToolServerIds={editSelectedMcpToolServerIds}
        mcpToolServerSearchTerm={editMcpToolServerSearchTerm}
        setMcpToolServerSearchTerm={setEditMcpToolServerSearchTerm}
        filteredMcpToolServers={filteredEditMcpToolServers}
        updateAgentMutation={updateAgentMutation}
        toggleTool={(toolName) => toggleTool(toolName, true)}
        toggleMcpToolServer={(mcpToolServerId) =>
          toggleMcpToolServer(mcpToolServerId, true)
        }
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
