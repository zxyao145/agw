"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { ApiError, apiGet, apiPut, apiDelete } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
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
import {
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerFooter,
  DrawerHeader,
  DrawerTitle,
} from "@/components/ui/drawer";
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
import { VisualAgentflowDialog } from "../agentflows/components/visual-agentflow-dialog";
import { AgentDto, AgentflowDto } from "@/types/agentflow";
import { Pencil, Trash2, X, Play } from "lucide-react";
import { ButtonGroup } from "@/components/ui/button-group";
import { Ulid } from "id128";

import {AiMessage, AiMessageContent} from "@/types"


type AgentflowExecuteRequest = {
  threadId: string | null;
  input: string;
};

type AgentflowExecuteResponse = {
  threadId: string;
  messages: AiMessage[];
};

function getPatternName(pattern: number): string {
  switch (pattern) {
    case 0:
      return "Concurrent";
    case 1:
      return "Sequential";
    case 2:
      return "GroupChat";
    case 3:
      return "Handoff";
    case 4:
      return "Magentic";
    default:
      return `Unknown (${pattern})`;
  }
}

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

export default function AgentflowsPage() {
  const queryClient = useQueryClient();

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agentflows")) as unknown as AgentflowDto[];
    },
  });

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agents")) as unknown as AgentDto[];
    },
  });

  const [visualOpen, setVisualOpen] = React.useState(false);
  const [editingAgentflow, setEditingAgentflow] = React.useState<{
    id: string;
    name: string;
    description: string | null;
    pattern: number;
    configurationJson: string | null;
    enable: boolean;
    nodes: any[];
    edges: any[];
  } | null>(null);

  // Execute drawer state
  const [executeOpen, setExecuteOpen] = React.useState(false);
  const [executingAgentflow, setExecutingAgentflow] =
    React.useState<AgentflowDto | null>(null);
  const [executeInput, setExecuteInput] = React.useState("");
  const [executeThreadId, setExecuteThreadId] = React.useState<string | null>(
    Ulid.generate().toCanonical()
  );
  const [executeResult, setExecuteResult] =
    React.useState<AgentflowExecuteResponse | null>(null);

  const updateAgentflowMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: any }) => {
      // @ts-ignore - Dynamic path not supported by apiPut types
      return await apiPut(`/api/agentflows/${id}`, { body });
    },
    onSuccess: async () => {
      toast.success("Agentflow updated");
      await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteAgentflowMutation = useMutation({
    mutationFn: async (id: string) => {
      // @ts-ignore - Dynamic path not supported by apiDelete types
      return await apiDelete(`/api/agentflows/${id}`);
    },
    onSuccess: async () => {
      toast.success("Agentflow deleted");
      await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  // State for tracking execution status
  const [isExecuting, setIsExecuting] = React.useState(false);

  // SSE-based execution function
  const executeAgentflowSSE = async (
    id: string,
    body: AgentflowExecuteRequest
  ): Promise<void> => {
    setIsExecuting(true);

    try {
      const response = await fetch(`/api/agentflows/${id}/execute-sse`, {
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
        if (done) {
          break;
        }

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n\n");

        // Keep the last incomplete line in buffer
        buffer = lines.pop() || "";

        const newMsg: AiMessage = {
          messageId: "",
          author: executingAgentflow!.name,
          role: "",
          contents: [{ type: "text", content: "" }],
        };

        for (const line of lines) {
          if (line.startsWith("data: ")) {
            const json = line.substring(6);
            try {
              const message: AiMessage = JSON.parse(json);
              // Update executeResult with streaming messages
              setExecuteResult((prev) => {
                if (message.role === "user") {
                  return prev;
                }
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
            } catch (e) {
              console.error("Parse error:", e);
            }
          }
        }
      }

      // toast.success("Execution completed");
    } catch (error) {
      toast.error(
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`
      );
      throw error;
    } finally {
      setIsExecuting(false);
    }
  };

  const handleToggleEnabled = React.useCallback(
    async (agentflow: AgentflowDto) => {
      try {
        // We need to get full agentflow data including nodes and edges
        // @ts-ignore - Dynamic path not supported by apiGet types
        const nodes = (await apiGet(`/api/agentflows/${agentflow.id}/nodes`)) as any[];
        // @ts-ignore - Dynamic path not supported by apiGet types
        const edges = (await apiGet(`/api/agentflows/${agentflow.id}/edges`)) as any[];

        updateAgentflowMutation.mutate({
          id: agentflow.id,
          body: {
            name: agentflow.name,
            description: agentflow.description,
            pattern: agentflow.pattern,
            configurationJson: agentflow.configurationJson,
            enable: !agentflow.enable,
            nodes: nodes || [],
            edges: edges || [],
          },
        });
      } catch (error) {
        toast.error("Failed to fetch agentflow details");
      }
    },
    [updateAgentflowMutation]
  );

  const handleDelete = React.useCallback(
    (agentflow: AgentflowDto) => {
      if (
        window.confirm(`Are you sure you want to delete "${agentflow.name}"?`)
      ) {
        deleteAgentflowMutation.mutate(agentflow.id);
      }
    },
    [deleteAgentflowMutation]
  );

  const handleEdit = React.useCallback(async (agentflow: AgentflowDto) => {
    try {
      // Fetch agentflow nodes and edges
      // @ts-ignore - Dynamic path not supported by apiGet types
      const nodes = (await apiGet(`/api/agentflows/${agentflow.id}/nodes`)) as any[];
      // @ts-ignore - Dynamic path not supported by apiGet types
      const edges = (await apiGet(`/api/agentflows/${agentflow.id}/edges`)) as any[];

      // Set editing agentflow data
      setEditingAgentflow({
        id: agentflow.id,
        name: agentflow.name,
        description: agentflow.description,
        pattern: agentflow.pattern,
        configurationJson: agentflow.configurationJson,
        enable: agentflow.enable,
        nodes: nodes || [],
        edges: edges || [],
      });

      // Open visual builder
      setVisualOpen(true);
    } catch (error) {
      toast.error("Failed to load agentflow details");
      console.error("Failed to load agentflow:", error);
    }
  }, []);

  const handleAgentflowCreated = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    setVisualOpen(false);
    setEditingAgentflow(null);
  }, [queryClient]);

  const handleVisualDialogClose = React.useCallback(() => {
    setVisualOpen(false);
    setEditingAgentflow(null);
  }, []);

  const handleExecute = React.useCallback((agentflow: AgentflowDto) => {
    setExecutingAgentflow(agentflow);
    setExecuteInput("");
    setExecuteResult(null);
    setExecuteThreadId(null);
    setExecuteOpen(true);
  }, []);

  const handleSendExecute = React.useCallback(async () => {
    if (!executingAgentflow || !executeInput.trim()) return;

    setExecuteResult((prev) => {
      const userMg = {
        messageId: Ulid.generate().toCanonical(),
        author: "user",
        role: "user",
        content: executeInput,
      };
      if (prev) {
        return {
          threadId: prev.threadId,
          messages: [...prev.messages, userMg],
        };
      }
      return {
        threadId: executeThreadId || Ulid.generate().toCanonical(),
        messages: [userMg],
      };
    });

    await executeAgentflowSSE(executingAgentflow.id, {
      threadId: executeThreadId,
      input: executeInput,
    });

    setExecuteInput("");
  }, [executingAgentflow, executeInput, executeThreadId]);

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Agentflows</h1>
          <p className="text-sm text-muted-foreground">
            Manage agentflows and execute them.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={() => agentflowsQuery.refetch()}
            disabled={agentflowsQuery.isFetching}
          >
            Refresh
          </Button>

          <Button
            className="cursor-pointer"
            onClick={() => setVisualOpen(true)}
          >
            Create Agentflow
          </Button>

          <VisualAgentflowDialog
            open={visualOpen}
            onOpenChange={handleVisualDialogClose}
            agents={agentsQuery.data || []}
            agentflows={agentflowsQuery.data || []}
            editingAgentflow={editingAgentflow}
            onAgentflowCreated={handleAgentflowCreated}
          />
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Agentflows</CardTitle>
          <CardDescription>
            Fetched from <code>/api/agentflows</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {agentflowsQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : agentflowsQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load agentflows:{" "}
              {getApiErrorMessage(agentflowsQuery.error)}
            </div>
          ) : agentflowsQuery.data && agentflowsQuery.data.length > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Description</TableHead>
                  <TableHead>Pattern</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead className="text-center">Enabled</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {agentflowsQuery.data.map((agentflow) => (
                  <TableRow key={agentflow.id}>
                    <TableCell className="font-medium">
                      {agentflow.name}
                    </TableCell>
                    <TableCell className="max-w-xs truncate">
                      {agentflow.description || "-"}
                    </TableCell>
                    <TableCell>{getPatternName(agentflow.pattern)}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {agentflow.createTime
                        ? new Date(agentflow.createTime).toLocaleString()
                        : "-"}
                    </TableCell>
                    <TableCell className="text-center">
                      <label className="relative inline-flex items-center cursor-pointer">
                        <input
                          type="checkbox"
                          checked={agentflow.enable}
                          onChange={() => handleToggleEnabled(agentflow)}
                          disabled={updateAgentflowMutation.isPending}
                          className="sr-only peer"
                        />
                        <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full rtl:peer-checked:after:-translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:start-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-green-600"></div>
                      </label>
                    </TableCell>
                    <TableCell>
                      <div className="flex justify-end">
                        <ButtonGroup>
                          <Button
                            variant="ghost"
                            className="cursor-pointer"
                            size="icon-sm"
                            onClick={() => handleExecute(agentflow)}
                            title="Run agentflow"
                          >
                            <Play className="w-4 h-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            className="cursor-pointer"
                            size="icon-sm"
                            onClick={() => handleEdit(agentflow)}
                            title="Edit agentflow"
                          >
                            <Pencil className="w-4 h-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="icon-sm"
                            onClick={() => handleDelete(agentflow)}
                            disabled={deleteAgentflowMutation.isPending}
                            title="Delete agentflow"
                            className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                          >
                            <Trash2 className="w-4 h-4" />
                          </Button>
                        </ButtonGroup>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <div className="text-sm text-muted-foreground">
              No agentflows found. Create one to get started.
            </div>
          )}
        </CardContent>
      </Card>

      {/* Execute Agentflow Drawer */}
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
              <DrawerTitle>
                Agentflow: {executingAgentflow?.name} ({executeThreadId})
              </DrawerTitle>
              <DrawerClose>
                <X size={20} className="cursor-pointer" />
              </DrawerClose>
            </div>
            <DrawerDescription>
              {/* 输入内容并执行 agentflow */}
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
            <div className="flex gap-2 items-end">
              <Textarea
                id="execute-input"
                className="flex-1"
                value={executeInput}
                onChange={(e) => setExecuteInput(e.target.value)}
                placeholder="请输入要发送给 agentflow 的内容..."
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
                  disabled={!executeInput.trim() || isExecuting}
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
