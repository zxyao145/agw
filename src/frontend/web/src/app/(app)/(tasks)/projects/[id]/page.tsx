"use client";

import * as React from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import type { ProjectTaskSummaryResponse } from "@/api/task-client";
import { ApiError, apiGet, apiPost } from "@/api/client";
import { buildChatTargetOptions } from "@/lib/chat-target-options";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { CreateTaskDialog } from "./create-task-dialog";
import {
  CREATE_TASK_BUTTON_LABEL,
  DETAILS_BUTTON_LABEL,
  PROJECT_DETAILS_DIALOG_TITLE,
  buildCreateTaskJobRequest,
  getProjectDetailItems,
  type ProjectDetails,
} from "./project-details";

type AgentDto = {
  id: string;
  displayName: string;
  name: string;
};

type AgentflowDto = {
  id: string;
  name: string;
  enable?: boolean;
};

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
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length > 0) {
      return error.body;
    }

    if (error.body && typeof error.body === "object") {
      const candidateBody = error.body as {
        message?: unknown;
        error?: unknown;
        detail?: unknown;
      };

      if (typeof candidateBody.message === "string" && candidateBody.message.trim().length > 0) {
        return candidateBody.message;
      }

      if (typeof candidateBody.error === "string" && candidateBody.error.trim().length > 0) {
        return candidateBody.error;
      }

      if (typeof candidateBody.detail === "string" && candidateBody.detail.trim().length > 0) {
        return candidateBody.detail;
      }

      try {
        const serializedBody = JSON.stringify(error.body);
        if (serializedBody && serializedBody !== "{}") {
          return serializedBody;
        }
      } catch {
        // ignore JSON serialization errors and fall back to status text below
      }
    }

    return `${error.status} ${error.statusText}`;
  }

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

