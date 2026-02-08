"use client";

import * as React from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { toast } from "sonner";
import { Ulid } from "id128";

import { apiGet } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Textarea } from "@/components/ui/textarea";
import { ButtonGroup } from "@/components/ui/button-group";
import type { AiMessage, ProcessedMessageItem } from "@/types/message";
import { ChatSession } from "@/components/message/chat-session";
import { ProjectTaskDto } from "./types";
import { Conversation } from "./components/conversation";
import { UserInput } from "@/components/message/user-input";

function formatDate(value?: string | null): string {
  if (!value) return "-";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleString();
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof Error) return error.message;
  return "Unknown error";
}

function statusLabel(status: number): string {
  switch (status) {
    case 0:
      return "Pending";
    case 1:
      return "Running";
    case 2:
      return "Succeeded";
    case 3:
      return "Failed";
    case 4:
      return "Canceled";
    default:
      return `#${status}`;
  }
}

function statusClassName(status: number): string {
  switch (status) {
    case 2:
      return "bg-emerald-500/10 text-emerald-700 dark:text-emerald-300";
    case 1:
      return "bg-blue-500/10 text-blue-700 dark:text-blue-300";
    case 0:
      return "bg-muted text-muted-foreground";
    case 4:
      return "bg-muted text-muted-foreground";
    case 3:
      return "bg-destructive/10 text-destructive";
    default:
      return "bg-muted text-muted-foreground";
  }
}

type ChatMessage = {
  AuthorName: string;
  Role: string;
  Content: string;
};

function StreamingChatSession({ messages }: { messages: AiMessage[] }) {
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);

  const processMessages = React.useCallback(
    (msgs: AiMessage[]): ProcessedMessageItem[] => {
      return msgs.map((msg) => ({ type: "normal", message: msg }));
    },
    []
  );

  if (messages.length === 0) {
    return null;
  }

  return (
    <div className="border-t pt-4">
      <div className="text-sm font-medium text-muted-foreground mb-2">
        Live Conversation
      </div>
      <ChatSession
        messages={messages}
        messagesEndRef={messagesEndRef}
        processMessages={processMessages}
      />
    </div>
  );
}

function OutputChatSession({ outputJson }: { outputJson: string }) {
  const messagesEndRef = React.useRef<HTMLDivElement>(null!);

  const messages = React.useMemo<AiMessage[]>(() => {
    try {
      const parsed = JSON.parse(outputJson);

      if (parsed.Outputs && Array.isArray(parsed.Outputs)) {
        return parsed.Outputs.filter(
          (msg: ChatMessage) => msg.Role && msg.Content,
        ).map((msg: ChatMessage, index: number) => ({
          messageId: `msg-${index}`,
          author: msg.AuthorName,
          role: msg.Role,
          contents: [
            {
              type: "TextContent",
              content: msg.Content,
            },
          ],
        }));
      }

      return [];
    } catch {
      return [];
    }
  }, [outputJson]);

  const processMessages = React.useCallback(
    (msgs: AiMessage[]): ProcessedMessageItem[] => {
      return msgs.map((msg) => ({ type: "normal", message: msg }));
    },
    [],
  );

  // If no valid messages, show raw JSON
  if (messages.length === 0) {
    return (
      <Textarea
        value={outputJson}
        readOnly
        rows={12}
        className="font-mono text-xs"
      />
    );
  }

  return (
    <ChatSession
      messages={messages}
      messagesEndRef={messagesEndRef}
      processMessages={processMessages}
    />
  );
}

