"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Ulid } from "id128";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { Button } from "@/components/ui/button";
import type { AiMessageContent } from "@/types";

import type {
  AgentDto,
  AgentCreateRequest,
  ToolInfo,
  ModelProviderApiKeyDto,
  AgentExecuteRequest,
  AgentExecuteResponse,
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

  // Create dialog state
  const [createOpen, setCreateOpen] = React.useState(false);
  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [systemPrompt, setSystemPrompt] = React.useState("");
  const [modelProviderApiKeyId, setModelProviderApiKeyId] = React.useState("");
  const [selectedTools, setSelectedTools] = React.useState<string[]>([]);
  const [toolSearchTerm, setToolSearchTerm] = React.useState("");
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
  const [executeInput, setExecuteInput] = React.useState("");
  const [executeThreadId, setExecuteThreadId] = React.useState<string | null>(
    Ulid.generate().toCanonical()
  );
  const [executeResult, setExecuteResult] =
    React.useState<AgentExecuteResponse | null>(null);
  const [isExecuting, setIsExecuting] = React.useState(false);

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
      body: AgentCreateRequest;
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

  // SSE-based execution function
  const executeAgentSSE = async (
    id: string,
    body: AgentExecuteRequest
  ): Promise<void> => {
    setIsExecuting(true);

    try {
      const response = await fetch(`/api/agents/${id}/execute-sse`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        throw new Error(
          `Execute failed: ${response.status} ${response.statusText}`
        );
      }

      const reader = response.body?.getReader();
      if (!reader) {
        throw new Error("No response body");
      }

      const decoder = new TextDecoder();
      let buffer = "";

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n\n");
        console.debug("Received lines:", lines);
        buffer = lines.pop() || "";

        for (const line of lines) {
          if (line.startsWith("data: ")) {
            const json = line.substring(6);
            try {
              const message = JSON.parse(json);

              setExecuteResult((prev) => {
                const messages = prev?.messages || [];
                const existingIndex = messages.findIndex(
                  (m) => m.messageId === message.messageId
                );

                if (existingIndex >= 0) {
                  const updated = [...messages];
                  const existingMsg = updated[existingIndex];
                  const existingTextContent = existingMsg.contents.find(
                    (c: AiMessageContent) => c.type === "text"
                  );
                  const newTextContent = message.contents.find(
                    (c: AiMessageContent) => c.type === "text"
                  );

                  if (existingTextContent && newTextContent) {
                    existingTextContent.content =
                      (existingTextContent.content || "") +
                      (newTextContent.content || "");
                  }

                  updated[existingIndex] = existingMsg;
                  console.debug(
                    "Updated message:",
                    prev?.threadId,
                    updated[existingIndex]
                  );
                  return { threadId: prev?.threadId || "", messages: updated };
                } else {
                  return {
                    threadId: prev?.threadId || "",
                    messages: [...messages, message],
                  };
                }
              });
            } catch (e) {
              console.error("Parse error:", e);
            }
          }
        }
      }
    } catch (error) {
      toast.error(
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`
      );
      throw error;
    } finally {
      setIsExecuting(false);
    }
  };

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
    setEditToolSearchTerm("");
    setEditOpen(true);
  };

  const handleDelete = (agent: AgentDto) => {
    setDeletingAgent(agent);
    setDeleteOpen(true);
  };

  const handleExecute = (agent: AgentDto) => {
    setExecutingAgent(agent);
    setExecuteInput("");
    setExecuteResult(null);
    setExecuteOpen(true);
    setExecuteThreadId(Ulid.generate().toCanonical());
  };

  const handleSendExecute = async () => {
    if (!executingAgent || !executeInput.trim()) return;

    await executeAgentSSE(executingAgent.id, {
      threadId: executeThreadId,
      input: executeInput,
    });

    setExecuteInput("");
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
            createAgentMutation={createAgentMutation}
            toggleTool={(toolName) => toggleTool(toolName, false)}
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
        updateAgentMutation={updateAgentMutation}
        toggleTool={(toolName) => toggleTool(toolName, true)}
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
        executeInput={executeInput}
        setExecuteInput={setExecuteInput}
        executeThreadId={executeThreadId}
        setExecuteThreadId={setExecuteThreadId}
        executeResult={executeResult}
        setExecuteResult={setExecuteResult}
        isExecuting={isExecuting}
        handleSendExecute={handleSendExecute}
      />
    </div>
  );
}
