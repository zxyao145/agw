"use client";

import * as React from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Check, ArrowDownFromLine } from "lucide-react";

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

type ProjectTaskDto = {
  id: string;
  projectId: string;
  workflowId: string;
  status: number;
  description: string;
  input: string;
  outputJson?: string | null;
  errorMessage?: string | null;
  createTime?: string | null;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
};

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

function ChatMessages({ outputJson }: { outputJson: string }) {
  const messages = React.useMemo(() => {
    try {
      const parsed = JSON.parse(outputJson);

      if (parsed.Outputs && Array.isArray(parsed.Outputs)) {
        return parsed.Outputs.filter(
          (msg: ChatMessage) => msg.Role && msg.Content
        );
      }

      // If no messages found, return empty array
      return [];
    } catch {
      return [];
    }
  }, [outputJson]);

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

  // Filter out user messages before mapping
  const assistantMessages = messages.filter(
    (msg: ChatMessage) => msg.Role.toLowerCase() !== "user"
  );

  return (
    <div className="space-y-6">
      {messages.map((message: ChatMessage, index: number) => {
        const showConnector = index !== assistantMessages.length - 1;
        const isAssistantMessage = message.Role.toLowerCase() !== "user";
        return (
          <div key={index} className="flex items-start gap-4">
            <div className="flex flex-col items-center flex-shrink-0">
              <div
                className="relative flex items-center justify-center rounded-full ring-8 ring-background shadow-sm h-8 w-8"
                style={{
                  backgroundColor: "hsl(142, 76%, 36%)",
                }}
              >
                {isAssistantMessage ? (
                  <Check size={16} color="#ffffff" />
                ) : (
                  <ArrowDownFromLine size={16} color="#ffffff" />
                )}
              </div>
              {showConnector && (
                <div className="w-0.5 flex-1 bg-border mt-2 min-h-16" />
              )}
            </div>

            {/* Content */}
            <div className="flex-1 pt-1">
              <div className="mb-2">
                <h3 className="font-semibold text-sm">{message.AuthorName}</h3>
                <p className="text-xs text-muted-foreground">{message.Role}</p>
              </div>
              <div className="rounded-lg rounded-tl-none px-4 py-3 bg-muted prose prose-sm dark:prose-invert max-w-none [&>*:first-child]:mt-0 [&>*:last-child]:mb-0">
                <ReactMarkdown remarkPlugins={[remarkGfm]}>
                  {message.Content}
                </ReactMarkdown>
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}

export default function TaskDetailsPage() {
  const params = useParams<{ id: string; taskId: string }>();
  const projectId = params.id;
  const taskId = params.taskId;

  const taskQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", taskId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks/{taskId}", {
        params: { path: { projectId, taskId } },
      } as never)) as unknown as ProjectTaskDto;
    },
  });

  const task = taskQuery.data;

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {taskQuery.isLoading
                ? "Loading task..."
                : task?.description ?? "Task"}
            </h1>
            {task ? (
              <>
                <span
                  className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(
                    task.status
                  )}`}
                >
                  {task?.id}
                </span>
                <span
                  className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(
                    task.status
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
            {task && (
              <Button asChild variant="outline" size="sm">
                <Link href={`/workflows/${task?.workflowId}`}>Workflow</Link>
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
        <div className="space-y-6">
          <div className="flex items-center gap-2 text-xs text-muted-foreground">
            <span>Conversation</span>
            {task.createTime && (
              <>
                <span>•</span>
                <span>Created: {formatDate(task.createTime)}</span>
              </>
            )}
            {task.startedTime && (
              <>
                <span>•</span>
                <span>Started: {formatDate(task.startedTime)}</span>
              </>
            )}
            {task.finishedTime && (
              <>
                <span>•</span>
                <span>Completed: {formatDate(task.finishedTime)}</span>
              </>
            )}
          </div>

          <Card>
            <CardHeader className="border-b [.border-b]:pb-4">
              <CardTitle>Input</CardTitle>
            </CardHeader>
            <CardContent>
              <ReactMarkdown remarkPlugins={[remarkGfm]}>
                {task.input}
              </ReactMarkdown>
            </CardContent>
          </Card>

          {task.outputJson ? (
            <Card>
              <CardHeader className="border-b [.border-b]:pb-4">
                <CardTitle>Output</CardTitle>
                <CardDescription>
                  Task execution conversation history.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <ChatMessages outputJson={task.outputJson} />
              </CardContent>
            </Card>
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
        </div>
      ) : null}
    </div>
  );
}
