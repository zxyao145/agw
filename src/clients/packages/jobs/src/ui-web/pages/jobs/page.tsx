"use client";

import * as React from "react";
import Link from "next/link";
import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
} from "@agw/components/query";
import { Eye, ListChecks, Pencil, Settings2, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiGet, apiPost } from "@agw/api";
import type { components } from "@agw/api";
import { AgentSelector } from "@agw/chat";
import { DateTimePicker } from "@agw/components";
import { StaticTable } from "@agw/components";
import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Empty } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@agw/components";
import { Switch } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";
import { Textarea } from "@agw/components";
import { getApiErrorMessage } from "@agw/api";
import { formatLocalDateTime } from "@agw/components";
import { ButtonGroup } from "@agw/components";
import {
  getDeliveryStateLabel,
  getNextRunState,
  getScheduleError,
  isCronValue,
  isPositiveIsoDuration,
  SCHEDULE_KIND_CRON,
  SCHEDULE_KIND_INTERVAL,
  SCHEDULE_KIND_ONCE,
  type JobSchedule,
} from "./schedule-value";
import { JobRunPagination } from "./job-run-pagination";
import { JobRunResultBadge } from "./job-run-result";
import { useJobRunPage } from "./use-job-run-page";

type JobDto = components["schemas"]["JobResponse"];
type JobRunDto = components["schemas"]["JobRunResponse"];
type JobCreateRequest = components["schemas"]["JobCreateRequest"];
type JobUpdateRequest = components["schemas"]["JobUpdateRequest"];
type JobEnableRequest = components["schemas"]["JobEnableRequest"];
type JobIdRequest = components["schemas"]["JobIdRequest"];

type JobFormState = {
  projectId: string;
  targetKind: number | null;
  agentId: string;
  name: string;
  instructions: string;
  schedule: JobSchedule;
  enabled: boolean;
};

type ProjectDto = {
  id: string;
  name: string;
};

type JobDialogProps = {
  mode: "create" | "edit";
  open: boolean;
  onOpenChange: (open: boolean) => void;
  form: JobFormState;
  setForm: React.Dispatch<React.SetStateAction<JobFormState>>;
  projects: ProjectDto[];
  onSubmit: () => void;
  isSubmitting: boolean;
};

const jobsPath = "/api/jobs/list";
const jobPath = "/api/jobs/get";
const jobCreatePath = "/api/jobs/create";
const jobUpdatePath = "/api/jobs/update";
const jobEnablePath = "/api/jobs/enable";
const jobDeletePath = "/api/jobs/delete";
const DEFAULT_INTERVAL = "PT1M";
const DEFAULT_CRON = "*/1 * * * *";

function createDefaultOnceAt(): string {
  return new Date(Date.now() + 3 * 60 * 1000).toISOString();
}

function createDefaultSchedule(): JobSchedule {
  return { kind: SCHEDULE_KIND_CRON, cron: DEFAULT_CRON };
}

function createDefaultJobFormState(): JobFormState {
  return {
    projectId: "11111111-1111-1111-1111-000000000001",
    targetKind: null,
    agentId: "",
    name: "",
    instructions: "",
    schedule: createDefaultSchedule(),
    enabled: true,
  };
}

function createEditFormState(job: JobDto): JobFormState {
  return {
    projectId: job.projectId,
    targetKind: job.targetKind,
    agentId: job.agentId,
    name: job.name,
    instructions: job.instructions,
    schedule: {
      kind: job.schedule.kind,
      at: job.schedule.at,
      interval: job.schedule.interval,
      cron: job.schedule.cron,
    },
    enabled: job.enabled,
  };
}

function buildJobRequest(form: JobFormState): JobCreateRequest {
  const scheduleError = getScheduleError(form.schedule);
  if (scheduleError) {
    throw new Error(scheduleError);
  }

  const projectId = form.projectId.trim();
  const agentId = form.agentId.trim();
  if (!projectId) {
    throw new Error("Project is required.");
  }
  if (form.targetKind === null || !agentId) {
    throw new Error("Execution target is required.");
  }
  const instructions = form.instructions.trim();
  if (!instructions) {
    throw new Error("Instructions are required.");
  }

  return {
    projectId,
    targetKind: form.targetKind,
    agentId,
    name: form.name.trim(),
    instructions,
    schedule: form.schedule,
    enabled: form.enabled,
  };
}

function getTargetKindLabel(targetKind: number): string {
  switch (targetKind) {
    case 0:
      return "Agent";
    case 1:
      return "Agentflow";
    default:
      return `Unknown (${targetKind})`;
  }
}

