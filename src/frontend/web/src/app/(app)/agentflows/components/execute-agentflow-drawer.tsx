import * as React from "react";
import { toast } from "sonner";
import { Ulid } from "id128";
import {
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerFooter,
  DrawerHeader,
  DrawerTitle,
} from "@/components/ui/drawer";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { X } from "lucide-react";
import type { AgentflowDto } from "@/types/agentflow";
import type { AiMessage } from "@/types";
import type { AgentflowExecuteRequest, AgentflowExecuteResponse } from "./types";
import { getTextContent, mergeTextContent, mergeMessages } from "./utils";

interface ExecuteAgentflowDrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  agentflow: AgentflowDto | null;
}

export function ExecuteAgentflowDrawer({
  open,
  onOpenChange,
  agentflow,
}: ExecuteAgentflowDrawerProps) {
  const [executeInput, setExecuteInput] = React.useState("");
  const [executeThreadId, setExecuteThreadId] = React.useState<string | null>(
    Ulid.generate().toCanonical()
  );
  const [executeResult, setExecuteResult] =
    React.useState<AgentflowExecuteResponse | null>(null);
  const [isExecuting, setIsExecuting] = React.useState(false);

  // Reset state when drawer opens/closes or agentflow changes
  React.useEffect(() => {
    if (open && agentflow) {
      setExecuteInput("");
      setExecuteResult(null);
      setExecuteThreadId(Ulid.generate().toCanonical());
    }
  }, [open, agentflow]);

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
                  mergeTextContent(updated[existingIndex], message);
                  console.debug('Updated message:', prev?.threadId, updated[existingIndex]);
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
    } catch (error) {
      toast.error(
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`
      );
      throw error;
    } finally {
      setIsExecuting(false);
    }
  };

  const handleSendExecute = React.useCallback(async () => {
    if (!agentflow || !executeInput.trim()) return;

    setExecuteResult((prev) => {
      const userMsg: AiMessage = {
        messageId: Ulid.generate().toCanonical(),
        author: "user",
        role: "user",
        contents: [{ type: "text", content: executeInput }],
      };
      if (prev) {
        return {
          threadId: prev.threadId,
          messages: [...prev.messages, userMsg],
        };
      }
      return {
        threadId: executeThreadId || Ulid.generate().toCanonical(),
        messages: [userMsg],
      };
    });

    await executeAgentflowSSE(agentflow.id, {
      threadId: executeThreadId,
      input: executeInput,
    });

    setExecuteInput("");
  }, [agentflow, executeInput, executeThreadId]);

  const handleClearConversation = () => {
    setExecuteInput("");
    setExecuteResult(null);
    setExecuteThreadId(null);
  };

  if (!agentflow) return null;

  return (
    <Drawer
      direction="right"
      open={open}
      onOpenChange={onOpenChange}
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
              Agentflow: {agentflow.name} ({executeThreadId})
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
          {executeResult && executeResult.messages.length > 0 && (
            <div className="space-y-2">
              <Label>Result</Label>
              <div className="border rounded-md p-3 max-h-96 overflow-y-auto space-y-3 bg-muted/20">
                {mergeMessages(executeResult.messages).map((msg) => (
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
                      {getTextContent(msg)}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
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
                  onClick={handleClearConversation}
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
  );
}
