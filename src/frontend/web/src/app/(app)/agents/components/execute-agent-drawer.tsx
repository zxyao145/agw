import * as React from "react";
import { Button } from "@/components/ui/button";
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
import { X } from "lucide-react";
import type { AgentDto, AgentExecuteResponse } from "./types";
import type { AiMessage } from "@/types";

interface ExecuteAgentDrawerProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  executingAgent: AgentDto | null;
  executeInput: string;
  setExecuteInput: (value: string) => void;
  executeThreadId: string | null;
  setExecuteThreadId: (value: string | null) => void;
  executeResult: AgentExecuteResponse | null;
  setExecuteResult: React.Dispatch<React.SetStateAction<AgentExecuteResponse | null>>;
  isExecuting: boolean;
  handleSendExecute: () => void;
}

export function ExecuteAgentDrawer({
  open,
  setOpen,
  executingAgent,
  executeInput,
  setExecuteInput,
  executeThreadId,
  setExecuteThreadId,
  executeResult,
  setExecuteResult,
  isExecuting,
  handleSendExecute,
}: ExecuteAgentDrawerProps) {
  // Merge messages with the same messageId
  const mergedMessages = React.useMemo(() => {
    if (!executeResult || executeResult.messages.length === 0) return [];

    const messageMap = new Map<string, AiMessage>();

    executeResult.messages.forEach((msg) => {
      if (messageMap.has(msg.messageId)) {
        // Merge content for same messageId
        const existing = messageMap.get(msg.messageId)!;
        const existingTextContent = existing.contents.find(
          (c) => c.type === "text"
        );
        const newTextContent = msg.contents.find((c) => c.type === "text");
        if (existingTextContent && newTextContent) {
          existingTextContent.content =
            (existingTextContent.content || "") +
            (newTextContent.content || "");
        }
      } else {
        // New message, create a copy
        messageMap.set(msg.messageId, { ...msg });
      }
    });

    return Array.from(messageMap.values());
  }, [executeResult]);

  return (
    <Drawer
      direction="right"
      open={open}
      onOpenChange={setOpen}
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
              Agent: {executingAgent?.name}({executeThreadId})
            </DrawerTitle>
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
          {mergedMessages.length > 0 && (
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
                      {msg.contents.find((c) => c.type === "text")?.content ||
                        ""}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Input area */}
        </div>

        <DrawerFooter>
          <div className="flex gap-2 items-end">
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
  );
}
