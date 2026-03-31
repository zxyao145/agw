"use client";

import * as React from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";

import { apiGet } from "@/api/client";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ButtonGroup } from "@/components/ui/button-group";
import { ProjectTaskDto } from "./types";
import { Conversation } from "@/components/message/conversation";

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
  const targetType = task?.agentType === 0 ? "agent" : "agentflow";
  const targetId = targetType === "agent" ? (task?.agentId ?? null) : (task?.agentflowId ?? null);
  const sessionId = task?.id ?? taskId;

  return (
    <div className="space-y-3 w-full min-w-0 max-w-full overflow-x-hidden flex flex-col">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {taskQuery.isLoading ? "Loading task..." : (task?.description ?? "Task")}
            </h1>
            {task ? (
              <>
                <span className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(task.status)}`}>
                  {task?.id}
                </span>
                <span className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(task.status)}`}>
                  {statusLabel(task.status)}
                </span>
              </>
            ) : null}
          </div>
          <div className="text-sm text-muted-foreground">{task?.description ?? ""}</div>
        </div>

        <div className="flex flex-wrap gap-2 sm:justify-end">
          <ButtonGroup>
            <Button asChild variant="outline" size="sm">
              <Link href={`/projects/${projectId}`}>Back</Link>
            </Button>
            {task && targetId && (
              <Button asChild variant="outline" size="sm">
                <Link
                  href={task.agentType === 0 ? `/agents/${targetId}` : `/agentflows/${targetId}`}
                >
                  {task.agentType === 0 ? "Agent" : "Agentflow"}
                </Link>
              </Button>
            )}
          </ButtonGroup>
        </div>
      </div>

      {taskQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading task details...</div>
      ) : taskQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load task: {getApiErrorMessage(taskQuery.error)}
        </div>
      ) : task ? (
        <div className="space-y-6 flex-1 min-w-0">
          {/* <Conversation task={task} /> */}

          <Conversation
            className="h-[calc(100vh-100px)]"
            executionId={targetId}
            agentType={targetType === "agent" ? 0 : 1}
            projectId={projectId}
            taskId={task?.id ?? taskId}
            sessionId={sessionId}
            resetSignal={`${taskId}:${targetId ?? "none"}`}
            placeholder="请输入要发送给任务执行器的内容..."
          />

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
