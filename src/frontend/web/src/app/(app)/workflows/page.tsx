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
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Textarea } from "@/components/ui/textarea"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { VisualWorkflowDialog } from "./components/visual-workflow-dialog"
import { AgentDto, WorkflowDto } from "@/types/workflow";

type WorkflowCreateRequest = components["schemas"]["WorkflowCreateRequest"]

type SelectedAgent = {
  agentId: string
  role: string
}

function getPatternName(pattern: number): string {
  switch (pattern) {
    case 0:
      return "Concurrent"
    case 1:
      return "Sequential"
    case 2:
      return "GroupChat"
    case 3:
      return "Handoff"
    case 4:
      return "Magentic"
    default:
      return `Unknown (${pattern})`
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
  const [visualOpen, setVisualOpen] = React.useState(false)
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
      setVisualOpen(false)
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

  const handleVisualBuild = React.useCallback(
    (visualData: {
      agents: { agentId: string; order: number; role: string | null }[]
      pattern: number
    }) => {
      setPattern(visualData.pattern)
      setSelectedAgents(
        visualData.agents.map((a) => ({
          agentId: a.agentId,
          role: a.role || "",
        }))
      )
      setVisualOpen(false)
      setCreateOpen(true)
    },
    []
  )

  return (
    <div className="space-y-6 w-full">
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

          <VisualWorkflowDialog
            open={visualOpen}
            onOpenChange={setVisualOpen}
            agents={agentsQuery.data || []}
            workflows={workflowsQuery.data || []}
            onBuild={handleVisualBuild}
          />

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
                  <Label htmlFor="pattern">Orchestration Pattern</Label>
                  <Select
                    value={String(pattern)}
                    onValueChange={(value) => {
                      setPattern(Number(value))
                      // Reset configuration when pattern changes
                      setConfigurationJson("")
                    }}
                  >
                    <SelectTrigger id="pattern">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="0">Concurrent (0)</SelectItem>
                      <SelectItem value="1">Sequential (1)</SelectItem>
                      <SelectItem value="2">GroupChat (2)</SelectItem>
                      <SelectItem value="3">Handoff (3)</SelectItem>
                      <SelectItem value="4">Magentic (4)</SelectItem>
                    </SelectContent>
                  </Select>
                  <div className="text-xs text-muted-foreground">
                    Choose the workflow orchestration pattern
                  </div>
                </div>

                {/* Pattern-specific configuration */}
                {pattern === 2 && (
                  <div className="grid gap-2 rounded-md border p-3">
                    <div className="text-sm font-medium">GroupChat Configuration</div>
                    <div className="grid gap-2">
                      <Label htmlFor="maxIterations">Max Iterations</Label>
                      <Input
                        id="maxIterations"
                        type="number"
                        min="1"
                        defaultValue="10"
                        onChange={(e) => {
                          const maxIterations = parseInt(e.target.value) || 10
                          setConfigurationJson(
                            JSON.stringify({ maxIterations }, null, 2)
                          )
                        }}
                      />
                      <div className="text-xs text-muted-foreground">
                        Maximum number of conversation rounds (default: 10)
                      </div>
                    </div>
                  </div>
                )}

                {pattern === 3 && (
                  <div className="grid gap-2 rounded-md border p-3">
                    <div className="text-sm font-medium">Handoff Configuration</div>
                    <div className="grid gap-2">
                      <Label htmlFor="maxHandoffs">Max Handoffs</Label>
                      <Input
                        id="maxHandoffs"
                        type="number"
                        min="1"
                        defaultValue="5"
                        onChange={(e) => {
                          const maxHandoffs = parseInt(e.target.value) || 5
                          setConfigurationJson(
                            JSON.stringify({ maxHandoffs }, null, 2)
                          )
                        }}
                      />
                      <div className="text-xs text-muted-foreground">
                        Maximum number of agent handoffs (default: 5)
                      </div>
                    </div>
                  </div>
                )}

                {pattern === 4 && (
                  <div className="grid gap-2 rounded-md border p-3">
                    <div className="text-sm font-medium">Magentic Configuration</div>
                    <div className="grid gap-4">
                      <div className="grid gap-2">
                        <Label htmlFor="maxRounds">Max Rounds</Label>
                        <Input
                          id="maxRounds"
                          type="number"
                          min="1"
                          defaultValue="10"
                          onChange={(e) => {
                            const maxRounds = parseInt(e.target.value) || 10
                            const config = configurationJson
                              ? JSON.parse(configurationJson)
                              : {}
                            setConfigurationJson(
                              JSON.stringify({ ...config, maxRounds }, null, 2)
                            )
                          }}
                        />
                        <div className="text-xs text-muted-foreground">
                          Maximum collaboration rounds (default: 10)
                        </div>
                      </div>

                      <div className="grid gap-2">
                        <Label htmlFor="maxStallCount">Max Stall Count</Label>
                        <Input
                          id="maxStallCount"
                          type="number"
                          min="1"
                          defaultValue="3"
                          onChange={(e) => {
                            const maxStallCount = parseInt(e.target.value) || 3
                            const config = configurationJson
                              ? JSON.parse(configurationJson)
                              : {}
                            setConfigurationJson(
                              JSON.stringify({ ...config, maxStallCount }, null, 2)
                            )
                          }}
                        />
                        <div className="text-xs text-muted-foreground">
                          Rounds without progress before orchestrator intervention
                          (default: 3)
                        </div>
                      </div>
                    </div>
                  </div>
                )}

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
                    <Select
                      onValueChange={(agentId) => {
                        setSelectedAgents((current) => {
                          if (current.some((x) => x.agentId === agentId)) return current
                          return [...current, { agentId, role: "" }]
                        })
                      }}
                    >
                      <SelectTrigger id="agents">
                        <SelectValue placeholder="Select an agent to add" />
                      </SelectTrigger>
                      <SelectContent>
                        {(agentsQuery.data ?? []).map((agent) => (
                          <SelectItem
                            key={agent.id}
                            value={agent.id}
                            disabled={selectedAgents.some((x) => x.agentId === agent.id)}
                          >
                            {agent.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
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
          ) : workflowsQuery.data && workflowsQuery.data.length > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Description</TableHead>
                  <TableHead>Pattern</TableHead>
                  <TableHead>Enabled</TableHead>
                  <TableHead>Created</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {workflowsQuery.data.map((workflow) => (
                  <TableRow key={workflow.id}>
                    <TableCell className="font-medium">{workflow.name}</TableCell>
                    <TableCell className="max-w-xs truncate">
                      {workflow.description || "-"}
                    </TableCell>
                    <TableCell>{getPatternName(workflow.pattern)}</TableCell>
                    <TableCell>
                      {workflow.enable ? (
                        <span className="text-green-600">Yes</span>
                      ) : (
                        <span className="text-muted-foreground">No</span>
                      )}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {workflow.createTime
                        ? new Date(workflow.createTime).toLocaleString()
                        : "-"}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <div className="text-sm text-muted-foreground">
              No workflows found. Create one to get started.
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}