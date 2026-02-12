"use client";

import * as React from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { Ulid } from "id128";

import { apiGet } from "@/api/client";
import { executeWithWebSocket } from "@/api/execution-ws";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { ButtonGroup } from "@/components/ui/button-group";
import type { AiMessage, ProcessedMessageItem } from "@/types/message";
import { ChatSession } from "@/components/message/chat-session";
import { ProjectTaskDto } from "./types";
import { UserInput } from "@/components/message/user-input";
import { getSessionByThreadId } from "../../../../claude-code/lib/chat-history-service";

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

function ChatMessageSession({
  title,
  messages,
}: {
  title: string;
  messages: AiMessage[];
}) {
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
        {title}
      </div>
      <ChatSession
        messages={messages}
        messagesEndRef={messagesEndRef}
        processMessages={processMessages}
      />
    </div>
  );
}

export default function TaskDetailsPage() {
  const params = useParams<{ id: string; taskId: string }>();
  const projectId = params.id;
  const taskId = params.taskId;

  const [isExecuting, setIsExecuting] = React.useState<boolean>(false);
  const [streamingMessages, setStreamingMessages] = React.useState<AiMessage[]>([]);
  const threadId = taskId;

  const taskQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", taskId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks/{taskId}", {
        params: { path: { projectId, taskId } },
      } as never)) as unknown as ProjectTaskDto;
    },
  });

  const task = taskQuery.data;
  const sessionQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", taskId, "session-record"],
    queryFn: async () => {
      const sessionByProject = await getSessionByThreadId(taskId, projectId);
      if (sessionByProject) {
        return sessionByProject;
      }
      return await getSessionByThreadId(taskId);
    },
    enabled: Boolean(taskId),
    refetchInterval: task?.status === 1 ? 2000 : false,
  });
  const sessionMessages = sessionQuery.data?.messages ?? [];
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
      await executeWithWebSocket(
        targetId,
        {
          agentType: targetType === "agent" ? 1 : 0,
          threadId,
          projectId,
          input: value,
        },
        (json) => {
          try {
            const message: AiMessage = JSON.parse(json);
            // Skip user messages from the stream (we already added it)
            if (message.role === "user") return;

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
      );
    } catch (error) {
      console.error("Execute failed:", error);
      toast.error(
        `Execute failed: ${error instanceof Error ? error.message : "Unknown error"}`
      );
    } finally {
      setIsExecuting(false);
      await sessionQuery.refetch();
    }
  }, [targetId, targetType, threadId, projectId, sessionQuery]);

  return (
    <div className="space-y-3 w-full min-w-0 max-w-full overflow-x-hidden flex flex-col">
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
        <div className="space-y-6 flex-1 min-w-0 relative">
          {/* <Conversation task={task} /> */}

          {sessionMessages.length > 0 ? (
            <ChatMessageSession
              title="Conversation"
              messages={sessionMessages}
            />
          ) : null}

          {streamingMessages.length > 0 ? (
            <ChatMessageSession
              title="Live Conversation"
              messages={streamingMessages}
            />
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
