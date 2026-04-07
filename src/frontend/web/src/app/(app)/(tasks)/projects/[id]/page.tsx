"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";

import { ApiError, apiGet } from "@/api/client";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

type ProjectDto = {
  id: string;
  name: string;
  description: string | null;
  workspace?: string | null;
  extraSetting?: string | null;
  enable: boolean;
  createTime?: string | null;
  updateTime?: string | null;
};

type ProjectTaskSummaryDto = {
  id: string;
  projectId: string;
  contextId: string;
  jobId?: string | null;
  status: number;
  title: string;
  errorMessage?: string | null;
  createTime?: string | null;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
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

  const projectQuery = useQuery({
    queryKey: ["projects", projectId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{id}", {
        params: { path: { id: projectId } },
      } as never)) as unknown as ProjectDto;
    },
  });

  const tasksQuery = useQuery({
    queryKey: ["projects", projectId, "tasks"],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks", {
        params: { path: { projectId } },
      } as never)) as unknown as ProjectTaskSummaryDto[];
    },
  });

  const project = projectQuery.data;
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
        </ButtonGroup>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Project Details</CardTitle>
          <CardDescription>Project metadata only. Task execution controls were removed.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 text-sm sm:grid-cols-2">
          <div className="space-y-1">
            <div className="text-xs uppercase tracking-wide text-muted-foreground">Workspace</div>
            <div className="break-all font-mono text-xs">{project?.workspace?.trim() || "-"}</div>
          </div>
          <div className="space-y-1">
            <div className="text-xs uppercase tracking-wide text-muted-foreground">
              Extra Setting
            </div>
            <div className="break-all font-mono text-xs">
              {project?.extraSetting?.trim() || "-"}
            </div>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Task History</CardTitle>
          <CardDescription>
            Tasks are listed newest first by <code>updateTime</code> with <code>createTime</code>{" "}
            as fallback.
          </CardDescription>
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
    </div>
  );
}
