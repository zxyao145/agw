"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";

import { apiGet } from "@/api/client";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ProjectTaskDto } from "./types";

function formatDate(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return date.toLocaleString();
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

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
  const messages = task?.messages ?? [];

  return (
    <div className="space-y-6 w-full min-w-0 max-w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {taskQuery.isLoading ? "Loading task..." : (task?.title ?? "Task")}
            </h1>
            {task ? (
              <span className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(task.status)}`}>
                {statusLabel(task.status)}
              </span>
            ) : null}
          </div>
          <div className="text-sm text-muted-foreground">
            {task?.jobId ? `Source job: ${task.jobId}` : "Source: chat"}
          </div>
          {task ? (
            <div className="text-xs text-muted-foreground">
              <span className="font-mono">{task.id}</span>
            </div>
          ) : null}
        </div>

        <ButtonGroup>
          <Button asChild size="sm">
            <Link href={`/chat?projectId=${projectId}&taskId=${taskId}`}>Continue In Chat</Link>
          </Button>
          <Button asChild variant="outline" size="sm">
            <Link href={`/projects/${projectId}`}>Back</Link>
          </Button>
        </ButtonGroup>
      </div>

      {taskQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading task details...</div>
      ) : taskQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load task: {getApiErrorMessage(taskQuery.error)}
        </div>
      ) : task ? (
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>Task Overview</CardTitle>
              <CardDescription>Execution metadata captured for this task session.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4 text-sm sm:grid-cols-2">
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Context ID
                </div>
                <div className="break-all font-mono text-xs">{task.contextId}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Job ID</div>
                <div className="break-all font-mono text-xs">{task.jobId ?? "-"}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Source</div>
                <div>{task.jobId ? "Job run" : "Chat session"}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Message Count
                </div>
                <div>{task.messageCount}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Created</div>
                <div>{formatDate(task.createTime)}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Started</div>
                <div>{formatDate(task.startedTime)}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Updated</div>
                <div>{formatDate(task.updateTime)}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Finished
                </div>
                <div>{formatDate(task.finishedTime)}</div>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Input</CardTitle>
              <CardDescription>The initial user input stored for this task.</CardDescription>
            </CardHeader>
            <CardContent>
              <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/40 p-3 text-sm">
                {task.input || "-"}
              </pre>
            </CardContent>
          </Card>

          {task.errorMessage ? (
            <Card className="border-destructive/50">
              <CardHeader>
                <CardTitle className="text-destructive">Error</CardTitle>
                <CardDescription>Terminal error recorded for this task.</CardDescription>
              </CardHeader>
              <CardContent>
                <div className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {task.errorMessage}
                </div>
              </CardContent>
            </Card>
          ) : null}

          <Card>
            <CardHeader>
              <CardTitle>Message History</CardTitle>
              <CardDescription>Conversation records attached to this task session.</CardDescription>
            </CardHeader>
            <CardContent>
              {messages.length === 0 ? (
                <div className="text-sm text-muted-foreground">No messages recorded.</div>
              ) : (
                <div className="space-y-3">
                  {messages.map((message) => (
                    <div key={message.messageId} className="rounded-lg border p-4">
                      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                        <span className="font-medium text-foreground">
                          {message.author || message.role || "Unknown"}
                        </span>
                        {message.role ? <span>{message.role}</span> : null}
                        {message.type ? <span>{message.type}</span> : null}
                      </div>

                      <div className="mt-3 space-y-2">
                        {message.contents.length === 0 ? (
                          <div className="text-sm text-muted-foreground">No content recorded.</div>
                        ) : (
                          message.contents.map((content, index) => (
                            <div key={`${message.messageId}:${index}`} className="space-y-1">
                              <div className="text-[11px] uppercase tracking-wide text-muted-foreground">
                                {content.type}
                              </div>
                              <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/40 p-3 text-sm">
                                {content.content || "-"}
                              </pre>
                            </div>
                          ))
                        )}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      ) : null}
    </div>
  );
}
