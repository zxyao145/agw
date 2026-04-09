"use client";

import * as React from "react";
import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
} from "@tanstack/react-query";
import { Eye, Pencil, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { StaticTable } from "@/components/static-table";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Empty } from "@/components/ui/empty";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";

import { getApiErrorMessage } from "../../(agents)/agents/components/utils";

type JobDto = {
  id: string;
  projectId: string;
  agentType: number | null;
  agentId: string | null;
  name: string;
  prompt: string | null;
  triggerType: number;
  triggerValue: string;
  nextRunTime: string;
  status: number;
  isEnabled: boolean;
  retryCount: number;
  maxRetryCount: number;
  lastError: string | null;
};

type TaskExecutionLogDto = {
  id: string;
  taskId: string;
  startTime: string;
  endTime: string | null;
  success: boolean;
  attempt: number;
  errorMessage: string | null;
};

type JobFormState = {
  projectId: string;
  agentType: number | null;
  agentId: string;
  name: string;
  prompt: string;
  triggerType: number;
  triggerValue: string;
  nextRunTime: string;
  maxRetryCount: number;
  isEnabled: boolean;
  status?: number;
};

type JobRequest = {
  projectId: string;
  agentType: number | null;
  agentId: string | null;
  name: string;
  prompt: string | null;
  triggerType: number;
  triggerValue: string;
  maxRetryCount: number;
  isEnabled: boolean;
  status?: number;
};

type ProjectDto = {
  id: string;
  name: string;
};

type AgentOption = {
  id: string;
  label: string;
  type: 0 | 1;
};

type JobDialogProps = {
  mode: "create" | "edit";
  open: boolean;
  onOpenChange: (open: boolean) => void;
  form: JobFormState;
  setForm: React.Dispatch<React.SetStateAction<JobFormState>>;
  projects: ProjectDto[];
  assignableAgents: AgentOption[];
  areAssignableAgentsReady: boolean;
  onSubmit: () => void;
  isSubmitting: boolean;
};

const jobsPath = "/api/jobs" as never;
const jobItemPath = "/api/jobs/{id}" as never;
const jobLogsPath = "/api/jobs/{id}/logs" as never;
const TRIGGER_TYPE_ONCE = 1;
const TRIGGER_TYPE_INTERVAL = 2;
const TRIGGER_TYPE_CRON = 3;
const DEFAULT_INTERVAL_TRIGGER_VALUE = "00:01:00";
const DEFAULT_CRON_TRIGGER_VALUE = "*/1 * * * *";

function createDefaultOnceRunTime(): string {
  return new Date(Date.now() + 3 * 60 * 1000).toISOString();
}

function createDefaultJobFormState(): JobFormState {
  return {
    projectId: "11111111-1111-1111-1111-000000000001",
    agentType: 0,
    agentId: "",
    name: "",
    prompt: "",
    triggerType: TRIGGER_TYPE_CRON,
    triggerValue: DEFAULT_CRON_TRIGGER_VALUE,
    nextRunTime: createDefaultOnceRunTime(),
    maxRetryCount: 3,
    isEnabled: true,
  };
}

function createEditFormState(job: JobDto): JobFormState {
  const nextRunTime =
    job.triggerType === TRIGGER_TYPE_ONCE && isValidDateTimeValue(job.triggerValue)
      ? job.triggerValue
      : job.nextRunTime;

  return {
    projectId: job.projectId,
    agentType: job.agentType,
    agentId: job.agentId ?? "",
    name: job.name,
    prompt: job.prompt ?? "",
    triggerType: job.triggerType,
    triggerValue: job.triggerValue,
    nextRunTime,
    maxRetryCount: job.maxRetryCount,
    isEnabled: job.isEnabled,
    status: job.status,
  };
}

