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

type AgentDto = {
  id: string
  name: string
  instructions: string
  systemPrompt: string
  modelProviderApiKeyId: string
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

type SelectedAgent = {
  agentId: string
  role: string
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

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agents")) as unknown as AgentDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [name, setName] = React.useState("")
  const [description, setDescription] = React.useState<string>("")
  const [enable, setEnable] = React.useState(true)
  const [pattern, setPattern] = React.useState<number>(0)
  const [configurationJson, setConfigurationJson] = React.useState<string>("")
  const [selectedAgents, setSelectedAgents] = React.useState<SelectedAgent[]>([])

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
      setSelectedAgents([])
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

            <DialogContent className="max-h-[calc(100vh-4rem)] overflow-hidden">
              <DialogHeader>
                <UiDialogTitle>Create workflow</UiDialogTitle>
                <UiDialogDescription>
                  Create a workflow. Bind agents now via a multi-select; binding{" "}
                  <code>order</code> equals the selection/add sequence.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid max-h-[calc(100vh-16rem)] gap-4 overflow-y-auto pr-2">
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

                <div className="grid gap-2">
                  <Label htmlFor="agents">Agents (multi-select)</Label>

                  {agentsQuery.isLoading ? (
                    <div className="text-sm text-muted-foreground">
                      Loading agents...
                    </div>
                  ) : agentsQuery.isError ? (
                    <div className="text-sm text-destructive">
                      Failed to load agents: {getApiErrorMessage(agentsQuery.error)}
                    </div>
                  ) : (agentsQuery.data?.length ?? 0) === 0 ? (
                    <div className="text-sm text-muted-foreground">
                      No agents found. Create an agent first.
                    </div>
                  ) : (
                    <select
                      id="agents"
                      multiple
                      value={selectedAgents.map((x) => x.agentId)}
                      onChange={(e) => {
                        const nextIds = Array.from(e.target.selectedOptions).map(
                          (o) => o.value
                        )

                        setSelectedAgents((current) => {
                          const currentIds = current.map((x) => x.agentId)
                          const removedIds = currentIds.filter(
                            (id) => !nextIds.includes(id)
                          )
                          const addedIds = nextIds.filter(
                            (id) => !currentIds.includes(id)
                          )

                          const kept = current.filter(
                            (x) => !removedIds.includes(x.agentId)
                          )
                          const added = addedIds.map((id) => ({
                            agentId: id,
                            role: "",
                          }))

                          return [...kept, ...added]
                        })
                      }}
                      className="border-input placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive dark:bg-input/30 w-full rounded-md border bg-transparent px-3 py-2 text-base shadow-xs transition-[color,box-shadow] outline-none focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50 md:text-sm"
                      size={Math.min(8, Math.max(4, agentsQuery.data?.length ?? 4))}
                    >
                      {(agentsQuery.data ?? []).map((agent) => (
                        <option key={agent.id} value={agent.id}>
                          {agent.name}
                        </option>
                      ))}
                    </select>
                  )}

                  <div className="text-xs text-muted-foreground">
                    Order is assigned by selection/add sequence (0..n-1). For{" "}
                    <code>Sequential</code> (1) pattern, at least one agent is
                    required.
                  </div>

                  {selectedAgents.length > 0 && (
                    <div className="space-y-3 rounded-md border p-3">
                      <div className="text-sm font-medium">Selected agents</div>

                      <div className="space-y-2">
                        {selectedAgents.map((item, index) => {
                          const agentName =
                            agentsQuery.data?.find((a) => a.id === item.agentId)
                              ?.name ?? item.agentId

                          return (
                            <div
                              key={item.agentId}
                              className="flex flex-col gap-2 sm:flex-row sm:items-center"
                            >
                              <div className="min-w-0 text-sm sm:w-64">
                                <span className="mr-2 font-mono text-xs text-muted-foreground">
                                  #{index}
                                </span>
                                <span className="truncate">{agentName}</span>
                              </div>

                              <div className="flex flex-1 items-center gap-2">
                                <Input
                                  value={item.role}
                                  onChange={(e) =>
                                    setSelectedAgents((current) =>
                                      current.map((x) =>
                                        x.agentId === item.agentId
                                          ? { ...x, role: e.target.value }
                                          : x
                                      )
                                    )
                                  }
                                  placeholder="Role (optional)"
                                />
                                <Button
                                  type="button"
                                  variant="outline"
                                  size="icon-sm"
                                  onClick={() =>
                                    setSelectedAgents((current) =>
                                      current.filter((x) => x.agentId !== item.agentId)
                                    )
                                  }
                                  aria-label={`Remove ${agentName}`}
                                  title="Remove"
                                >
                                  ×
                                </Button>
                              </div>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  )}
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
                      agents: selectedAgents.map((x, index) => ({
                        agentId: x.agentId,
                        order: index,
                        role: x.role.trim().length ? x.role.trim() : null,
                      })),
                    })
                  }
                  disabled={
                    !name.trim() ||
                    createWorkflowMutation.isPending ||
                    (pattern === 1 && selectedAgents.length === 0)
                  }
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