export default function ProjectDetailsPage() {
  const params = useParams<{ id: string }>();
  const projectId = params.id;
  const queryClient = useQueryClient();
  const [detailsOpen, setDetailsOpen] = React.useState(false);
  const [createTaskOpen, setCreateTaskOpen] = React.useState(false);

  const projectQuery = useQuery({
    queryKey: ["projects", projectId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{id}", {
        params: { path: { id: projectId } },
      } as never)) as unknown as ProjectDetails;
    },
  });

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => (await apiGet("/api/agents")) as unknown as AgentDto[],
  });

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as unknown as AgentflowDto[],
  });

  const tasksQuery = useQuery({
    queryKey: ["projects", projectId, "tasks"],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks", {
        params: { path: { projectId } },
      } as never)) as unknown as ProjectTaskSummaryResponse[];
    },
  });

  const project = projectQuery.data;
  const projectDetailItems = project ? getProjectDetailItems(project) : [];
  const targetOptions = buildChatTargetOptions({
    projectId,
    agents: agentsQuery.data ?? [],
    agentflows: agentflowsQuery.data ?? [],
  });
  const areTargetsReady = agentsQuery.isSuccess && agentflowsQuery.isSuccess;
  const targetsErrorMessage =
    agentsQuery.isError || agentflowsQuery.isError
      ? `Failed to load targets: ${getApiErrorMessage(agentsQuery.error ?? agentflowsQuery.error)}`
      : null;

  const createTaskMutation = useMutation({
    mutationFn: async (body: ReturnType<typeof buildCreateTaskJobRequest>) => {
      return await apiPost("/api/jobs" as never, { body } as never);
    },
    onSuccess: async () => {
      toast.success("Task created.");
      setCreateTaskOpen(false);
      await queryClient.invalidateQueries({ queryKey: ["jobs"] });
      await queryClient.invalidateQueries({ queryKey: ["projects", projectId, "tasks"] });
    },
    onError: (error) => {
      toast.error(`Create task failed: ${getApiErrorMessage(error)}`);
    },
  });

  const tasks = [...(tasksQuery.data ?? [])].sort((left, right) => {
    const leftTime = Date.parse(left.updateTime ?? left.createTime ?? "") || 0;
    const rightTime = Date.parse(right.updateTime ?? right.createTime ?? "") || 0;
    return rightTime - leftTime;
  });

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {projectQuery.isLoading ? "Loading project..." : (project?.name ?? "Project")}
            </h1>
            {project ? (
              <span
                className={`rounded-md px-2 py-0.5 text-xs ${
                  project.enable
                    ? "bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
                    : "bg-muted text-muted-foreground"
                }`}
              >
                {project.enable ? "Enabled" : "Disabled"}
              </span>
            ) : null}
          </div>
          <div className="text-sm text-muted-foreground">
            {project?.description?.trim() || "Read-only task history for this project."}
          </div>
          {project ? (
            <div className="text-xs text-muted-foreground">
              <span className="font-mono">{project.id}</span>
              <span className="mx-2">·</span>
              Updated: {formatDate(project.updateTime ?? project.createTime)}
            </div>
          ) : null}
        </div>

        <ButtonGroup>
          <Button asChild variant="outline" size="sm">
            <Link href="/projects">Back</Link>
          </Button>
          <Button variant="outline" size="sm" onClick={() => setDetailsOpen(true)}>
            {DETAILS_BUTTON_LABEL}
          </Button>
          <Button
            size="sm"
            onClick={() => setCreateTaskOpen(true)}
            disabled={!project || !project.enable}
          >
            {CREATE_TASK_BUTTON_LABEL}
          </Button>
        </ButtonGroup>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Tasks</CardTitle>
          <CardDescription>Read-only history for chat sessions and job runs.</CardDescription>
        </CardHeader>
        <CardContent>
          {tasksQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading tasks...</div>
          ) : tasksQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load tasks: {getApiErrorMessage(tasksQuery.error)}
            </div>
          ) : tasks.length === 0 ? (
            <div className="text-sm text-muted-foreground">No task history found.</div>
          ) : (
            <div className="space-y-3">
              {tasks.map((task) => (
                <div key={task.id} className="rounded-lg border p-4">
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                    <div className="min-w-0 space-y-2">
                      <div className="flex flex-wrap items-center gap-2">
                        <div className="font-medium">{task.title}</div>
                        <span
                          className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(task.status)}`}
                        >
                          {statusLabel(task.status)}
                        </span>
                      </div>

                      <div className="grid gap-1 text-xs text-muted-foreground">
                        <div>
                          Source:{" "}
                          <span className="font-mono">
                            {task.jobId ? `job:${task.jobId}` : "chat"}
                          </span>
                        </div>
                        <div>
                          Task ID: <span className="font-mono">{task.id}</span>
                        </div>
                        <div>
                          Context ID: <span className="font-mono">{task.contextId}</span>
                        </div>
                        <div>
                          Job ID: <span className="font-mono">{task.jobId ?? "-"}</span>
                        </div>
                        <div>
                          Created: {formatDate(task.createTime)} · Started:{" "}
                          {formatDate(task.startedTime)} · Finished: {formatDate(task.finishedTime)}
                        </div>
                      </div>

                      {task.errorMessage ? (
                        <div className="text-xs text-destructive">Error: {task.errorMessage}</div>
                      ) : null}
                    </div>

                    <Button asChild variant="outline" size="sm">
                      <Link href={`/projects/${projectId}/tasks/${task.id}`}>View History</Link>
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      {projectQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load project: {getApiErrorMessage(projectQuery.error)}
        </div>
      ) : null}

      <Dialog open={detailsOpen} onOpenChange={setDetailsOpen}>
        <DialogContent size="lg">
          <DialogHeader>
            <DialogTitle>{PROJECT_DETAILS_DIALOG_TITLE}</DialogTitle>
            <DialogDescription>Current project metadata.</DialogDescription>
          </DialogHeader>

          {projectQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading project details...</div>
          ) : projectQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load project: {getApiErrorMessage(projectQuery.error)}
            </div>
          ) : (
            <div className="grid gap-4 text-sm sm:grid-cols-2">
              {projectDetailItems.map((item) => (
                <div key={item.label} className="space-y-1">
                  <div className="text-xs uppercase tracking-wide text-muted-foreground">
                    {item.label}
                  </div>
                  <div className={`break-all ${item.mono ? "font-mono text-xs" : ""}`}>
                    {item.value}
                  </div>
                </div>
              ))}
            </div>
          )}
        </DialogContent>
      </Dialog>

      <CreateTaskDialog
        open={createTaskOpen}
        onOpenChange={setCreateTaskOpen}
        project={project}
        targetOptions={targetOptions}
        targetsError={targetsErrorMessage}
        areTargetsReady={areTargetsReady}
        isSubmitting={createTaskMutation.isPending}
        onSubmit={({ jobName, prompt, targetValue }) => {
          try {
            createTaskMutation.mutate(
              buildCreateTaskJobRequest({
                projectId,
                targetValue,
                jobName,
                prompt,
              }),
            );
          } catch (error) {
            toast.error(getApiErrorMessage(error));
          }
        }}
      />
    </div>
  );
}
