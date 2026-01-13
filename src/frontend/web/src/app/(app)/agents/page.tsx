"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { ApiError, apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import type { components } from "@/api/openapi";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle as UiDialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";

import {Ulid} from 'id128';

import {
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerFooter,
  DrawerHeader,
  DrawerTitle,
  DrawerTrigger,
} from "@/components/ui/drawer";

import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

import {AiMessage, AiMessageContent} from "@/types"

import { Pencil, Trash2, X, Play } from "lucide-react";
import { ButtonGroup } from "@/components/ui/button-group";

type AgentCreateRequest = components["schemas"]["AgentCreateRequest"];

type AgentDto = {
  id: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderApiKeyId: string;
  tools?: string | null;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

type ToolInfo = {
  name: string;
  description: string;
  category: string;
  typeName: string;
  parameters: Array<{
    name: string;
    type: string;
    description?: string;
    isOptional: boolean;
  }>;
};

type ModelProviderApiKeyDto = {
  id: string;
  apiKeyName: string;
  modelProviderId: string;
  modelId: string;
  providerId: string;
  providerName: string;
  modelName: string;
};

type AgentExecuteRequest = {
  threadId: string | null;
  input: string;
};

type AgentExecuteResponse = {
  threadId: string;
  messages: AiMessage[];
};

function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    return `${error.status} ${error.statusText}`;
  }
  if (error instanceof Error) return error.message;
  return "Unknown error";
}

