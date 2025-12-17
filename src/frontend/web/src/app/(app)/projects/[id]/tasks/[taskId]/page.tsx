"use client"

import * as React from "react"
import Link from "next/link"
import { useParams } from "next/navigation"
import { useQuery } from "@tanstack/react-query"

import { apiGet } from "@/api/client"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Label } from "@/components/ui/label"
import { Textarea } from "@/components/ui/textarea"
import { ButtonGroup } from "@/components/ui/button-group"

type ProjectTaskDto = {
  id: string
  projectId: string
  workflowId: string
  status: number
  description: string
  input: string
  outputJson?: string | null
  errorMessage?: string | null
  createTime?: string | null
  updateTime?: string | null
  startedTime?: string | null
  finishedTime?: string | null
}

function formatDate(value?: string | null): string {
  if (!value) return "-"
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return value
  return d.toLocaleString()
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return "Unknown error"
}

function statusLabel(status: number): string {
  switch (status) {
    case 0:
      return "Pending"
    case 1:
      return "Running"
    case 2:
      return "Succeeded"
    case 3:
      return "Failed"
    case 4:
      return "Canceled"
    default:
      return `#${status}`
  }
}

function statusClassName(status: number): string {
  switch (status) {
    case 2:
      return "bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"
    case 1:
      return "bg-blue-500/10 text-blue-700 dark:text-blue-300"
    case 0:
      return "bg-muted text-muted-foreground"
    case 4:
      return "bg-muted text-muted-foreground"
    case 3:
      return "bg-destructive/10 text-destructive"
    default:
      return "bg-muted text-muted-foreground"
  }
}

export default function TaskDetailsPage() {
  const params = useParams<{ id: string; taskId: string }>()
  const projectId = params.id
  const taskId = params.taskId

  const taskQuery = useQuery({
    queryKey: ["projects", projectId, "tasks", taskId],
    queryFn: async () => {
      return (await apiGet("/api/projects/{projectId}/tasks/{taskId}", {
        params: { path: { projectId, taskId } },
      })) as unknown as ProjectTaskDto
    },
  })

  const task = taskQuery.data

  return (
    <div className="space-y-6">
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
          <div className="flex gap-3 justify-between">
            <div>
              <span className="font-medium text-muted-foreground mr-2 flex-wrap">
                Created:
              </span>
              <span>{formatDate(task.createTime)}</span>
            </div>
            {/* <div className="grid grid-cols-[120px_1fr] items-center gap-2">
                <span className="font-medium text-muted-foreground">Updated:</span>
                <span>{formatDate(task.updateTime)}</span>
              </div> */}
            <div>
              <span className="font-medium text-muted-foreground mr-2">
                Started:
              </span>
              <span>
                {task.startedTime ? formatDate(task.startedTime) : "-"}
              </span>
            </div>
            <div>
              <span className="font-medium text-muted-foreground mr-2">
                Finished:
              </span>
              <span>
                {task.finishedTime ? formatDate(task.finishedTime) : "-"}
              </span>
            </div>
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Input</CardTitle>
            </CardHeader>
            <CardContent>
              <Textarea
                value={task.input}
                readOnly
                rows={8}
                className="font-mono text-xs"
              />
            </CardContent>
          </Card>

          {task.outputJson ? (
            <Card>
              <CardHeader>
                <CardTitle>Output</CardTitle>
                <CardDescription>
                  Task execution output (JSON format).
                </CardDescription>
              </CardHeader>
              <CardContent>
                <Textarea
                  value={task.outputJson}
                  readOnly
                  rows={12}
                  className="font-mono text-xs"
                />
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
