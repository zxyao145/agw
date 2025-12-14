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

type AgentCreateRequest = components["schemas"]["AgentCreateRequest"]

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

export default function AgentsPage() {
  const queryClient = useQueryClient()

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agents")) as unknown as AgentDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [name, setName] = React.useState("")
  const [instructions, setInstructions] = React.useState("")
  const [systemPrompt, setSystemPrompt] = React.useState("")
  const [modelProviderApiKeyId, setModelProviderApiKeyId] = React.useState("")

  const createAgentMutation = useMutation({
    mutationFn: async (body: AgentCreateRequest) => {
      return await apiPost("/api/agents", { body })
    },
    onSuccess: async () => {
      toast.success("Agent created")
      setCreateOpen(false)
      setName("")
      setInstructions("")
      setSystemPrompt("")
      setModelProviderApiKeyId("")
      await queryClient.invalidateQueries({ queryKey: ["agents"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Agents</h1>
          <p className="text-sm text-muted-foreground">
            Manage agents. Creating an agent requires a Model Provider API Key.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => agentsQuery.refetch()}
            disabled={agentsQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create agent</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create agent</UiDialogTitle>
                <UiDialogDescription>
                  Uses <code>/api/agents</code>. Provide{" "}
                  <code>modelProviderApiKeyId</code> as UUID for now.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="name">Name</Label>
                  <Input
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="demo-agent"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="modelProviderApiKeyId">
                    Model Provider API Key Id (uuid)
                  </Label>
                  <Input
                    id="modelProviderApiKeyId"
                    value={modelProviderApiKeyId}
                    onChange={(e) => setModelProviderApiKeyId(e.target.value)}
                    placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="instructions">Instructions</Label>
                  <Textarea
                    id="instructions"
                    value={instructions}
                    onChange={(e) => setInstructions(e.target.value)}
                    rows={4}
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="systemPrompt">System prompt</Label>
                  <Textarea
                    id="systemPrompt"
                    value={systemPrompt}
                    onChange={(e) => setSystemPrompt(e.target.value)}
                    rows={4}
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
                  onClick={() =>
                    createAgentMutation.mutate({
                      name,
                      instructions,
                      systemPrompt,
                      modelProviderApiKeyId,
                    })
                  }
                  disabled={
                    !name.trim() ||
                    !modelProviderApiKeyId.trim() ||
                    createAgentMutation.isPending
                  }
                >
                  {createAgentMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Agents</CardTitle>
          <CardDescription>
            Fetched from <code>/api/agents</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {agentsQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : agentsQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load agents: {getApiErrorMessage(agentsQuery.error)}
            </div>
          ) : (
            <pre className="max-h-[520px] overflow-auto rounded-md border bg-muted/30 p-3 text-xs">
              {pretty(agentsQuery.data)}
            </pre>
          )}
        </CardContent>
      </Card>
    </div>
  )
}