export default function AgentsPage() {
  const queryClient = useQueryClient();

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
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

  // State for tracking execution status
  const [isExecuting, setIsExecuting] = React.useState(false);

  // SSE-based execution function
  const executeAgentSSE = async (
    id: string,
    body: AgentExecuteRequest
  ): Promise<void> => {
    setIsExecuting(true);

    try {
      const response = await fetch(`/api/agents/${id}/execute-sse`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });

      if (!response.ok) {
        throw new Error(`Execute failed: ${response.status} ${response.statusText}`);
      }

      const reader = response.body?.getReader();
      if (!reader) {
        throw new Error('No response body');
      }

      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n\n');
        console.debug('Received lines:', lines);
        // Keep the last incomplete line in buffer
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const json = line.substring(6);
            try {
              // {MessageId: 'c2c49f1a-fd9f-4690-957e-bddfbd890337', Author: 'Hello ', Role: 'assistant', Content: ''}
              const message: AiMessage = JSON.parse(json);

              // Update executeResult with streaming messages
              setExecuteResult((prev) => {
                const messages = prev?.messages || [];
                const existingIndex = messages.findIndex(
                  (m) => m.messageId === message.messageId
                );

                if (existingIndex >= 0) {
                  // Merge content for same messageId
                  const updated = [...messages];
                  const existingMsg = updated[existingIndex];
                  const existingTextContent = existingMsg.contents.find(c => c.type === "text");
                  const newTextContent = message.contents.find(c => c.type === "text");

                  if (existingTextContent && newTextContent) {
                    existingTextContent.content = (existingTextContent.content || "") + (newTextContent.content || "");
                  }

                  updated[existingIndex] = existingMsg;
                  console.debug('Updated message:', prev?.threadId , updated[existingIndex]);
                  return { threadId: prev?.threadId || '', messages: updated };
                } else {
                  // New message
                  return {
                    threadId: prev?.threadId || '',
                    messages: [...messages, message],
                  };
                }
              });

              // Set threadId if not set (use the first message's thread context)
              if (!executeThreadId) {
                // We need to track threadId separately or extract from first message
                // For now, we'll keep it null and let backend manage it
              }
            } catch (e) {
              console.error('Parse error:', e);
            }
          }
        }
      }

      // toast.success("Execution completed");
    } catch (error) {
      toast.error(`Execute failed: ${error instanceof Error ? error.message : 'Unknown error'}`);
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
    // Parse tools from JSON string
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

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create agent</Button>
            </DialogTrigger>

            <DialogContent className="max-w-2xl max-h-[90vh] flex flex-col">
              <DialogHeader>
                <UiDialogTitle>Create agent</UiDialogTitle>
                <UiDialogDescription>
                  Uses <code>/api/tools</code> for available tools.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4 overflow-y-auto pr-2 -mr-2">
                <div className="grid gap-2">
                  <Label htmlFor="name">Name</Label>
                  <Input
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="demo-agent"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="description">Description</Label>
                  <Input
                    id="description"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    placeholder="Agent description..."
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="modelProviderApiKeyId">
                    Model Provider API Key
                  </Label>
                  <Select
                    value={modelProviderApiKeyId}
                    onValueChange={setModelProviderApiKeyId}
                  >
                    <SelectTrigger id="modelProviderApiKeyId" className="w-full">
                      <SelectValue placeholder="Select an API key..." />
                    </SelectTrigger>
                    <SelectContent position="popper" sideOffset={4}>
                      <SelectGroup>
                        <SelectLabel>Available API Keys</SelectLabel>
                        {modelProviderApiKeysQuery.isLoading ? (
                          <SelectItem value="loading" disabled>
                            Loading...
                          </SelectItem>
                        ) : modelProviderApiKeysQuery.data &&
                          modelProviderApiKeysQuery.data.length > 0 ? (
                          modelProviderApiKeysQuery.data.map((key) => (
                            <SelectItem key={key.id} value={key.id}>
                              {key.apiKeyName} (Model: {key.modelName},
                              Provider: {key.providerName})
                            </SelectItem>
                          ))
                        ) : (
                          <SelectItem value="no-keys" disabled>
                            No API keys available
                          </SelectItem>
                        )}
                      </SelectGroup>
                    </SelectContent>
                  </Select>
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="systemPrompt">System prompt</Label>
                  <Textarea
                    id="systemPrompt"
                    value={systemPrompt}
                    onChange={(e) => setSystemPrompt(e.target.value)}
                    rows={4}
                  />
                </div>

                <div className="grid gap-2">
                  <Label>Tools</Label>
                  <Input
                    placeholder="Search tools..."
                    value={toolSearchTerm}
                    onChange={(e) => setToolSearchTerm(e.target.value)}
                    className="mb-2"
                  />
                  <div className="border rounded-md p-3 max-h-48 overflow-y-auto space-y-2">
                    {toolsQuery.isLoading ? (
                      <div className="text-sm text-muted-foreground">
                        Loading tools...
                      </div>
                    ) : filteredTools.length === 0 ? (
                      <div className="text-sm text-muted-foreground">
                        No tools found
                      </div>
                    ) : (
                      filteredTools.map((tool) => (
                        <div
                          key={tool.name}
                          className="flex items-start space-x-2"
                        >
                          <Checkbox
                            id={`tool-${tool.name}`}
                            checked={selectedTools.includes(tool.name)}
                            onCheckedChange={() => toggleTool(tool.name, false)}
                          />
                          <div className="grid gap-1 leading-none">
                            <label
                              htmlFor={`tool-${tool.name}`}
                              className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70 cursor-pointer"
                            >
                              {tool.name}
                              <span className="ml-2 text-xs text-muted-foreground">
                                ({tool.category})
                              </span>
                            </label>
                            <p className="text-xs text-muted-foreground">
                              {tool.description}
                            </p>
                          </div>
                        </div>
                      ))
                    )}
                  </div>
                  <p className="text-xs text-muted-foreground mt-1">
                    {selectedTools.length} tool(s) selected
                  </p>
                </div>
              </div>

              <DialogFooter>
                <DialogClose asChild>
                  <Button type="button" variant="outline">
                    Cancel
                  </Button>
                </DialogClose>
                <Button
                  type="button"
                  onClick={() =>
                    createAgentMutation.mutate({
                      name,
                      description,
                      systemPrompt,
                      modelProviderApiKeyId,
                      tools:
                        selectedTools.length > 0
                          ? JSON.stringify(selectedTools)
                          : null,
                    })
                  }
                  disabled={
                    !name.trim() ||
                    !modelProviderApiKeyId.trim() ||
                    createAgentMutation.isPending
                  }
                >
                  {createAgentMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Agents</CardTitle>
          <CardDescription>
            Fetched from <code>/api/agents</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {agentsQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : agentsQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load agents: {getApiErrorMessage(agentsQuery.error)}
            </div>
          ) : agentsQuery.data && agentsQuery.data.length > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Description</TableHead>
                  <TableHead>System Prompt</TableHead>
                  <TableHead>Tools</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {agentsQuery.data.map((agent) => {
                  let toolNames: string[] = [];
                  try {
                    toolNames = agent.tools ? JSON.parse(agent.tools) : [];
                  } catch {
                    toolNames = [];
                  }

                  return (
                    <TableRow key={agent.id}>
                      <TableCell className="font-medium">
                        {agent.name}
                      </TableCell>
                      <TableCell className="max-w-xs truncate">
                        {agent.description || "-"}
                      </TableCell>
                      <TableCell className="max-w-xs truncate">
                        {agent.systemPrompt || "-"}
                      </TableCell>
                      <TableCell className="max-w-xs">
                        {toolNames.length > 0 ? (
                          <span className="text-xs">
                            {toolNames.slice(0, 2).join(", ")}
                            {toolNames.length > 2 &&
                              ` +${toolNames.length - 2} more`}
                          </span>
                        ) : (
                          <span className="text-muted-foreground">-</span>
                        )}
                      </TableCell>
                      {/* <TableCell className="font-mono text-xs">
                        {agent.modelProviderApiKeyId.substring(0, 8)}...
                      </TableCell> */}
                      <TableCell className="text-xs text-muted-foreground">
                        {agent.createTime
                          ? new Date(agent.createTime).toLocaleString()
                          : "-"}
                      </TableCell>
                      <TableCell className="text-right">
                        <div className="flex justify-end gap-2">
                          <ButtonGroup>
                            <Button
                              variant="ghost"
                              className="cursor-pointer"
                              size="sm"
                              onClick={() => handleExecute(agent)}
                            >
                              <Play className="w-4 h-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              className="cursor-pointer"
                              size="sm"
                              onClick={() => handleEdit(agent)}
                            >
                              <Pencil className="w-4 h-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => handleDelete(agent)}
                              className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                            >
                              <Trash2 className="w-4 h-4" />
                            </Button>
                          </ButtonGroup>
                        </div>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          ) : (
            <div className="text-sm text-muted-foreground">
              No agents found. Create one to get started.
            </div>
          )}
        </CardContent>
      </Card>

      {/* Edit Dialog */}
      <Dialog open={editOpen} onOpenChange={setEditOpen}>
        <DialogContent className="max-w-2xl max-h-[90vh] flex flex-col">
          <DialogHeader>
            <UiDialogTitle>Edit agent</UiDialogTitle>
            <UiDialogDescription>
              Update the agent configuration
            </UiDialogDescription>
          </DialogHeader>

          <div className="grid gap-4 overflow-y-auto pr-2 -mr-2">
            <div className="grid gap-2">
              <Label htmlFor="edit-name">Name</Label>
              <Input
                id="edit-name"
                value={editName}
                onChange={(e) => setEditName(e.target.value)}
                placeholder="demo-agent"
              />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="edit-description">Description</Label>
              <Input
                id="edit-description"
                value={editDescription}
                onChange={(e) => setEditDescription(e.target.value)}
                placeholder="Agent description..."
              />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="edit-modelProviderApiKeyId">
                Model Provider API Key
              </Label>
              <Select
                value={editModelProviderApiKeyId}
                onValueChange={setEditModelProviderApiKeyId}
              >
                <SelectTrigger id="edit-modelProviderApiKeyId" className="w-full">
                  <SelectValue placeholder="Select an API key..." />
                </SelectTrigger>
                <SelectContent position="popper" sideOffset={4}>
                  <SelectGroup>
                    <SelectLabel>Available API Keys</SelectLabel>
                    {modelProviderApiKeysQuery.isLoading ? (
                      <SelectItem value="loading" disabled>
                        Loading...
                      </SelectItem>
                    ) : modelProviderApiKeysQuery.data &&
                      modelProviderApiKeysQuery.data.length > 0 ? (
                      modelProviderApiKeysQuery.data.map((key) => (
                        <SelectItem key={key.id} value={key.id}>
                          {key.apiKeyName} (Model: {key.modelName}, Provider:{" "}
                          {key.providerName})
                        </SelectItem>
                      ))
                    ) : (
                      <SelectItem value="no-keys" disabled>
                        No API keys available
                      </SelectItem>
                    )}
                  </SelectGroup>
                </SelectContent>
              </Select>
            </div>

            <div className="grid gap-2">
              <Label htmlFor="edit-systemPrompt">System prompt</Label>
              <Textarea
                id="edit-systemPrompt"
                value={editSystemPrompt}
                onChange={(e) => setEditSystemPrompt(e.target.value)}
                rows={4}
              />
            </div>

            <div className="grid gap-2">
              <Label>Tools</Label>
              <Input
                placeholder="Search tools..."
                value={editToolSearchTerm}
                onChange={(e) => setEditToolSearchTerm(e.target.value)}
                className="mb-2"
              />
              <div className="border rounded-md p-3 max-h-48 overflow-y-auto space-y-2">
                {toolsQuery.isLoading ? (
                  <div className="text-sm text-muted-foreground">
                    Loading tools...
                  </div>
                ) : filteredEditTools.length === 0 ? (
                  <div className="text-sm text-muted-foreground">
                    No tools found
                  </div>
                ) : (
                  filteredEditTools.map((tool) => (
                    <div key={tool.name} className="flex items-start space-x-2">
                      <Checkbox
                        id={`edit-tool-${tool.name}`}
                        checked={editSelectedTools.includes(tool.name)}
                        onCheckedChange={() => toggleTool(tool.name, true)}
                      />
                      <div className="grid gap-1 leading-none">
                        <label
                          htmlFor={`edit-tool-${tool.name}`}
                          className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70 cursor-pointer"
                        >
                          {tool.name}
                          <span className="ml-2 text-xs text-muted-foreground">
                            ({tool.category})
                          </span>
                        </label>
                        <p className="text-xs text-muted-foreground">
                          {tool.description}
                        </p>
                      </div>
                    </div>
                  ))
                )}
              </div>
              <p className="text-xs text-muted-foreground mt-1">
                {editSelectedTools.length} tool(s) selected
              </p>
            </div>
          </div>

          <DialogFooter>
            <DialogClose asChild>
              <Button type="button" variant="outline">
                Cancel
              </Button>
            </DialogClose>
            <Button
              type="button"
              onClick={() => {
                if (editingAgent) {
                  updateAgentMutation.mutate({
                    id: editingAgent.id,
                    body: {
                      name: editName,
                      description: editDescription,
                      systemPrompt: editSystemPrompt,
                      modelProviderApiKeyId: editModelProviderApiKeyId,
                      tools:
                        editSelectedTools.length > 0
                          ? JSON.stringify(editSelectedTools)
                          : null,
                    },
                  });
                }
              }}
              disabled={
                !editName.trim() ||
                !editModelProviderApiKeyId.trim() ||
                updateAgentMutation.isPending
              }
            >
              {updateAgentMutation.isPending ? "Updating..." : "Update"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Delete Confirmation Dialog */}
      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent>
          <DialogHeader>
            <UiDialogTitle>Delete agent</UiDialogTitle>
            <UiDialogDescription>
              Are you sure you want to delete agent &quot;{deletingAgent?.name}
              &quot;? This action cannot be undone.
            </UiDialogDescription>
          </DialogHeader>

          <DialogFooter>
            <DialogClose asChild>
              <Button type="button" variant="outline">
                Cancel
              </Button>
            </DialogClose>
            <Button
              type="button"
              variant="destructive"
              onClick={() => {
                if (deletingAgent) {
                  deleteAgentMutation.mutate(deletingAgent.id);
                }
              }}
              disabled={deleteAgentMutation.isPending}
            >
              {deleteAgentMutation.isPending ? "Deleting..." : "Delete"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Execute Agent Sheet */}
      <Drawer
        direction="right"
        open={executeOpen}
        onOpenChange={setExecuteOpen}
        modal={true}
      >
        <DrawerContent
          className="data-[vaul-drawer-direction=right]:sm:max-w-xl"
          onPointerDownOutside={(e) => {
            e.preventDefault();
          }}
        >
          <DrawerHeader>
            <div className="flex item-center justify-between">
              <DrawerTitle>Agent: {executingAgent?.name}({executeThreadId})</DrawerTitle>
              <DrawerClose>
                <X size={20} className="cursor-pointer" />
              </DrawerClose>
            </div>
            <DrawerDescription>
              {/* 输入内容并发送给 agent 执行 */}
            </DrawerDescription>
          </DrawerHeader>

          <div className="grid gap-4 py-4">
            {/* Thread ID display */}
            {executeThreadId && (
              <div className="text-xs text-muted-foreground">
                Thread ID: {executeThreadId}
              </div>
            )}

            {/* Execution results */}
            {executeResult &&
              executeResult.messages.length > 0 &&
              (() => {
                // Merge messages with the same messageId
                const messageMap = new Map<string, AiMessage>();

                executeResult.messages.forEach((msg) => {
                  if (messageMap.has(msg.messageId)) {
                    // Merge content for same messageId
                    const existing = messageMap.get(msg.messageId)!;
                    const existingTextContent = existing.contents.find(c => c.type === "text");
                    const newTextContent = msg.contents.find(c => c.type === "text");
                    if (existingTextContent && newTextContent) {
                      existingTextContent.content = (existingTextContent.content || "") + (newTextContent.content || "");
                    }
                  } else {
                    // New message, create a copy
                    messageMap.set(msg.messageId, { ...msg });
                  }
                });

                const mergedMessages = Array.from(messageMap.values());

                return (
                  <div className="space-y-2">
                    <Label>Result</Label>
                    <div className="border rounded-md p-3 max-h-96 overflow-y-auto space-y-3 bg-muted/20">
                      {mergedMessages.map((msg) => (
                        <div
                          key={msg.messageId}
                          className={`p-3 rounded-md ${
                            msg.role === "user"
                              ? "bg-primary/10 ml-8"
                              : msg.role === "assistant"
                                ? "bg-secondary/50 mr-8"
                                : "bg-muted"
                          }`}
                        >
                          <div className="text-xs font-medium text-muted-foreground mb-1">
                            {msg.author}({msg.role ?? ""})
                          </div>
                          <div className="text-sm whitespace-pre-wrap">
                            {msg.contents.find(c => c.type === "text")?.content || ""}
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                );
              })()}

            {/* Input area */}
          </div>

          <DrawerFooter>
            <div className="flex  gap-2 items-end">
              <Textarea
                id="execute-input"
                className="flex-1"
                value={executeInput}
                onChange={(e) => setExecuteInput(e.target.value)}
                placeholder="请输入要发送给 agent 的内容..."
                rows={1}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !e.shiftKey) {
                    e.preventDefault();
                    handleSendExecute();
                  }
                }}
              />

              <div>
                <Button
                  onClick={handleSendExecute}
                  disabled={
                    !executeInput.trim() || isExecuting
                  }
                  className="w-full"
                >
                  {isExecuting ? "执行中..." : "发送"}
                </Button>

                {executeResult && (
                  <Button
                    variant="outline"
                    onClick={() => {
                      setExecuteInput("");
                      setExecuteResult(null);
                      setExecuteThreadId(null);
                    }}
                    className="w-full"
                  >
                    清空会话
                  </Button>
                )}
              </div>
            </div>
            <div className="grid gap-2">
              <p className="text-xs text-muted-foreground">
                按 Enter 发送，Shift+Enter 换行
              </p>
            </div>
          </DrawerFooter>
        </DrawerContent>
      </Drawer>
    </div>
  );
}
