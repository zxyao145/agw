"use client"

import * as React from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"

import { ApiError, apiGet, apiPost } from "@/api/client"
import type { components } from "@/api/openapi"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle as UiDialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Textarea } from "@/components/ui/textarea"

type WorkflowCreateRequest = components["schemas"]["WorkflowCreateRequest"]

type WorkflowDto = {
  id: string
  name: string
  description: string | null
  pattern: number
  configurationJson: string | null
  enable: boolean
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

function pretty(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value)
  }
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body
    }
    return `${error.status} ${error.statusText}`
  }
  if (error instanceof Error) return error.message
  return "Unknown error"
}

export default function WorkflowsPage() {
  const queryClient = useQueryClient()

  const workflowsQuery = useQuery({
    queryKey: ["workflows"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/workflows")) as unknown as WorkflowDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [name, setName] = React.useState("")
  const [description, setDescription] = React.useState<string>("")
  const [enable, setEnable] = React.useState(true)
  const [pattern, setPattern] = React.useState<number>(0)
  const [configurationJson, setConfigurationJson] = React.useState<string>("")

  const createWorkflowMutation = useMutation({
    mutationFn: async (body: WorkflowCreateRequest) => {
      return await apiPost("/api/workflows", { body })
    },
    onSuccess: async () => {
      toast.success("Workflow created")
      setCreateOpen(false)
      setName("")
      setDescription("")
      setEnable(true)
      setPattern(0)
      setConfigurationJson("")
      await queryClient.invalidateQueries({ queryKey: ["workflows"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Workflows</h1>
          <p className="text-sm text-muted-foreground">
            Manage workflows and execute them.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => workflowsQuery.refetch()}
            disabled={workflowsQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create workflow</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create workflow</UiDialogTitle>
                <UiDialogDescription>
                  Create a workflow. Agents binding can be configured later (for
                  now uses an empty list).
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="name">Name</Label>
                  <Input
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="demo-workflow"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="description">Description</Label>
                  <Textarea
                    id="description"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    placeholder="Workflow description"
                    rows={3}
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="pattern">Pattern (number)</Label>
                  <Input
                    id="pattern"
                    inputMode="numeric"
                    value={String(pattern)}
                    onChange={(e) => setPattern(Number(e.target.value || "0"))}
                  />
                  <div className="text-xs text-muted-foreground">
                    Maps to backend enum
                    <code className="mx-1">WorkflowOrchestrationPattern</code>.
                  </div>
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="configurationJson">Configuration JSON</Label>
                  <Textarea
                    id="configurationJson"
                    value={configurationJson}
                    onChange={(e) => setConfigurationJson(e.target.value)}
                    placeholder='{"key":"value"}'
                    rows={4}
                  />
                </div>

                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={enable}
                    onChange={(e) => setEnable(e.target.checked)}
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
                    createWorkflowMutation.mutate({
                      name,
                      description: description.length ? description : null,
                      pattern,
                      configurationJson: configurationJson.length
                        ? configurationJson
                        : null,
                      enable,
                      agents: [],
                    })
                  }
                  disabled={!name.trim() || createWorkflowMutation.isPending}
                >
                  {createWorkflowMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Workflows</CardTitle>
          <CardDescription>
            Fetched from <code>/api/workflows</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {workflowsQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : workflowsQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load workflows:{" "}
              {getApiErrorMessage(workflowsQuery.error)}
            </div>
          ) : (
            <pre className="max-h-[520px] overflow-auto rounded-md border bg-muted/30 p-3 text-xs">
              {pretty(workflowsQuery.data)}
            </pre>
          )}
        </CardContent>
      </Card>
    </div>
  )
}