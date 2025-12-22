"use client"

import * as React from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"

import { ApiError, apiGet, apiPost, apiPut, apiDelete } from "@/api/client"
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
import { Pencil, Trash2 } from "lucide-react"

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

  const [visualOpen, setVisualOpen] = React.useState(false)
  const [editingWorkflow, setEditingWorkflow] = React.useState<{
    id: string
    name: string
    description: string | null
    pattern: number
    configurationJson: string | null
    enable: boolean
    nodes: any[]
    edges: any[]
  } | null>(null)

  const updateWorkflowMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: any }) => {
      return await apiPut(`/api/workflows/${id}`, { body })
    },
    onSuccess: async () => {
      toast.success("Workflow updated")
      await queryClient.invalidateQueries({ queryKey: ["workflows"] })
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`)
    },
  })

  const deleteWorkflowMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete(`/api/workflows/${id}`)
    },
    onSuccess: async () => {
      toast.success("Workflow deleted")
      await queryClient.invalidateQueries({ queryKey: ["workflows"] })
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`)
    },
  })

  const handleToggleEnabled = React.useCallback(
    async (workflow: WorkflowDto) => {
      try {
        // We need to get full workflow data including nodes and edges
        const nodes = (await apiGet(`/api/workflows/${workflow.id}/nodes`)) as any[]
        const edges = (await apiGet(`/api/workflows/${workflow.id}/edges`)) as any[]

        updateWorkflowMutation.mutate({
          id: workflow.id,
          body: {
            name: workflow.name,
            description: workflow.description,
            pattern: workflow.pattern,
            configurationJson: workflow.configurationJson,
            enable: !workflow.enable,
            nodes: nodes || [],
            edges: edges || [],
          },
        })
      } catch (error) {
        toast.error("Failed to fetch workflow details")
      }
    },
    [updateWorkflowMutation]
  )

  const handleDelete = React.useCallback(
    (workflow: WorkflowDto) => {
      if (window.confirm(`Are you sure you want to delete "${workflow.name}"?`)) {
        deleteWorkflowMutation.mutate(workflow.id)
      }
    },
    [deleteWorkflowMutation]
  )

  const handleEdit = React.useCallback(
    async (workflow: WorkflowDto) => {
      try {
        // Fetch workflow nodes and edges
        const nodes = (await apiGet(`/api/workflows/${workflow.id}/nodes`)) as any[]
        const edges = (await apiGet(`/api/workflows/${workflow.id}/edges`)) as any[]

        // Set editing workflow data
        setEditingWorkflow({
          id: workflow.id,
          name: workflow.name,
          description: workflow.description,
          pattern: workflow.pattern,
          configurationJson: workflow.configurationJson,
          enable: workflow.enable,
          nodes: nodes || [],
          edges: edges || [],
        })

        // Open visual builder
        setVisualOpen(true)
      } catch (error) {
        toast.error("Failed to load workflow details")
        console.error("Failed to load workflow:", error)
      }
    },
    []
  )

  const handleWorkflowCreated = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ["workflows"] })
    setVisualOpen(false)
    setEditingWorkflow(null)
  }, [queryClient])

  const handleVisualDialogClose = React.useCallback(() => {
    setVisualOpen(false)
    setEditingWorkflow(null)
  }, [])


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
            className="cursor-pointer"
            onClick={() => workflowsQuery.refetch()}
            disabled={workflowsQuery.isFetching}
          >
            Refresh
          </Button>

          <VisualWorkflowDialog
            open={visualOpen}
            onOpenChange={handleVisualDialogClose}
            agents={agentsQuery.data || []}
            workflows={workflowsQuery.data || []}
            editingWorkflow={editingWorkflow}
            onWorkflowCreated={handleWorkflowCreated}
          />

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
                  <TableHead>Created</TableHead>
                  <TableHead className="text-center">Enabled</TableHead>
                  <TableHead className="text-center">Actions</TableHead>
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
                    <TableCell className="text-xs text-muted-foreground">
                      {workflow.createTime
                        ? new Date(workflow.createTime).toLocaleString()
                        : "-"}
                    </TableCell>
                    <TableCell className="text-center">
                      <label className="relative inline-flex items-center cursor-pointer">
                        <input
                          type="checkbox"
                          checked={workflow.enable}
                          onChange={() => handleToggleEnabled(workflow)}
                          disabled={updateWorkflowMutation.isPending}
                          className="sr-only peer"
                        />
                        <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full rtl:peer-checked:after:-translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:start-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-green-600"></div>
                      </label>
                    </TableCell>
                    <TableCell>
                      <div className="flex items-center justify-center gap-2">
                        <Button
                          variant="ghost"
                          size="icon-sm"
                          onClick={() => handleEdit(workflow)}
                          title="Edit workflow"
                        >
                          <Pencil className="w-4 h-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon-sm"
                          onClick={() => handleDelete(workflow)}
                          disabled={deleteWorkflowMutation.isPending}
                          title="Delete workflow"
                          className="text-destructive hover:text-destructive hover:bg-destructive/10"
                        >
                          <Trash2 className="w-4 h-4" />
                        </Button>
                      </div>
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