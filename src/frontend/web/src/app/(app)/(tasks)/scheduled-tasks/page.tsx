"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { ApiError, apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

type ScheduledTaskDto = {
  id: string;
  projectId: string;
  agentType: number | null;
  agentId: string | null;
  name: string;
  prompt: string | null;
  triggerType: number;
  triggerValue: string;
  timeZoneId: string;
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

type ScheduledTaskRequest = {
  projectId: string;
  agentType: number | null;
  agentId: string | null;
  name: string;
  prompt: string | null;
  triggerType: number;
  triggerValue: string;
  timeZoneId: string;
  nextRunTime: string;
  maxRetryCount: number;
  isEnabled: boolean;
  status?: number;
};

const taskPath = "/api/scheduled-tasks" as never;
const taskItemPath = "/api/scheduled-tasks/{id}" as never;
const taskLogsPath = "/api/scheduled-tasks/{id}/logs" as never;

function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return `${error.status} ${error.statusText}`;
  }

  if (error instanceof Error) {
    return error.message;
  }

  return "Unknown error";
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

function fromLocalInput(value: string): string {
  return new Date(value).toISOString();
}

export default function ScheduledTasksPage() {
  const queryClient = useQueryClient();

  const [selectedTaskId, setSelectedTaskId] = React.useState<string | null>(null);
  const [editingTask, setEditingTask] = React.useState<ScheduledTaskDto | null>(null);

  const [form, setForm] = React.useState<ScheduledTaskRequest>({
    projectId: "",
    agentType: 0,
    agentId: null,
    name: "",
    prompt: null,
    triggerType: 3,
    triggerValue: "*/1 * * * *",
    timeZoneId: "Asia/Shanghai",
    nextRunTime: new Date().toISOString(),
    maxRetryCount: 3,
    isEnabled: true,
  });

  const tasksQuery = useQuery({
    queryKey: ["scheduled-tasks"],
    queryFn: async () => (await apiGet(taskPath)) as ScheduledTaskDto[],
  });

  const taskDetailQuery = useQuery({
    queryKey: ["scheduled-task", selectedTaskId],
    enabled: Boolean(selectedTaskId),
    queryFn: async () =>
      (await apiGet(taskItemPath, {
        params: { path: { id: selectedTaskId as string } },
      } as never)) as ScheduledTaskDto,
  });

  const taskLogsQuery = useQuery({
    queryKey: ["scheduled-task-logs", selectedTaskId],
    enabled: Boolean(selectedTaskId),
    queryFn: async () =>
      (await apiGet(taskLogsPath, {
        params: { path: { id: selectedTaskId as string } },
      } as never)) as TaskExecutionLogDto[],
  });

  const createMutation = useMutation({
    mutationFn: async (payload: ScheduledTaskRequest) => apiPost(taskPath, { body: payload } as never),
    onSuccess: async () => {
      toast.success("ScheduledTask 创建成功");
      await queryClient.invalidateQueries({ queryKey: ["scheduled-tasks"] });
    },
    onError: (error) => toast.error(`创建失败: ${errorMessage(error)}`),
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, payload }: { id: string; payload: ScheduledTaskRequest }) =>
      apiPut(taskItemPath, {
        params: { path: { id } },
        body: payload,
      } as never),
    onSuccess: async () => {
      toast.success("ScheduledTask 更新成功");
      setEditingTask(null);
      await queryClient.invalidateQueries({ queryKey: ["scheduled-tasks"] });
      await queryClient.invalidateQueries({ queryKey: ["scheduled-task", selectedTaskId] });
    },
    onError: (error) => toast.error(`更新失败: ${errorMessage(error)}`),
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) =>
      apiDelete(taskItemPath, {
        params: { path: { id } },
      } as never),
    onSuccess: async () => {
      toast.success("ScheduledTask 已删除");
      setSelectedTaskId(null);
      await queryClient.invalidateQueries({ queryKey: ["scheduled-tasks"] });
    },
    onError: (error) => toast.error(`删除失败: ${errorMessage(error)}`),
  });

  const tasks = tasksQuery.data ?? [];

  const applyTaskToForm = React.useCallback((task: ScheduledTaskDto) => {
    setForm({
      projectId: task.projectId,
      agentType: task.agentType,
      agentId: task.agentId,
      name: task.name,
      prompt: task.prompt,
      triggerType: task.triggerType,
      triggerValue: task.triggerValue,
      timeZoneId: task.timeZoneId,
      nextRunTime: task.nextRunTime,
      maxRetryCount: task.maxRetryCount,
      isEnabled: task.isEnabled,
      status: task.status,
    });
  }, []);

  const submit = () => {
    const payload: ScheduledTaskRequest = {
      ...form,
      name: form.name.trim(),
      projectId: form.projectId.trim(),
      agentId: form.agentId?.trim() ? form.agentId.trim() : null,
      prompt: form.prompt?.trim() ? form.prompt.trim() : null,
      nextRunTime: form.nextRunTime,
    };

    if (!payload.name || !payload.projectId || !payload.nextRunTime) {
      toast.error("名称、项目ID、NextRunTime 不能为空");
      return;
    }

    if (editingTask) {
      updateMutation.mutate({ id: editingTask.id, payload });
      return;
    }

    createMutation.mutate(payload);
  };

  return (
    <div className="w-full space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">Scheduled Tasks</h1>
          <p className="text-sm text-muted-foreground">支持 list / detail / create / update / delete / logs。</p>
        </div>
        <Button variant="outline" onClick={() => tasksQuery.refetch()}>
          Refresh
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{editingTask ? "编辑 ScheduledTask" : "创建 ScheduledTask"}</CardTitle>
          <CardDescription>TriggerType: 1=Once, 2=Interval, 3=Cron</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3 sm:grid-cols-2">
          <div className="grid gap-2">
            <Label>名称</Label>
            <Input value={form.name} onChange={(e) => setForm((s) => ({ ...s, name: e.target.value }))} />
          </div>
          <div className="grid gap-2">
            <Label>ProjectId</Label>
            <Input value={form.projectId} onChange={(e) => setForm((s) => ({ ...s, projectId: e.target.value }))} />
          </div>
          <div className="grid gap-2">
            <Label>AgentType</Label>
            <Input
              type="number"
              value={form.agentType ?? ""}
              onChange={(e) => setForm((s) => ({ ...s, agentType: e.target.value ? Number(e.target.value) : null }))}
            />
          </div>
          <div className="grid gap-2">
            <Label>AgentId</Label>
            <Input value={form.agentId ?? ""} onChange={(e) => setForm((s) => ({ ...s, agentId: e.target.value }))} />
          </div>
          <div className="grid gap-2">
            <Label>TriggerType</Label>
            <Input
              type="number"
              value={form.triggerType}
              onChange={(e) => setForm((s) => ({ ...s, triggerType: Number(e.target.value) }))}
            />
          </div>
          <div className="grid gap-2">
            <Label>TriggerValue</Label>
            <Input
              value={form.triggerValue}
              onChange={(e) => setForm((s) => ({ ...s, triggerValue: e.target.value }))}
            />
          </div>
          <div className="grid gap-2">
            <Label>TimeZoneId</Label>
            <Input
              value={form.timeZoneId}
              onChange={(e) => setForm((s) => ({ ...s, timeZoneId: e.target.value }))}
            />
          </div>
          <div className="grid gap-2">
            <Label>NextRunTime</Label>
            <Input
              type="datetime-local"
              value={toLocalInput(form.nextRunTime)}
              onChange={(e) => setForm((s) => ({ ...s, nextRunTime: fromLocalInput(e.target.value) }))}
            />
          </div>
          <div className="grid gap-2">
            <Label>MaxRetryCount</Label>
            <Input
              type="number"
              value={form.maxRetryCount}
              onChange={(e) => setForm((s) => ({ ...s, maxRetryCount: Number(e.target.value) }))}
            />
          </div>
          {editingTask ? (
            <div className="grid gap-2">
              <Label>Status</Label>
              <Input
                type="number"
                value={form.status ?? 1}
                onChange={(e) => setForm((s) => ({ ...s, status: Number(e.target.value) }))}
              />
            </div>
          ) : null}
          <div className="grid gap-2 sm:col-span-2">
            <Label>Prompt</Label>
            <Textarea
              value={form.prompt ?? ""}
              onChange={(e) => setForm((s) => ({ ...s, prompt: e.target.value }))}
              rows={4}
            />
          </div>
          <label className="text-sm sm:col-span-2">
            <input
              type="checkbox"
              checked={form.isEnabled}
              onChange={(e) => setForm((s) => ({ ...s, isEnabled: e.target.checked }))}
            />{" "}
            IsEnabled
          </label>
          <div className="sm:col-span-2 flex gap-2">
            <Button onClick={submit} disabled={createMutation.isPending || updateMutation.isPending}>
              {editingTask ? "保存" : "创建"}
            </Button>
            {editingTask ? (
              <Button
                variant="outline"
                onClick={() => {
                  setEditingTask(null);
                  setForm({
                    projectId: "",
                    agentType: 0,
                    agentId: null,
                    name: "",
                    prompt: null,
                    triggerType: 3,
                    triggerValue: "*/1 * * * *",
                    timeZoneId: "Asia/Shanghai",
                    nextRunTime: new Date().toISOString(),
                    maxRetryCount: 3,
                    isEnabled: true,
                  });
                }}
              >
                取消编辑
              </Button>
            ) : null}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>ScheduledTask 列表</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          {tasksQuery.isLoading ? <div className="text-sm text-muted-foreground">Loading...</div> : null}
          {tasks.map((task) => (
            <div key={task.id} className="rounded border p-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="font-medium">{task.name}</div>
                <div className="flex gap-2">
                  <Button size="sm" variant="outline" onClick={() => setSelectedTaskId(task.id)}>
                    详情
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => {
                      setEditingTask(task);
                      applyTaskToForm(task);
                    }}
                  >
                    编辑
                  </Button>
                  <Button size="sm" variant="destructive" onClick={() => deleteMutation.mutate(task.id)}>
                    删除
                  </Button>
                </div>
              </div>
              <div className="mt-2 text-xs text-muted-foreground">
                ID: {task.id} · NextRun: {new Date(task.nextRunTime).toLocaleString()} · Retry: {task.retryCount}/{task.maxRetryCount}
              </div>
            </div>
          ))}
          {!tasksQuery.isLoading && tasks.length === 0 ? (
            <div className="text-sm text-muted-foreground">暂无 ScheduledTask。</div>
          ) : null}
        </CardContent>
      </Card>

      {selectedTaskId ? (
        <Card>
          <CardHeader>
            <CardTitle>ScheduledTask 详情 + TaskExecutionLog</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {taskDetailQuery.data ? (
              <pre className="overflow-auto rounded bg-muted p-3 text-xs">
                {JSON.stringify(taskDetailQuery.data, null, 2)}
              </pre>
            ) : (
              <div className="text-sm text-muted-foreground">加载详情中...</div>
            )}

            <div>
              <div className="mb-2 text-sm font-medium">Execution Logs</div>
              <div className="space-y-2">
                {(taskLogsQuery.data ?? []).map((log) => (
                  <div key={log.id} className="rounded border p-2 text-xs">
                    <div>
                      {log.success ? "✅" : "❌"} Attempt #{log.attempt} · {new Date(log.startTime).toLocaleString()}
                    </div>
                    {log.errorMessage ? (
                      <div className="text-destructive">{log.errorMessage}</div>
                    ) : null}
                  </div>
                ))}
                {taskLogsQuery.isLoading ? (
                  <div className="text-sm text-muted-foreground">日志加载中...</div>
                ) : null}
                {!taskLogsQuery.isLoading && (taskLogsQuery.data?.length ?? 0) === 0 ? (
                  <div className="text-sm text-muted-foreground">暂无日志。</div>
                ) : null}
              </div>
            </div>
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}