export default function TaskDetailsPage() {
  const params = useParams<{ id: string; taskId: string }>();
  const projectId = params.id;
  const taskId = params.taskId;

  const [isExecuting, setIsExecuting] = React.useState<boolean>(false);
  const [streamingMessages, setStreamingMessages] = React.useState<AiMessage[]>([]);
  const [threadId, setThreadId] = React.useState<string>(() => Ulid.generate().toCanonical());

  const taskQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", taskId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks/{taskId}", {
        params: { path: { projectId, taskId } },
      } as never)) as unknown as ProjectTaskDto;
    },
  });

  const task = taskQuery.data;
  const targetType = task?.agentType === 1 ? "agent" : "agentflow";
  const targetId =
    targetType === "agent" ? (task?.agentId ?? null) : (task?.agentflowId ?? null);

  const handleOnExecute = React.useCallback(async (value: string) => {
    if (!targetId || !value.trim()) return;

    setIsExecuting(true);

    // Add user message to streaming messages
    const userMessage: AiMessage = {
      messageId: Ulid.generate().toCanonical(),
      author: "user",
      role: "user",
      contents: [{ type: "TextContent", content: value }],
    };
    setStreamingMessages((prev) => [...prev, userMessage]);

    try {
      const executeUrl =
        targetType === "agent"
          ? `/api/agents/${targetId}/execute-sse`
          : `/api/agentflows/${targetId}/execute-sse`;

      const response = await fetch(executeUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          threadId,
          input: value,
        }),
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
        const { done, value: chunk } = await reader.read();
        if (done) break;

        buffer += decoder.decode(chunk, { stream: true });
        const lines = buffer.split("\n\n");

        // Keep the last incomplete line in buffer
        buffer = lines.pop() || "";

        for (const line of lines) {
          if (line.startsWith("data: ")) {
            const json = line.substring(6);
            try {
              const message: AiMessage = JSON.parse(json);
              // Skip user messages from the stream (we already added it)
              if (message.role === "user") continue;

              setStreamingMessages((prev) => {
                const existingIndex = prev.findIndex(
                  (m) => m.messageId === message.messageId
                );

                if (existingIndex >= 0) {
                  // Merge content for same messageId
                  const updated = [...prev];
                  const existingMsg = updated[existingIndex];
                  const existingTextContent = existingMsg.contents.find(
                    (c) => c.type === "TextContent" || c.type === "text"
                  );
                  const newTextContent = message.contents.find(
                    (c) => c.type === "TextContent" || c.type === "text"
                  );

                  if (existingTextContent && newTextContent) {
                    existingTextContent.content =
                      (existingTextContent.content || "") +
                      (newTextContent.content || "");
                  }

                  return updated;
                } else {
                  // New message
                  return [...prev, message];
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
    } finally {
      setIsExecuting(false);
    }
  }, [targetId, targetType, threadId]);

  return (
    <div className="space-y-3 w-full flex flex-col">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {taskQuery.isLoading
                ? "Loading task..."
                : (task?.description ?? "Task")}
            </h1>
            {task ? (
              <>
                <span
                  className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(
                    task.status,
                  )}`}
                >
                  {task?.id}
                </span>
                <span
                  className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(
                    task.status,
                  )}`}
                >
                  {statusLabel(task.status)}
                </span>
              </>
            ) : null}
          </div>
          <div className="text-sm text-muted-foreground">
            {task?.description ?? ""}
          </div>
        </div>

        <div className="flex flex-wrap gap-2 sm:justify-end">
          <ButtonGroup>
            <Button asChild variant="outline" size="sm">
              <Link href={`/projects/${projectId}`}>Back</Link>
            </Button>
            {task && targetId && (
              <Button asChild variant="outline" size="sm">
                <Link
                  href={
                    task.agentType === 1
                      ? `/agents/${targetId}`
                      : `/agentflows/${targetId}`
                  }
                >
                  {task.agentType === 1 ? "Agent" : "Agentflow"}
                </Link>
              </Button>
            )}
          </ButtonGroup>
        </div>
      </div>

      {taskQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">
          Loading task details...
        </div>
      ) : taskQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load task: {getApiErrorMessage(taskQuery.error)}
        </div>
      ) : task ? (
        <div className="space-y-6 flex-1 relative">
          {/* <Conversation task={task} /> */}

          {task.outputJson ? (
            <OutputChatSession outputJson={task.outputJson} />
          ) : null}

          {streamingMessages.length > 0 ? (
            <StreamingChatSession messages={streamingMessages} />
          ) : null}

          {task.errorMessage ? (
            <Card className="border-destructive/50">
              <CardHeader>
                <CardTitle className="text-destructive">Error</CardTitle>
                <CardDescription>Task execution error details.</CardDescription>
              </CardHeader>
              <CardContent>
                <div className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {task.errorMessage}
                </div>
              </CardContent>
            </Card>
          ) : null}

          <div className="absolute bottom-0 z-10 left-0 right-4 h-30 bg-linear-to-t from-bg-000 from-50% via-bg-000/80 via-70% to-transparent pointer-events-none">
            <UserInput
              isExecuting={isExecuting}
              onExecute={handleOnExecute}
            ></UserInput>
          </div>
        </div>
      ) : null}
    </div>
  );
}
