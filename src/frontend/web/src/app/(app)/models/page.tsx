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

type ModelCreateRequest = components["schemas"]["ModelCreateRequest"]

type ModelDto = {
  id: string
  name: string
  description: string | null
  type: number
  maxTokens: number
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

function parseIntOrNull(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed.length) return null
  const n = Number(trimmed)
  if (!Number.isFinite(n)) return null
  return Math.trunc(n)
}

export default function ModelsPage() {
  const queryClient = useQueryClient()

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/models")) as unknown as ModelDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [name, setName] = React.useState("")
  const [description, setDescription] = React.useState("")
  const [type, setType] = React.useState("0")
  const [maxTokens, setMaxTokens] = React.useState("4096")

  const createModelMutation = useMutation({
    mutationFn: async (body: ModelCreateRequest) => {
      return await apiPost("/api/models", { body })
    },
    onSuccess: async () => {
      toast.success("Model created")
      setCreateOpen(false)
      setName("")
      setDescription("")
      setType("0")
      setMaxTokens("4096")
      await queryClient.invalidateQueries({ queryKey: ["models"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  const parsedType = parseIntOrNull(type)
  const parsedMaxTokens = parseIntOrNull(maxTokens)

  const createDisabled =
    !name.trim() ||
    parsedType === null ||
    parsedMaxTokens === null ||
    createModelMutation.isPending

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Models</h1>
          <p className="text-sm text-muted-foreground">
            Manage LLM models (type/maxTokens).
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => modelsQuery.refetch()}
            disabled={modelsQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create model</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create model</UiDialogTitle>
                <UiDialogDescription>
                  Uses <code>/api/models</code> with{" "}
                  <code>ModelCreateRequest</code>.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="name">Name</Label>
                  <Input
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="gpt-4o-mini"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="type">Type (int)</Label>
                  <Input
                    id="type"
                    inputMode="numeric"
                    value={type}
                    onChange={(e) => setType(e.target.value)}
                    placeholder="0"
                  />
                  <p className="text-xs text-muted-foreground">
                    Backend schema <code>ModelType</code> is an integer enum.
                  </p>
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="maxTokens">Max tokens (int)</Label>
                  <Input
                    id="maxTokens"
                    inputMode="numeric"
                    value={maxTokens}
                    onChange={(e) => setMaxTokens(e.target.value)}
                    placeholder="4096"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="description">Description (nullable)</Label>
                  <Textarea
                    id="description"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    rows={3}
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
                  disabled={createDisabled}
                  onClick={() =>
                    createModelMutation.mutate({
                      name,
                      description: description.length ? description : null,
                      type: parsedType ?? 0,
                      maxTokens: parsedMaxTokens ?? 0,
                    })
                  }
                >
                  {createModelMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Models</CardTitle>
          <CardDescription>
            Fetched from <code>/api/models</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {modelsQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : modelsQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load models: {getApiErrorMessage(modelsQuery.error)}
            </div>
          ) : (
            <pre className="max-h-[520px] overflow-auto rounded-md border bg-muted/30 p-3 text-xs">
              {pretty(modelsQuery.data)}
            </pre>
          )}
        </CardContent>
      </Card>
    </div>
  )
}