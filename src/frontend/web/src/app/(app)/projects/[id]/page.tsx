"use client";

import * as React from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { ApiError, apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import type { components } from "@/api/openapi";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { ButtonGroup } from "@/components/ui/button-group";

type ProjectUpdateRequest = components["schemas"]["ProjectUpdateRequest"];
type ProjectTaskCreateRequest =
  components["schemas"]["ProjectTaskCreateRequest"];
type ProjectTaskUpdateRequest =
  components["schemas"]["ProjectTaskUpdateRequest"];
type ProjectTaskReorderRequest =
  components["schemas"]["ProjectTaskReorderRequest"];

type ProjectDto = {
  id: string;
  name: string;
  description: string | null;
  enable: boolean;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

type WorkflowDto = {
  id: string;
  name: string;
  description: string | null;
  enable: boolean;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

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
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    if (error.body && typeof error.body === "object") {
      try {
        return JSON.stringify(error.body);
      } catch {
        // ignore
      }
    }
    return `${error.status} ${error.statusText}`;
  }
  if (error instanceof Error) return error.message;
  return "Unknown error";
}

function statusLabel(status: number): string {
  // DSystem.Domain.Enums.ProjectTaskStatus
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

  const projectQuery = useQuery({
    queryKey: ["projects", projectId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{id}", {
        params: { path: { id: projectId } },
      })) as unknown as ProjectDto;
    },
  });

  const tasksQuery = useQuery({
    queryKey: ["projects", projectId, "tasks"],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks", {
        params: { path: { projectId } },
      })) as unknown as ProjectTaskDto[];
    },
  });

  const workflowsQuery = useQuery({
    queryKey: ["workflows"],
    queryFn: async () => {
      return (await apiGet("/api/workflows")) as unknown as WorkflowDto[];
    },
  });

  const [editOpen, setEditOpen] = React.useState(false);
  const [editName, setEditName] = React.useState("");
  const [editDescription, setEditDescription] = React.useState<string>("");
  const [editEnable, setEditEnable] = React.useState(true);

  const [createTaskOpen, setCreateTaskOpen] = React.useState(false);
  const [createTaskWorkflowId, setCreateTaskWorkflowId] = React.useState("");
  const [createTaskDescription, setCreateTaskDescription] = React.useState("");
  const [createTaskInput, setCreateTaskInput] = React.useState("");

  const [editTaskOpen, setEditTaskOpen] = React.useState(false);
  const [editTaskId, setEditTaskId] = React.useState<string | null>(null);
  const [editTaskDescription, setEditTaskDescription] = React.useState("");
  const [editTaskInput, setEditTaskInput] = React.useState("");

  React.useEffect(() => {
    const p = projectQuery.data;
    if (!p) return;
    setEditName(p.name ?? "");
    setEditDescription(p.description ?? "");
    setEditEnable(Boolean(p.enable));
  }, [projectQuery.data]);

  const updateProjectMutation = useMutation({
    mutationFn: async (body: ProjectUpdateRequest) => {
      return await apiPut("/api/projects/{id}", {
        params: { path: { id: projectId } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Project updated");
      setEditOpen(false);
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
      await queryClient.invalidateQueries({
        queryKey: ["projects", projectId],
      });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteProjectMutation = useMutation({
    mutationFn: async () => {
      return await apiDelete("/api/projects/{id}", {
        params: { path: { id: projectId } },
      });
    },
    onSuccess: async () => {
      toast.success("Project deleted");
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
      window.location.href = "/projects";
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const createTaskMutation = useMutation({
    mutationFn: async (body: ProjectTaskCreateRequest) => {
      return await apiPost("/api/projects/{projectId}/tasks", {
        params: { path: { projectId } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Task created");
      setCreateTaskOpen(false);
      setCreateTaskWorkflowId("");
      setCreateTaskDescription("");
      setCreateTaskInput("");
      await queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "tasks"],
      });
    },
    onError: (error) => {
      toast.error(`Create task failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateTaskMutation = useMutation({
    mutationFn: async (args: {
      taskId: string;
      body: ProjectTaskUpdateRequest;
    }) => {
      return await apiPut("/api/projects/{projectId}/tasks/{taskId}", {
        params: { path: { projectId, taskId: args.taskId } },
        body: args.body,
      });
    },
    onSuccess: async () => {
      toast.success("Task updated");
      setEditTaskOpen(false);
      setEditTaskId(null);
      await queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "tasks"],
      });
    },
    onError: (error) => {
      toast.error(`Update task failed: ${getApiErrorMessage(error)}`);
    },
  });

  const reorderTaskMutation = useMutation({
    mutationFn: async (taskId: string) => {
      const body: ProjectTaskReorderRequest = {
        updateTimeUtc: new Date().toISOString(),
      };
      return await apiPost("/api/projects/{projectId}/tasks/{taskId}/reorder", {
        params: { path: { projectId, taskId } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Task reordered");
      await queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "tasks"],
      });
    },
    onError: (error) => {
      toast.error(`Reorder failed: ${getApiErrorMessage(error)}`);
    },
  });

  const cancelTaskMutation = useMutation({
    mutationFn: async (taskId: string) => {
      return await apiPost("/api/projects/{projectId}/tasks/{taskId}/cancel", {
        params: { path: { projectId, taskId } },
      });
    },
    onSuccess: async () => {
      toast.success("Task canceled");
      await queryClient.invalidateQueries({
        queryKey: ["projects", projectId, "tasks"],
      });
    },
    onError: (error) => {
      toast.error(`Cancel failed: ${getApiErrorMessage(error)}`);
    },
  });

  const project = projectQuery.data;

  const tasks = React.useMemo(() => {
    const list = tasksQuery.data ?? [];
    return [...list].sort((a, b) => {
      const at = Date.parse(a.updateTime ?? a.createTime ?? "") || 0;
      const bt = Date.parse(b.updateTime ?? b.createTime ?? "") || 0;
      return bt - at;
    });
  }, [tasksQuery.data]);

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {projectQuery.isLoading
                ? "Loading project..."
                : project?.name ?? "Project"}
            </h1>
            {project ? (
              <span
                className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(
                  project.enable ? 2 : 0
                )}`}
              >
                {project.enable ? "Enabled" : "Disabled"}
              </span>
            ) : null}
          </div>
          {project?.description ? (
            <div className="text-sm text-muted-foreground">
              {project.description}
            </div>
          ) : (
            <div className="text-sm text-muted-foreground">
              Project details and tasks overview.
            </div>
          )}
          {project ? (
            <div className="text-xs text-muted-foreground">
              <span className="font-mono">{project.id}</span>
              <span className="mx-2">·</span>
              Updated: {formatDate(project.updateTime ?? project.createTime)}
            </div>
          ) : null}
        </div>

        <div className="flex flex-wrap gap-2 sm:justify-end">
          <ButtonGroup>
            <Button asChild variant="outline" size="sm">
              <Link href="/projects">Back</Link>
            </Button>

            <Dialog open={editOpen} onOpenChange={setEditOpen}>
              <DialogTrigger asChild>
                <Button variant="outline" size="sm" disabled={!project}>
                  Edit
                </Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <UiDialogTitle>Edit project</UiDialogTitle>
                  <UiDialogDescription>
                    Update project settings.
                  </UiDialogDescription>
                </DialogHeader>

                <div className="grid gap-4">
                  <div className="grid gap-2">
                    <Label htmlFor="edit-name">Name</Label>
                    <Input
                      id="edit-name"
                      value={editName}
                      onChange={(e) => setEditName(e.target.value)}
                    />
                  </div>

                  <div className="grid gap-2">
                    <Label htmlFor="edit-description">Description</Label>
                    <Textarea
                      id="edit-description"
                      value={editDescription}
                      onChange={(e) => setEditDescription(e.target.value)}
                      rows={4}
                    />
                  </div>

                  <label className="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      checked={editEnable}
                      onChange={(e) => setEditEnable(e.target.checked)}
                    />
                    Enable
                  </label>
                </div>

                <DialogFooter>
                  <DialogClose asChild>
                    <Button type="button" variant="outline">
                      Cancel
                    </Button>
                  </DialogClose>
                  <Button
                    type="button"
                    onClick={() =>
                      updateProjectMutation.mutate({
                        name: editName,
                        description: editDescription.length
                          ? editDescription
                          : null,
                        enable: editEnable,
                      })
                    }
                    disabled={
                      !editName.trim() || updateProjectMutation.isPending
                    }
                  >
                    {updateProjectMutation.isPending ? "Saving..." : "Save"}
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>

            <Button
              variant="destructive"
              size="sm"
              onClick={() => deleteProjectMutation.mutate()}
              disabled={!project || deleteProjectMutation.isPending}
            >
              {deleteProjectMutation.isPending ? "Deleting..." : "Delete"}
            </Button>
          </ButtonGroup>
        </div>
      </div>

      <Card>
        <CardHeader>
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
            <div className="space-y-1">
              <CardTitle>Tasks</CardTitle>
              <CardDescription>
                Display order: <code>updateTime</code> desc (fallback{" "}
                <code>createTime</code>) to reflect reorder actions.
              </CardDescription>
            </div>

            <Dialog open={createTaskOpen} onOpenChange={setCreateTaskOpen}>
              <Button
                size="sm"
                onClick={() => {
                  setCreateTaskOpen(true);
                }}
              >
                Create task
              </Button>
              <DialogContent>
                <DialogHeader>
                  <UiDialogTitle>Create task</UiDialogTitle>
                  <UiDialogDescription>
                    Create a task under this project. Tasks execute
                    asynchronously (scheduler will pick it up).
                  </UiDialogDescription>
                </DialogHeader>

                {workflowsQuery.isLoading ? (
                  <div className="text-sm text-muted-foreground">
                    Loading workflows...
                  </div>
                ) : workflowsQuery.isError ? (
                  <div className="text-sm text-destructive">
                    Failed to load workflows:{" "}
                    {getApiErrorMessage(workflowsQuery.error)}
                  </div>
                ) : (
                  <div className="grid gap-4">
                    <div className="grid gap-2">
                      <Label htmlFor="task-workflow">Workflow</Label>
                      <select
                        id="task-workflow"
                        value={createTaskWorkflowId}
                        onChange={(e) =>
                          setCreateTaskWorkflowId(e.target.value)
                        }
                        className="h-9 w-full rounded-md border bg-transparent px-3 text-sm shadow-sm"
                      >
                        <option value="" disabled>
                          Select a workflow...
                        </option>
                        {(workflowsQuery.data ?? [])
                          .filter((w) => w.enable)
                          .map((w) => (
                            <option key={w.id} value={w.id}>
                              {w.name}
                            </option>
                          ))}
                      </select>
                      <div className="text-xs text-muted-foreground">
                        Only enabled workflows are shown.
                      </div>
                    </div>

                    <div className="grid gap-2">
                      <Label htmlFor="task-description">Description</Label>
                      <Input
                        id="task-description"
                        value={createTaskDescription}
                        onChange={(e) =>
                          setCreateTaskDescription(e.target.value)
                        }
                        placeholder="What to do"
                      />
                    </div>

                    <div className="grid gap-2">
                      <Label htmlFor="task-input">Input</Label>
                      <Textarea
                        id="task-input"
                        value={createTaskInput}
                        onChange={(e) => setCreateTaskInput(e.target.value)}
                        placeholder="Task input"
                        rows={6}
                      />
                    </div>
                  </div>
                )}

                <DialogFooter>
                  <DialogClose asChild>
                    <Button type="button" variant="outline">
                      Cancel
                    </Button>
                  </DialogClose>
                  <Button
                    type="button"
                    onClick={() =>
                      createTaskMutation.mutate({
                        workflowId: createTaskWorkflowId,
                        description: createTaskDescription,
                        input: createTaskInput,
                      })
                    }
                    disabled={
                      workflowsQuery.isLoading ||
                      workflowsQuery.isError ||
                      !createTaskWorkflowId ||
                      !createTaskDescription.trim() ||
                      !createTaskInput.trim() ||
                      createTaskMutation.isPending
                    }
                  >
                    {createTaskMutation.isPending ? "Creating..." : "Create"}
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>
        </CardHeader>

        <CardContent>
          {tasksQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">
              Loading tasks...
            </div>
          ) : tasksQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load tasks: {getApiErrorMessage(tasksQuery.error)}
            </div>
          ) : tasks.length === 0 ? (
            <div className="text-sm text-muted-foreground">No tasks.</div>
          ) : (
            <div className="space-y-3">
              {tasks.slice(0, 20).map((t) => {
                const isPending = t.status === 0;
                return (
                  <div key={t.id} className="rounded-lg border p-4">
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                      <div className="min-w-0 space-y-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <div className="font-medium">{t.description}</div>
                          <span
                            className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(
                              t.status
                            )}`}
                          >
                            {statusLabel(t.status)}
                          </span>
                        </div>

                        <div className="text-xs text-muted-foreground">
                          <span className="font-mono">{t.id}</span>
                          <span className="mx-2">·</span>
                          Workflow:{" "}
                          <span className="font-mono">{t.workflowId}</span>
                        </div>

                        <div className="text-xs text-muted-foreground">
                          Created: {formatDate(t.createTime)} · Updated:{" "}
                          {formatDate(t.updateTime)}
                        </div>

                        {t.errorMessage ? (
                          <div className="text-xs text-destructive">
                            Error: {t.errorMessage}
                          </div>
                        ) : null}
                      </div>

                      <div className="flex flex-wrap gap-2 sm:justify-end">
                        <ButtonGroup>
                          <Button asChild variant="outline" size="sm">
                            <Link href={`/projects/${projectId}/tasks/${t.id}`}>
                              View
                            </Link>
                          </Button>

                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => {
                              setEditTaskId(t.id);
                              setEditTaskDescription(t.description ?? "");
                              setEditTaskInput(t.input ?? "");
                              setEditTaskOpen(true);
                            }}
                            disabled={!isPending}
                            title={
                              isPending
                                ? "Edit task"
                                : "Only pending tasks can be edited"
                            }
                          >
                            Edit
                          </Button>

                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => reorderTaskMutation.mutate(t.id)}
                            disabled={
                              !isPending || reorderTaskMutation.isPending
                            }
                            title={
                              isPending
                                ? "Reorder (bump updateTimeUtc)"
                                : "Only pending tasks can be reordered"
                            }
                          >
                            {reorderTaskMutation.isPending
                              ? "Reordering..."
                              : "Reorder"}
                          </Button>

                          <Button
                            variant="destructive"
                            size="sm"
                            onClick={() => cancelTaskMutation.mutate(t.id)}
                            disabled={
                              t.status === 2 ||
                              t.status === 3 ||
                              t.status === 4 ||
                              cancelTaskMutation.isPending
                            }
                            title={
                              t.status === 2 || t.status === 3 || t.status === 4
                                ? "Task already completed"
                                : "Cancel task"
                            }
                          >
                            {cancelTaskMutation.isPending
                              ? "Canceling..."
                              : "Cancel"}
                          </Button>
                        </ButtonGroup>
                      </div>
                    </div>
                  </div>
                );
              })}

              {tasks.length > 20 ? (
                <div className="text-xs text-muted-foreground">
                  Showing first 20 tasks.
                </div>
              ) : null}
            </div>
          )}
        </CardContent>
      </Card>

      <Dialog open={editTaskOpen} onOpenChange={setEditTaskOpen}>
        <DialogContent>
          <DialogHeader>
            <UiDialogTitle>Edit task</UiDialogTitle>
            <UiDialogDescription>
              Only pending tasks can be updated.
            </UiDialogDescription>
          </DialogHeader>

          <div className="grid gap-4">
            <div className="grid gap-2">
              <Label htmlFor="edit-task-description">Description</Label>
              <Input
                id="edit-task-description"
                value={editTaskDescription}
                onChange={(e) => setEditTaskDescription(e.target.value)}
              />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="edit-task-input">Input</Label>
              <Textarea
                id="edit-task-input"
                value={editTaskInput}
                onChange={(e) => setEditTaskInput(e.target.value)}
                rows={6}
              />
            </div>
          </div>

          <DialogFooter>
            <DialogClose asChild>
              <Button type="button" variant="outline">
                Cancel
              </Button>
            </DialogClose>
            <Button
              type="button"
              onClick={() => {
                if (!editTaskId) return;
                updateTaskMutation.mutate({
                  taskId: editTaskId,
                  body: {
                    description: editTaskDescription,
                    input: editTaskInput,
                  },
                });
              }}
              disabled={
                !editTaskId ||
                !editTaskDescription.trim() ||
                !editTaskInput.trim() ||
                updateTaskMutation.isPending
              }
            >
              {updateTaskMutation.isPending ? "Saving..." : "Save"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {projectQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load project: {getApiErrorMessage(projectQuery.error)}
        </div>
      ) : null}
    </div>
  );
}