function buildJobRequest(form: JobFormState, mode: "create" | "edit"): JobRequest {
  const triggerValue = resolveTriggerValue(form);
  const payload: JobRequest = {
    projectId: form.projectId.trim(),
    agentType: form.agentType,
    agentId: form.agentId.trim() || null,
    name: form.name.trim(),
    prompt: form.prompt.trim() || null,
    triggerType: form.triggerType,
    triggerValue,
    maxRetryCount: form.maxRetryCount,
    isEnabled: form.isEnabled,
    status: mode === "edit" ? form.status : undefined,
  };

  if (!payload.name) {
    throw new Error("Job name is required.");
  }

  if (!payload.projectId) {
    throw new Error("Project ID is required.");
  }

  if (!payload.triggerValue) {
    throw new Error("Trigger value is required.");
  }

  if (
    ![TRIGGER_TYPE_ONCE, TRIGGER_TYPE_INTERVAL, TRIGGER_TYPE_CRON].includes(payload.triggerType)
  ) {
    throw new Error("Trigger type is invalid.");
  }

  if (payload.agentType !== null && ![0, 1].includes(payload.agentType)) {
    throw new Error("Agent type is invalid.");
  }

  if (!Number.isFinite(payload.maxRetryCount) || payload.maxRetryCount < 0) {
    throw new Error("Max retry count must be zero or greater.");
  }

  if (mode === "edit" && payload.status !== undefined && ![1, 2, 3].includes(payload.status)) {
    throw new Error("Status is invalid.");
  }

  return payload;
}

function isValidDateTimeValue(value?: string | null): value is string {
  if (!value) {
    return false;
  }

  return !Number.isNaN(new Date(value).getTime());
}

function isPositiveIntervalValue(value?: string | null): value is string {
  if (!value) {
    return false;
  }

  const match = value.trim().match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/);
  if (!match) {
    return false;
  }

  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4] ?? 0);
  const totalMilliseconds = (((days * 24 + hours) * 60 + minutes) * 60 + seconds) * 1000;

  return totalMilliseconds > 0;
}

function isCronValue(value?: string | null): value is string {
  if (!value) {
    return false;
  }

  return value.trim().split(/\s+/).length === 5;
}

function getNextRunTimeForOnce(form: JobFormState): string {
  if (isValidDateTimeValue(form.triggerValue)) {
    return form.triggerValue;
  }

  return createDefaultOnceRunTime();
}

function resolveTriggerValue(form: JobFormState): string {
  switch (form.triggerType) {
    case TRIGGER_TYPE_ONCE:
      return getNextRunTimeForOnce(form).trim();
    case TRIGGER_TYPE_INTERVAL:
      return isPositiveIntervalValue(form.triggerValue)
        ? form.triggerValue.trim()
        : DEFAULT_INTERVAL_TRIGGER_VALUE;
    case TRIGGER_TYPE_CRON:
      return isCronValue(form.triggerValue) ? form.triggerValue.trim() : DEFAULT_CRON_TRIGGER_VALUE;
    default:
      return form.triggerValue.trim();
  }
}