function getScheduleValue(schedule: JobSchedule): string {
  switch (schedule.kind) {
    case SCHEDULE_KIND_ONCE:
      return schedule.at ? formatLocalDateTime(schedule.at) : "-";
    case SCHEDULE_KIND_INTERVAL:
      return schedule.interval ?? "-";
    case SCHEDULE_KIND_CRON:
      return schedule.cron ?? "-";
    default:
      return "-";
  }
}

export default function JobsPage({ onViewLogs }: { onViewLogs?: (jobId: string) => void }) {
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
  const [pendingEnabledJobIds, setPendingEnabledJobIds] = React.useState<Set<string>>(
    () => new Set(),
  );

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
    queryFn: async () => await apiGet("/api/agents"),
  });

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => await apiGet("/api/agentflows"),
  });

  const viewingJobId = viewingJob?.id ?? null;

  const jobDetailQuery = useQuery({
    queryKey: ["job", viewingJobId],
    enabled: Boolean(detailsOpen && viewingJobId),
    queryFn: async () =>
      (await apiGet(jobPath, {
        params: { query: { id: viewingJobId as string } },
      })) as JobDto,
  });

  const jobRunPage = useJobRunPage(viewingJobId, detailsOpen);
  const jobLogsQuery = jobRunPage.logsQuery;

  const createMutation = useMutation({
    mutationFn: async (body: JobCreateRequest) => {
      return await apiPost(jobCreatePath, { body });
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
    mutationFn: async (body: JobUpdateRequest) => {
      return await apiPost(jobUpdatePath, { body });
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

  const updateEnabledMutation = useMutation({
    mutationFn: async (body: JobEnableRequest) =>
      (await apiPost(jobEnablePath, { body })) as JobDto,
    onSuccess: (updatedJob) => {
      queryClient.setQueryData<JobDto[]>(["jobs"], (jobs) =>
        jobs?.map((job) => (job.id === updatedJob.id ? updatedJob : job)),
      );
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
    onSettled: (_data, _error, variables) => {
      setPendingEnabledJobIds((current) => {
        const next = new Set(current);
        next.delete(variables.id);
        return next;
      });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) => {
      const request: JobIdRequest = { id };
      return await apiPost(jobDeletePath, { body: request });
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

  const toggleJobEnabled = (job: JobDto, isEnabled: boolean) => {
    if (pendingEnabledJobIds.has(job.id)) return;

    setPendingEnabledJobIds((current) => new Set(current).add(job.id));
    updateEnabledMutation.mutate({ id: job.id, enabled: isEnabled });
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
      createMutation.mutate(buildJobRequest(createForm));
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  const submitEdit = () => {
    if (!editingJob) return;

    try {
      updateMutation.mutate({ ...buildJobRequest(editForm), id: editingJob.id });
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

          <Button onClick={() => setCreateOpen(true)}>Add Job</Button>
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
              <TableHead>Schedule</TableHead>
              <TableHead>Next Run</TableHead>
              <TableHead>Enabled</TableHead>
              <TableHead>Last Delivery</TableHead>
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
                  <div className="text-sm break-all" title={job.projectId}>
                    {projectsQuery.data?.find((project) => project.id === job.projectId)?.name ??
                      job.projectId}
                  </div>
                  <div className="text-xs break-all text-muted-foreground" title={job.agentId}>
                    {getTargetKindLabel(job.targetKind)}
                    {` · ${(job.targetKind === 1 ? agentflowsQuery.data : agentsQuery.data)?.find((agent) => agent.id === job.agentId)?.name ?? job.agentId}`}
                  </div>
                </TableCell>
                <TableCell className="min-w-52">
                  <div className="font-medium">{job.schedule.kind}</div>
                  <div className="font-mono text-xs break-all text-muted-foreground">
                    {getScheduleValue({
                      kind: job.schedule.kind,
                      at: job.schedule.at,
                      interval: job.schedule.interval,
                      cron: job.schedule.cron,
                    })}
                  </div>
                </TableCell>
                <TableCell className="min-w-44 text-sm text-muted-foreground">
                  <div>{getNextRunDisplay(job)}</div>
                </TableCell>
                <TableCell>
                  <Switch
                    className="cursor-pointer"
                    checked={job.enabled}
                    onCheckedChange={(isEnabled) => toggleJobEnabled(job, isEnabled)}
                    disabled={pendingEnabledJobIds.has(job.id)}
                    aria-label={`${job.name} enabled`}
                  />
                </TableCell>
                <TableCell className="min-w-52">
                  {job.lastDelivery ? (
                    <div className="flex flex-col items-start gap-2">
                      <Badge
                        variant={
                          job.lastDelivery.state === 3
                            ? "default"
                            : job.lastDelivery.state === 6
                              ? "destructive"
                              : "secondary"
                        }
                      >
                        {getDeliveryStateLabel(job.lastDelivery.state)}
                      </Badge>
                      <div className="text-xs text-muted-foreground">
                        Attempt {job.lastDelivery.attempt}
                      </div>
                      {job.lastDelivery.lastError ? (
                        <div className="line-clamp-2 text-xs text-destructive">
                          {job.lastDelivery.lastError}
                        </div>
                      ) : null}
                    </div>
                  ) : (
                    <span className="text-sm text-muted-foreground">-</span>
                  )}
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-2">
                    <ButtonGroup>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        asChild={!onViewLogs}
                        onClick={onViewLogs ? () => onViewLogs(job.id) : undefined}
                        aria-label={`View ${job.name} logs`}
                      >
                        {onViewLogs ? (
                          <ListChecks className="h-4 w-4" />
                        ) : (
                          <Link href={`/jobs/logs/?jobId=${encodeURIComponent(job.id)}`}>
                            <ListChecks className="h-4 w-4" />
                          </Link>
                        )}
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        onClick={() => openDetailsDialog(job)}
                      >
                        <Eye className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        onClick={() => openEditDialog(job)}
                      >
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        onClick={() => openDeleteDialog(job)}
                        disabled={deleteMutation.isPending}
                        className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </ButtonGroup>
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
        logs={jobRunPage.logs ?? []}
        logsLoading={jobLogsQuery.isPending}
        logsError={jobLogsQuery.isError ? getApiErrorMessage(jobLogsQuery.error) : null}
        logsOffset={jobRunPage.offset}
        logsHasNextPage={jobRunPage.hasNextPage}
        logsBusy={jobLogsQuery.isFetching}
        onPreviousLogs={jobRunPage.goToPreviousPage}
        onNextLogs={jobRunPage.goToNextPage}
        onEdit={(job) => {
          setDetailsOpen(false);
          openEditDialog(job);
        }}
      />
    </div>
  );
}

function getNextRunDisplay(job: JobDto): string {
  const state = getNextRunState(job);
  if (state === "ended") return "Ended";
  if (state === "scheduled" && job.dueAt) return formatLocalDateTime(job.dueAt);
  return "-";
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
  onSubmit,
  isSubmitting,
}: JobDialogProps) {
  const title = mode === "create" ? "Create Job" : "Edit Job";
  const submitLabel = mode === "create" ? "Create Job" : "Save Changes";
  const isOnceSchedule = form.schedule.kind === SCHEDULE_KIND_ONCE;
  const isIntervalSchedule = form.schedule.kind === SCHEDULE_KIND_INTERVAL;
  const isCronSchedule = form.schedule.kind === SCHEDULE_KIND_CRON;
  const scheduleError = getScheduleError(form.schedule);
  const targetError =
    form.targetKind === null || !form.agentId ? "Select an execution target." : null;
  const instructionsError = form.instructions.trim() ? null : "Instructions are required.";
  const [isAdvanced, setIsAdvanced] = React.useState(false);
  const [showValidation, setShowValidation] = React.useState(false);

  React.useEffect(() => {
    if (!open) {
      setIsAdvanced(false);
      setShowValidation(false);
    }
  }, [open]);

  React.useEffect(() => {
    setForm((current) => {
      if (current.schedule.kind === SCHEDULE_KIND_ONCE) {
        const at =
          current.schedule.at && !Number.isNaN(Date.parse(current.schedule.at))
            ? current.schedule.at
            : createDefaultOnceAt();
        if (current.schedule.at === at) {
          return current;
        }

        return {
          ...current,
          schedule: { kind: SCHEDULE_KIND_ONCE, at },
        };
      }

      if (current.schedule.kind === SCHEDULE_KIND_INTERVAL) {
        const interval = isPositiveIsoDuration(current.schedule.interval)
          ? current.schedule.interval
          : DEFAULT_INTERVAL;
        if (current.schedule.interval === interval) {
          return current;
        }

        return {
          ...current,
          schedule: { kind: SCHEDULE_KIND_INTERVAL, interval },
        };
      }

      if (current.schedule.kind === SCHEDULE_KIND_CRON) {
        const cron = isCronValue(current.schedule.cron) ? current.schedule.cron : DEFAULT_CRON;
        if (current.schedule.cron === cron) {
          return current;
        }

        return {
          ...current,
          schedule: { kind: SCHEDULE_KIND_CRON, cron },
        };
      }

      return { ...current, schedule: createDefaultSchedule() };
    });
  }, [form.schedule.kind, setForm]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="xl" className="flex max-h-[90vh] flex-col overflow-hidden">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            Configure the project, execution target, instructions, and schedule for this job.
          </DialogDescription>
        </DialogHeader>

        <div className="grid max-h-[65vh] grid-cols-1 gap-6 overflow-y-auto agw-scrollbar pr-1 sm:grid-cols-2">
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
            <Label htmlFor={`${mode}-agent-id`}>Execution Target</Label>
            <AgentSelector
              id={`${mode}-agent-id`}
              projectId={form.projectId}
              value={
                form.targetKind !== null && form.agentId
                  ? {
                      agentType: form.targetKind as 0 | 1,
                      agentId: form.agentId,
                    }
                  : null
              }
              onSelect={({ agentType, agentId }) =>
                setForm((current) => ({
                  ...current,
                  targetKind: agentType,
                  agentId,
                }))
              }
              onClear={() =>
                setForm((current) => ({
                  ...current,
                  targetKind: null,
                  agentId: "",
                }))
              }
              placeholder="Select an agent or agentflow"
              clearable
            />
            {showValidation && targetError ? (
              <p className="text-xs text-destructive">{targetError}</p>
            ) : null}
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-schedule-kind`}>Schedule Type</Label>
            <Select
              value={form.schedule.kind}
              onValueChange={(kind) =>
                setForm((current) => ({
                  ...current,
                  schedule: { kind },
                }))
              }
            >
              <SelectTrigger id={`${mode}-schedule-kind`} className="w-full">
                <SelectValue placeholder="Select schedule type" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={SCHEDULE_KIND_ONCE}>Once</SelectItem>
                <SelectItem value={SCHEDULE_KIND_INTERVAL}>Interval</SelectItem>
                <SelectItem value={SCHEDULE_KIND_CRON}>Cron</SelectItem>
              </SelectContent>
            </Select>
          </div>

          {isOnceSchedule ? (
            <div className="space-y-2">
              <Label htmlFor={`${mode}-schedule-at`}>Run Time</Label>
              <DateTimePicker
                id={`${mode}-schedule-at`}
                value={form.schedule.at ?? createDefaultOnceAt()}
                onChange={(at) =>
                  setForm((current) => ({
                    ...current,
                    schedule: { kind: SCHEDULE_KIND_ONCE, at },
                  }))
                }
              />
              <p className="text-xs text-muted-foreground">
                Pick the exact time to run this job once.
              </p>
            </div>
          ) : null}

          {isIntervalSchedule || isCronSchedule ? (
            <div className="space-y-2">
              <Label htmlFor={`${mode}-schedule-value`}>
                {isIntervalSchedule ? "Interval" : "Cron"}
              </Label>
              <Input
                id={`${mode}-schedule-value`}
                value={
                  isIntervalSchedule ? (form.schedule.interval ?? "") : (form.schedule.cron ?? "")
                }
                placeholder={isIntervalSchedule ? DEFAULT_INTERVAL : DEFAULT_CRON}
                aria-invalid={scheduleError !== null}
                onChange={(event) =>
                  setForm((current) => ({
                    ...current,
                    schedule: isIntervalSchedule
                      ? { kind: SCHEDULE_KIND_INTERVAL, interval: event.target.value }
                      : { kind: SCHEDULE_KIND_CRON, cron: event.target.value },
                  }))
                }
              />
              {scheduleError ? (
                <p className="text-xs text-destructive">{scheduleError}</p>
              ) : (
                <p className="text-xs text-muted-foreground">
                  {isIntervalSchedule
                    ? "Use a positive ISO 8601 duration such as PT1M."
                    : "Use a standard five-field cron expression such as */1 * * * *."}
                </p>
              )}
            </div>
          ) : null}

          {isAdvanced ? (
            <>
              <div className="space-y-2">
                <Label htmlFor={`${mode}-job-name`}>Job Name</Label>
                <Input
                  id={`${mode}-job-name`}
                  value={form.name}
                  placeholder="Generated automatically when blank"
                  onChange={(event) =>
                    setForm((current) => ({
                      ...current,
                      name: event.target.value,
                    }))
                  }
                />
              </div>

              <div className="space-y-2 sm:col-span-2">
                <Label htmlFor={`${mode}-enabled`}>Enabled</Label>
                <div className="flex min-h-9 items-center rounded-md border px-3">
                  <Switch
                    id={`${mode}-enabled`}
                    checked={form.enabled}
                    onCheckedChange={(checked) =>
                      setForm((current) => ({
                        ...current,
                        enabled: checked,
                      }))
                    }
                  />
                  <Label htmlFor={`${mode}-enabled`} className="ml-3 text-sm font-normal">
                    Allow this job to be picked up by the scheduler
                  </Label>
                </div>
              </div>
            </>
          ) : null}

          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-instructions`}>Instructions</Label>
            <p className="text-xs text-muted-foreground">
              Instructions passed to the selected target for each scheduled run.
            </p>
            <Textarea
              id={`${mode}-instructions`}
              rows={6}
              value={form.instructions}
              placeholder="Summarize the latest project activity and post the result to the team channel."
              aria-invalid={instructionsError !== null}
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  instructions: event.target.value,
                }))
              }
            />
            {showValidation && instructionsError ? (
              <p className="text-xs text-destructive">{instructionsError}</p>
            ) : null}
          </div>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => setIsAdvanced((current) => !current)}
            disabled={isSubmitting}
          >
            <Settings2 className="h-4 w-4" />
            {isAdvanced ? "Basic" : "Advanced"}
          </Button>

          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => onOpenChange(false)}
            disabled={isSubmitting}
          >
            Cancel
          </Button>
          <Button
            size="sm"
            type="button"
            onClick={() => {
              setShowValidation(true);
              onSubmit();
            }}
            disabled={isSubmitting || scheduleError !== null}
          >
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
  logsOffset,
  logsHasNextPage,
  logsBusy,
  onPreviousLogs,
  onNextLogs,
  onEdit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  viewingJob: JobDto | null;
  detailJob: JobDto | null | undefined;
  detailLoading: boolean;
  detailError: string | null;
  logs: JobRunDto[];
  logsLoading: boolean;
  logsError: string | null;
  logsOffset: number;
  logsHasNextPage: boolean;
  logsBusy: boolean;
  onPreviousLogs: () => void;
  onNextLogs: () => void;
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

        <div className="min-h-0 max-h-[65vh] flex-1 space-y-6 overflow-y-auto agw-scrollbar pr-1">
          {detailLoading && !job ? (
            <div className="text-sm text-muted-foreground">Loading details...</div>
          ) : detailError ? (
            <div className="text-sm text-destructive">Failed to load details: {detailError}</div>
          ) : job ? (
            <>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <DetailField label="Job ID" value={job.id} mono />
                <DetailField label="Project ID" value={job.projectId} mono />
                <DetailField label="Target Kind" value={getTargetKindLabel(job.targetKind)} />
                <DetailField label="Agent ID" value={job.agentId} mono />
                <DetailField label="Schedule Type" value={job.schedule.kind} />
                <DetailField
                  label="Schedule Value"
                  value={getScheduleValue({
                    kind: job.schedule.kind,
                    at: job.schedule.at,
                    interval: job.schedule.interval,
                    cron: job.schedule.cron,
                  })}
                  mono
                />
                <DetailField label="Next Run" value={getNextRunDisplay(job)} />
                <DetailField label="Enabled" value={job.enabled ? "Enabled" : "Disabled"} />
                <DetailField
                  label="Last Delivery"
                  value={
                    job.lastDelivery
                      ? `${getDeliveryStateLabel(job.lastDelivery.state)} · Attempt ${job.lastDelivery.attempt}`
                      : "-"
                  }
                />
              </div>

              <div className="space-y-2">
                <Label>Instructions</Label>
                <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm whitespace-pre-wrap break-words text-muted-foreground">
                  {job.instructions}
                </div>
              </div>

              {job.lastDelivery?.lastError ? (
                <div className="space-y-2">
                  <Label>Last Error</Label>
                  <div className="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm whitespace-pre-wrap break-words text-destructive">
                    {job.lastDelivery.lastError}
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
                            <JobRunResultBadge result={log.result} />
                            <span className="text-sm font-medium">Attempt {log.attempt}</span>
                          </div>
                          <div className="text-xs text-muted-foreground">
                            {formatLocalDateTime(log.startedAt)}
                            {` -> ${formatLocalDateTime(log.endedAt)}`}
                          </div>
                        </div>
                        {log.error ? (
                          <div className="mt-2 text-sm whitespace-pre-wrap break-words text-destructive">
                            {log.error}
                          </div>
                        ) : null}
                      </div>
                    ))}
                  </div>
                )}
                <JobRunPagination
                  offset={logsOffset}
                  hasNextPage={logsHasNextPage}
                  isLoading={logsBusy}
                  isError={Boolean(logsError)}
                  onPrevious={onPreviousLogs}
                  onNext={onNextLogs}
                />
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