function formatDateTime(value?: string | null): string {
  if (!value) return "-";

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

function toLocalInput(dateTime?: string | null): string {
  if (!dateTime) {
    return "";
  }

  const date = new Date(dateTime);
  if (Number.isNaN(date.getTime())) {
    return "";
  }

  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function fromLocalInput(dateTime: string): string {
  if (!dateTime) {
    return "";
  }

  const parsed = new Date(dateTime);
  if (Number.isNaN(parsed.getTime())) {
    return "";
  }

  return parsed.toISOString();
}

function getTriggerTypeLabel(triggerType: number): string {
  switch (triggerType) {
    case TRIGGER_TYPE_ONCE:
      return "Once";
    case TRIGGER_TYPE_INTERVAL:
      return "Interval";
    case TRIGGER_TYPE_CRON:
      return "Cron";
    default:
      return `Unknown (${triggerType})`;
  }
}

function getStatusLabel(status: number): string {
  switch (status) {
    case 1:
      return "Pending";
    case 2:
      return "Running";
    case 3:
      return "Paused";
    default:
      return `Unknown (${status})`;
  }
}

function getStatusVariant(status: number): "default" | "secondary" | "outline" {
  switch (status) {
    case 2:
      return "default";
    case 3:
      return "outline";
    case 1:
    default:
      return "secondary";
  }
}

function getAgentTypeLabel(agentType: number | null): string {
  switch (agentType) {
    case 0:
      return "Agent";
    case 1:
      return "Agentflow";
    default:
      return "Not assigned";
  }
}

export default function JobsPage() {
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editOpen, setEditOpen] = React.useState(false);
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [detailsOpen, setDetailsOpen] = React.useState(false);
  const [createForm, setCreateForm] = React.useState<JobFormState>(createDefaultJobFormState);
  const [editForm, setEditForm] = React.useState<JobFormState>(createDefaultJobFormState);
  const [editingJob, setEditingJob] = React.useState<JobDto | null>(null);
  const [deletingJob, setDeletingJob] = React.useState<JobDto | null>(null);
  const [viewingJob, setViewingJob] = React.useState<JobDto | null>(null);

  const jobsQuery = useQuery({
    queryKey: ["jobs"],
    queryFn: async () => {
      return (await apiGet(jobsPath)) as JobDto[];
    },
  });

  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => (await apiGet("/api/projects")) as unknown as ProjectDto[],
  });

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () =>
      (await apiGet("/api/agents")) as unknown as Array<{
        id: string;
        displayName: string;
        name: string;
      }>,
  });

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () =>
      (await apiGet("/api/agentflows")) as unknown as Array<{ id: string; name: string }>,
  });

  const assignableAgents = React.useMemo<AgentOption[]>(() => {
    const agentOptions =
      agentsQuery.data?.map((agent) => ({
        id: agent.id,
        label: agent.displayName?.trim() || agent.name,
        type: 0 as const,
      })) ?? [];

    const agentflowOptions =
      agentflowsQuery.data?.map((agentflow) => ({
        id: agentflow.id,
        label: agentflow.name,
        type: 1 as const,
      })) ?? [];

    return [...agentOptions, ...agentflowOptions];
  }, [agentsQuery.data, agentflowsQuery.data]);
  const areAssignableAgentsReady = agentsQuery.isSuccess && agentflowsQuery.isSuccess;

  const viewingJobId = viewingJob?.id ?? null;

  const jobDetailQuery = useQuery({
    queryKey: ["job", viewingJobId],
    enabled: Boolean(detailsOpen && viewingJobId),
    queryFn: async () =>
      (await apiGet(jobItemPath, {
        params: { path: { id: viewingJobId as string } },
      } as never)) as JobDto,
  });

  const jobLogsQuery = useQuery({
    queryKey: ["job-logs", viewingJobId],
    enabled: Boolean(detailsOpen && viewingJobId),
    queryFn: async () =>
      (await apiGet(jobLogsPath, {
        params: { path: { id: viewingJobId as string } },
      } as never)) as TaskExecutionLogDto[],
  });

  const createMutation = useMutation({
    mutationFn: async (body: JobRequest) => {
      return await apiPost(jobsPath, { body } as never);
    },
    onSuccess: async () => {
      toast.success("Job created");
      setCreateOpen(false);
      setCreateForm(createDefaultJobFormState());
      await queryClient.invalidateQueries({ queryKey: ["jobs"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: JobRequest }) => {
      return await apiPut(jobItemPath, {
        params: { path: { id } },
        body,
      } as never);
    },
    onSuccess: async (_, variables) => {
      toast.success("Job updated");
      setEditOpen(false);
      setEditingJob(null);
      setEditForm(createDefaultJobFormState());
      await queryClient.invalidateQueries({ queryKey: ["jobs"] });
      await queryClient.invalidateQueries({ queryKey: ["job", variables.id] });
      await queryClient.invalidateQueries({ queryKey: ["job-logs", variables.id] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete(jobItemPath, {
        params: { path: { id } },
      } as never);
    },
    onSuccess: async (_, deletedId) => {
      toast.success("Job deleted");
      setDeleteOpen(false);
      setDeletingJob(null);

      if (viewingJob?.id === deletedId) {
        setDetailsOpen(false);
        setViewingJob(null);
      }

      await queryClient.invalidateQueries({ queryKey: ["jobs"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const openEditDialog = (job: JobDto) => {
    setEditingJob(job);
    setEditForm(createEditFormState(job));
    setEditOpen(true);
  };

  const openDetailsDialog = (job: JobDto) => {
    setViewingJob(job);
    setDetailsOpen(true);
  };

  const openDeleteDialog = (job: JobDto) => {
    setDeletingJob(job);
    setDeleteOpen(true);
  };

  const closeCreateDialog = (open: boolean) => {
    setCreateOpen(open);
    if (!open && !createMutation.isPending) {
      setCreateForm(createDefaultJobFormState());
    }
  };

  const closeEditDialog = (open: boolean) => {
    setEditOpen(open);
    if (!open && !updateMutation.isPending) {
      setEditingJob(null);
      setEditForm(createDefaultJobFormState());
    }
  };

  const closeDeleteDialog = (open: boolean) => {
    setDeleteOpen(open);
    if (!open && !deleteMutation.isPending) {
      setDeletingJob(null);
    }
  };

  const closeDetailsDialog = (open: boolean) => {
    setDetailsOpen(open);
    if (!open) {
      setViewingJob(null);
    }
  };

  const submitCreate = () => {
    try {
      createMutation.mutate(buildJobRequest(createForm, "create"));
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  const submitEdit = () => {
    if (!editingJob) return;

    try {
      updateMutation.mutate({
        id: editingJob.id,
        body: buildJobRequest(editForm, "edit"),
      });
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  return (
    <div className="w-full space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Jobs</h1>
          <p className="text-sm text-muted-foreground">
            Schedule project work with once, interval, or cron-based execution rules.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => {
              jobsQuery.refetch();
            }}
            disabled={jobsQuery.isFetching}
          >
            Refresh
          </Button>

          <Button onClick={() => setCreateOpen(true)}>
            <Plus className="mr-2 h-4 w-4" />
            Add Job
          </Button>
        </div>
      </div>

      {jobsQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading...</div>
      ) : jobsQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load jobs: {getApiErrorMessage(jobsQuery.error)}
        </div>
      ) : (
        <StaticTable isEmpty={jobsQuery.data === undefined || jobsQuery.data.length === 0}>
          <Empty>
            <div className="text-sm text-muted-foreground">
              No jobs found. Create a scheduled job to get started.
            </div>
          </Empty>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Project / Agent</TableHead>
              <TableHead>Trigger</TableHead>
              <TableHead>Next Run</TableHead>
              <TableHead>Status</TableHead>
              <TableHead className="w-40 text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {jobsQuery.data?.map((job) => (
              <TableRow key={job.id}>
                <TableCell className="min-w-52">
                  <div className="font-medium">{job.name}</div>
                  <div className="font-mono text-xs break-all text-muted-foreground">{job.id}</div>
                </TableCell>
                <TableCell className="min-w-56">
                  <div className="text-sm break-all">{job.projectId}</div>
                  <div className="text-xs text-muted-foreground">
                    {getAgentTypeLabel(job.agentType)}
                    {job.agentId ? ` · ${job.agentId}` : ""}
                  </div>
                </TableCell>
                <TableCell className="min-w-52">
                  <div className="font-medium">{getTriggerTypeLabel(job.triggerType)}</div>
                  <div className="font-mono text-xs break-all text-muted-foreground">
                    {job.triggerValue}
                  </div>
                </TableCell>
                <TableCell className="min-w-44 text-sm text-muted-foreground">
                  <div>{formatDateTime(job.nextRunTime)}</div>
                </TableCell>
                <TableCell className="min-w-52">
                  <div className="flex flex-col items-start gap-2">
                    <Badge variant={getStatusVariant(job.status)}>
                      {getStatusLabel(job.status)}
                    </Badge>
                    <div className="text-xs text-muted-foreground">
                      {job.isEnabled ? "Enabled" : "Disabled"} · Retry {job.retryCount}/
                      {job.maxRetryCount}
                    </div>
                    {job.lastError ? (
                      <div className="line-clamp-2 text-xs text-destructive">{job.lastError}</div>
                    ) : null}
                  </div>
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-2">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => openDetailsDialog(job)}
                    >
                      <Eye className="h-4 w-4" />
                    </Button>
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => openEditDialog(job)}
                    >
                      <Pencil className="h-4 w-4" />
                    </Button>
                    <Button
                      type="button"
                      variant="destructive"
                      size="sm"
                      onClick={() => openDeleteDialog(job)}
                      disabled={deleteMutation.isPending}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </StaticTable>
      )}

      <JobDialog
        mode="create"
        open={createOpen}
        onOpenChange={closeCreateDialog}
        form={createForm}
        setForm={setCreateForm}
        projects={projectsQuery.data ?? []}
        assignableAgents={assignableAgents}
        areAssignableAgentsReady={areAssignableAgentsReady}
        onSubmit={submitCreate}
        isSubmitting={createMutation.isPending}
      />

      <JobDialog
        mode="edit"
        open={editOpen}
        onOpenChange={closeEditDialog}
        form={editForm}
        setForm={setEditForm}
        projects={projectsQuery.data ?? []}
        assignableAgents={assignableAgents}
        areAssignableAgentsReady={areAssignableAgentsReady}
        onSubmit={submitEdit}
        isSubmitting={updateMutation.isPending}
      />

      <DeleteJobDialog
        open={deleteOpen}
        onOpenChange={closeDeleteDialog}
        deletingJob={deletingJob}
        deleteMutation={deleteMutation}
      />

      <JobDetailsDialog
        open={detailsOpen}
        onOpenChange={closeDetailsDialog}
        viewingJob={viewingJob}
        detailJob={jobDetailQuery.data ?? viewingJob}
        detailLoading={jobDetailQuery.isLoading}
        detailError={jobDetailQuery.isError ? getApiErrorMessage(jobDetailQuery.error) : null}
        logs={jobLogsQuery.data ?? []}
        logsLoading={jobLogsQuery.isLoading}
        logsError={jobLogsQuery.isError ? getApiErrorMessage(jobLogsQuery.error) : null}
        onEdit={(job) => {
          setDetailsOpen(false);
          openEditDialog(job);
        }}
      />
    </div>
  );
}

function DeleteJobDialog({
  open,
  onOpenChange,
  deletingJob,
  deleteMutation,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  deletingJob: JobDto | null;
  deleteMutation: UseMutationResult<unknown, Error, string, unknown>;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="sm">
        <DialogHeader>
          <DialogTitle>Delete job</DialogTitle>
          <DialogDescription>
            Are you sure you want to delete job &quot;{deletingJob?.name}&quot;? This action cannot
            be undone.
          </DialogDescription>
        </DialogHeader>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            type="button"
            variant="destructive"
            onClick={() => {
              if (deletingJob) {
                deleteMutation.mutate(deletingJob.id);
              }
            }}
            disabled={deleteMutation.isPending}
          >
            {deleteMutation.isPending ? "Deleting..." : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function JobDialog({
  mode,
  open,
  onOpenChange,
  form,
  setForm,
  projects,
  assignableAgents,
  areAssignableAgentsReady,
  onSubmit,
  isSubmitting,
}: JobDialogProps) {
  const title = mode === "create" ? "Create Job" : "Edit Job";
  const submitLabel = mode === "create" ? "Create Job" : "Save Changes";
  const isOnceTrigger = form.triggerType === TRIGGER_TYPE_ONCE;
  const isIntervalTrigger = form.triggerType === TRIGGER_TYPE_INTERVAL;
  const isCronTrigger = form.triggerType === TRIGGER_TYPE_CRON;
  const filteredAgents = React.useMemo(
    () =>
      assignableAgents.filter((agent) => form.agentType !== null && agent.type === form.agentType),
    [assignableAgents, form.agentType],
  );

  React.useEffect(() => {
    if (form.agentType === null) {
      if (form.agentId) {
        setForm((current) => ({ ...current, agentId: "" }));
      }
      return;
    }

    if (!areAssignableAgentsReady) {
      return;
    }

    const matchesSelectedType = assignableAgents.some(
      (agent) => agent.id === form.agentId && agent.type === form.agentType,
    );
    if (!matchesSelectedType && form.agentId) {
      setForm((current) => ({ ...current, agentId: "" }));
    }
  }, [areAssignableAgentsReady, assignableAgents, form.agentId, form.agentType, setForm]);

  React.useEffect(() => {
    setForm((current) => {
      if (current.triggerType === TRIGGER_TYPE_ONCE) {
        const onceRunTime = getNextRunTimeForOnce(current);
        if (current.triggerValue === onceRunTime && current.nextRunTime === onceRunTime) {
          return current;
        }

        return {
          ...current,
          triggerValue: onceRunTime,
          nextRunTime: onceRunTime,
        };
      }

      if (current.triggerType === TRIGGER_TYPE_INTERVAL) {
        const nextTriggerValue = isPositiveIntervalValue(current.triggerValue)
          ? current.triggerValue
          : DEFAULT_INTERVAL_TRIGGER_VALUE;
        if (current.triggerValue === nextTriggerValue) {
          return current;
        }

        return {
          ...current,
          triggerValue: nextTriggerValue,
        };
      }

      if (current.triggerType === TRIGGER_TYPE_CRON) {
        const nextTriggerValue = isCronValue(current.triggerValue)
          ? current.triggerValue
          : DEFAULT_CRON_TRIGGER_VALUE;
        if (current.triggerValue === nextTriggerValue) {
          return current;
        }

        return {
          ...current,
          triggerValue: nextTriggerValue,
        };
      }

      return current;
    });
  }, [form.triggerType, setForm]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="2xl" className="flex max-h-[90vh] flex-col overflow-hidden">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            Configure the project target, agent routing, and trigger schedule for this job.
          </DialogDescription>
        </DialogHeader>

        <div className="grid max-h-[65vh] grid-cols-1 gap-6 overflow-y-auto pr-1 sm:grid-cols-2">
          <div className="space-y-2">
            <Label htmlFor={`${mode}-job-name`}>Name</Label>
            <Input
              id={`${mode}-job-name`}
              value={form.name}
              placeholder="Nightly summarizer"
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  name: event.target.value,
                }))
              }
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-project-id`}>Project ID</Label>
            <Select
              value={form.projectId}
              onValueChange={(value) =>
                setForm((current) => ({
                  ...current,
                  projectId: value,
                }))
              }
            >
              <SelectTrigger id={`${mode}-project-id`} className="w-full">
                <SelectValue placeholder="Select a project" />
              </SelectTrigger>
              <SelectContent>
                {projects.map((project) => (
                  <SelectItem key={project.id} value={project.id}>
                    {project.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-agent-type`}>Agent Type</Label>
            <Select
              value={form.agentType === null ? "none" : String(form.agentType)}
              onValueChange={(value) =>
                setForm((current) => ({
                  ...current,
                  agentType: value === "none" ? null : Number(value),
                }))
              }
            >
              <SelectTrigger id={`${mode}-agent-type`} className="w-full">
                <SelectValue placeholder="Select agent type" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="none">Not assigned</SelectItem>
                <SelectItem value="0">Agent</SelectItem>
                <SelectItem value="1">Agentflow</SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-agent-id`}>Agent ID</Label>
            <Select
              value={form.agentId || "none"}
              onValueChange={(value) =>
                setForm((current) => ({
                  ...current,
                  agentId: value === "none" ? "" : value,
                }))
              }
              disabled={form.agentType === null}
            >
              <SelectTrigger id={`${mode}-agent-id`} className="w-full">
                <SelectValue
                  placeholder={
                    form.agentType === null ? "Select agent type first" : "Optional agent"
                  }
                />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="none">Not assigned</SelectItem>
                {filteredAgents.map((agent) => (
                  <SelectItem key={agent.id} value={agent.id}>
                    {agent.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-trigger-type`}>Trigger Type</Label>
            <Select
              value={String(form.triggerType)}
              onValueChange={(value) =>
                setForm((current) => ({
                  ...current,
                  triggerType: Number(value),
                }))
              }
            >
              <SelectTrigger id={`${mode}-trigger-type`} className="w-full">
                <SelectValue placeholder="Select trigger type" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={String(TRIGGER_TYPE_ONCE)}>Once</SelectItem>
                <SelectItem value={String(TRIGGER_TYPE_INTERVAL)}>Interval</SelectItem>
                <SelectItem value={String(TRIGGER_TYPE_CRON)}>Cron</SelectItem>
              </SelectContent>
            </Select>
          </div>

          {isOnceTrigger ? (
            <div className="space-y-2">
              <Label htmlFor={`${mode}-next-run-time`}>Run Time</Label>
              <Input
                id={`${mode}-next-run-time`}
                type="datetime-local"
                value={toLocalInput(getNextRunTimeForOnce(form))}
                onChange={(event) => {
                  const nextRunTime = fromLocalInput(event.target.value);
                  setForm((current) => ({
                    ...current,
                    nextRunTime,
                    triggerValue: nextRunTime,
                  }));
                }}
              />
              <p className="text-xs text-muted-foreground">
                Pick the exact time to run this job once. This value will be sent as the once
                trigger value.
              </p>
            </div>
          ) : null}

          {isIntervalTrigger || isCronTrigger ? (
            <div className="space-y-2">
              <Label htmlFor={`${mode}-trigger-value`}>
                {isIntervalTrigger ? "Interval" : "Trigger Value"}
              </Label>
              <Input
                id={`${mode}-trigger-value`}
                value={form.triggerValue}
                placeholder={isIntervalTrigger ? DEFAULT_INTERVAL_TRIGGER_VALUE : "*/5 * * * *"}
                onChange={(event) =>
                  setForm((current) => ({
                    ...current,
                    triggerValue: event.target.value,
                  }))
                }
              />
              <p className="text-xs text-muted-foreground">
                {isIntervalTrigger
                  ? "Use a .NET TimeSpan value such as 00:01:00 for a one-minute interval."
                  : "Use a standard cron string such as */5 * * * *."}
              </p>
            </div>
          ) : null}

          <div className="space-y-2">
            <Label htmlFor={`${mode}-max-retry-count`}>Max Retry Count</Label>
            <Input
              id={`${mode}-max-retry-count`}
              type="number"
              min={0}
              value={String(form.maxRetryCount)}
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  maxRetryCount: Number(event.target.value || 0),
                }))
              }
            />
          </div>

          {mode === "edit" ? (
            <div className="space-y-2">
              <Label htmlFor={`${mode}-status`}>Status</Label>
              <Select
                value={String(form.status ?? 1)}
                onValueChange={(value) =>
                  setForm((current) => ({
                    ...current,
                    status: Number(value),
                  }))
                }
              >
                <SelectTrigger id={`${mode}-status`} className="w-full">
                  <SelectValue placeholder="Select status" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="1">Pending</SelectItem>
                  <SelectItem value="2">Running</SelectItem>
                  <SelectItem value="3">Paused</SelectItem>
                </SelectContent>
              </Select>
            </div>
          ) : null}

          <div className={`space-y-2 ${mode === "edit" ? "" : "sm:col-span-2"}`}>
            <Label htmlFor={`${mode}-enabled`}>Enabled</Label>
            <div className="flex min-h-9 items-center rounded-md border px-3">
              <Switch
                id={`${mode}-enabled`}
                checked={form.isEnabled}
                onCheckedChange={(checked) =>
                  setForm((current) => ({
                    ...current,
                    isEnabled: checked,
                  }))
                }
              />
              <Label htmlFor={`${mode}-enabled`} className="ml-3 text-sm font-normal">
                Allow this job to be picked up by the scheduler
              </Label>
            </div>
          </div>

          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-prompt`}>Prompt</Label>
            <p className="text-xs text-muted-foreground">
              Optional execution prompt passed into the scheduled run.
            </p>
            <Textarea
              id={`${mode}-prompt`}
              rows={6}
              value={form.prompt}
              placeholder="Summarize the latest project activity and post the result to the team channel."
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  prompt: event.target.value,
                }))
              }
            />
          </div>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            onClick={() => onOpenChange(false)}
            disabled={isSubmitting}
          >
            Cancel
          </Button>
          <Button type="button" onClick={onSubmit} disabled={isSubmitting}>
            {isSubmitting ? "Saving..." : submitLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function JobDetailsDialog({
  open,
  onOpenChange,
  viewingJob,
  detailJob,
  detailLoading,
  detailError,
  logs,
  logsLoading,
  logsError,
  onEdit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  viewingJob: JobDto | null;
  detailJob: JobDto | null | undefined;
  detailLoading: boolean;
  detailError: string | null;
  logs: TaskExecutionLogDto[];
  logsLoading: boolean;
  logsError: string | null;
  onEdit: (job: JobDto) => void;
}) {
  const job = detailJob ?? viewingJob;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="2xl" className="max-h-[90vh] overflow-hidden">
        <DialogHeader>
          <DialogTitle>{job?.name ?? "Job details"}</DialogTitle>
          <DialogDescription>
            Review the current job configuration and recent execution attempts.
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 max-h-[65vh] flex-1 space-y-6 overflow-y-auto pr-1">
          {detailLoading && !job ? (
            <div className="text-sm text-muted-foreground">Loading details...</div>
          ) : detailError ? (
            <div className="text-sm text-destructive">Failed to load details: {detailError}</div>
          ) : job ? (
            <>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <DetailField label="Job ID" value={job.id} mono />
                <DetailField label="Project ID" value={job.projectId} mono />
                <DetailField label="Agent Type" value={getAgentTypeLabel(job.agentType)} />
                <DetailField label="Agent ID" value={job.agentId ?? "-"} mono />
                <DetailField label="Trigger Type" value={getTriggerTypeLabel(job.triggerType)} />
                <DetailField label="Trigger Value" value={job.triggerValue} mono />
                <DetailField label="Next Run" value={formatDateTime(job.nextRunTime)} />
                <DetailField label="Status" value={getStatusLabel(job.status)} />
                <DetailField
                  label="Execution"
                  value={`${job.isEnabled ? "Enabled" : "Disabled"} · Retry ${job.retryCount}/${job.maxRetryCount}`}
                />
              </div>

              <div className="space-y-2">
                <Label>Prompt</Label>
                <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm whitespace-pre-wrap break-words text-muted-foreground">
                  {job.prompt?.trim() ? job.prompt : "No prompt configured."}
                </div>
              </div>

              {job.lastError ? (
                <div className="space-y-2">
                  <Label>Last Error</Label>
                  <div className="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm whitespace-pre-wrap break-words text-destructive">
                    {job.lastError}
                  </div>
                </div>
              ) : null}

              <div className="space-y-3">
                <div>
                  <Label>Execution Logs</Label>
                  <p className="text-xs text-muted-foreground">
                    Recent attempts for the selected scheduled job.
                  </p>
                </div>

                {logsError ? (
                  <div className="text-sm text-destructive">Failed to load logs: {logsError}</div>
                ) : logsLoading ? (
                  <div className="text-sm text-muted-foreground">Loading logs...</div>
                ) : logs.length === 0 ? (
                  <div className="text-sm text-muted-foreground">No execution logs found.</div>
                ) : (
                  <div className="space-y-2">
                    {logs.map((log) => (
                      <div key={log.id} className="rounded-md border px-3 py-2">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <div className="flex items-center gap-2">
                            <Badge variant={log.success ? "default" : "destructive"}>
                              {log.success ? "Succeeded" : "Failed"}
                            </Badge>
                            <span className="text-sm font-medium">Attempt {log.attempt}</span>
                          </div>
                          <div className="text-xs text-muted-foreground">
                            {formatDateTime(log.startTime)}
                            {log.endTime ? ` -> ${formatDateTime(log.endTime)}` : ""}
                          </div>
                        </div>
                        {log.errorMessage ? (
                          <div className="mt-2 text-sm whitespace-pre-wrap break-words text-destructive">
                            {log.errorMessage}
                          </div>
                        ) : null}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </>
          ) : (
            <div className="text-sm text-muted-foreground">Select a job to inspect details.</div>
          )}
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Close
          </Button>
          <Button type="button" onClick={() => job && onEdit(job)} disabled={!job}>
            Edit Job
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function DetailField({
  label,
  value,
  mono = false,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div className="space-y-2">
      <Label>{label}</Label>
      <div
        className={`rounded-md border bg-muted/30 px-3 py-2 text-sm break-words text-muted-foreground ${
          mono ? "font-mono text-xs" : ""
        }`}
      >
        {value}
      </div>
    </div>
  );
}
