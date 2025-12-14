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

type ModelProviderCreateRequest =
  components["schemas"]["ModelProviderCreateRequest"]

type ModelProviderDto = {
  modelId: string
  providerId: string
  inputPrice: number
  outputPrice: number
  cacheRead: number
  cacheWrite: number
  rpsLimit: number
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

function parseFloatOrNull(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed.length) return null
  const n = Number(trimmed)
  if (!Number.isFinite(n)) return null
  return n
}

export default function ModelProvidersPage() {
  const queryClient = useQueryClient()

  const modelProvidersQuery = useQuery({
    queryKey: ["model-providers"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/model-providers")) as unknown as ModelProviderDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [modelId, setModelId] = React.useState("")
  const [providerId, setProviderId] = React.useState("")
  const [inputPrice, setInputPrice] = React.useState("0")
  const [outputPrice, setOutputPrice] = React.useState("0")
  const [cacheRead, setCacheRead] = React.useState("0")
  const [cacheWrite, setCacheWrite] = React.useState("0")
  const [rpsLimit, setRpsLimit] = React.useState("60")

  const createMutation = useMutation({
    mutationFn: async (body: ModelProviderCreateRequest) => {
      return await apiPost("/api/model-providers", { body })
    },
    onSuccess: async () => {
      toast.success("Model provider created")
      setCreateOpen(false)
      setModelId("")
      setProviderId("")
      setInputPrice("0")
      setOutputPrice("0")
      setCacheRead("0")
      setCacheWrite("0")
      setRpsLimit("60")
      await queryClient.invalidateQueries({ queryKey: ["model-providers"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  const parsedInputPrice = parseFloatOrNull(inputPrice)
  const parsedOutputPrice = parseFloatOrNull(outputPrice)
  const parsedCacheRead = parseFloatOrNull(cacheRead)
  const parsedCacheWrite = parseFloatOrNull(cacheWrite)
  const parsedRpsLimit = parseIntOrNull(rpsLimit)

  const createDisabled =
    !modelId.trim() ||
    !providerId.trim() ||
    parsedInputPrice === null ||
    parsedOutputPrice === null ||
    parsedCacheRead === null ||
    parsedCacheWrite === null ||
    parsedRpsLimit === null ||
    createMutation.isPending

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Model Providers</h1>
          <p className="text-sm text-muted-foreground">
            Manage pricing/limits for a model on a provider.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => modelProvidersQuery.refetch()}
            disabled={modelProvidersQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create model provider</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create model provider</UiDialogTitle>
                <UiDialogDescription>
                  Uses <code>/api/model-providers</code> with{" "}
                  <code>ModelProviderCreateRequest</code>.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="modelId">Model ID (uuid)</Label>
                  <Input
                    id="modelId"
                    value={modelId}
                    onChange={(e) => setModelId(e.target.value)}
                    placeholder="00000000-0000-0000-0000-000000000000"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="providerId">Provider ID (uuid)</Label>
                  <Input
                    id="providerId"
                    value={providerId}
                    onChange={(e) => setProviderId(e.target.value)}
                    placeholder="00000000-0000-0000-0000-000000000000"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="inputPrice">Input price (double)</Label>
                  <Input
                    id="inputPrice"
                    inputMode="decimal"
                    value={inputPrice}
                    onChange={(e) => setInputPrice(e.target.value)}
                    placeholder="0.0"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="outputPrice">Output price (double)</Label>
                  <Input
                    id="outputPrice"
                    inputMode="decimal"
                    value={outputPrice}
                    onChange={(e) => setOutputPrice(e.target.value)}
                    placeholder="0.0"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="cacheRead">Cache read (double)</Label>
                  <Input
                    id="cacheRead"
                    inputMode="decimal"
                    value={cacheRead}
                    onChange={(e) => setCacheRead(e.target.value)}
                    placeholder="0.0"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="cacheWrite">Cache write (double)</Label>
                  <Input
                    id="cacheWrite"
                    inputMode="decimal"
                    value={cacheWrite}
                    onChange={(e) => setCacheWrite(e.target.value)}
                    placeholder="0.0"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="rpsLimit">RPS limit (int)</Label>
                  <Input
                    id="rpsLimit"
                    inputMode="numeric"
                    value={rpsLimit}
                    onChange={(e) => setRpsLimit(e.target.value)}
                    placeholder="60"
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
                    createMutation.mutate({
                      modelId,
                      providerId,
                      inputPrice: parsedInputPrice ?? 0,
                      outputPrice: parsedOutputPrice ?? 0,
                      cacheRead: parsedCacheRead ?? 0,
                      cacheWrite: parsedCacheWrite ?? 0,
                      rpsLimit: parsedRpsLimit ?? 0,
                    })
                  }
                >
                  {createMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Model Providers</CardTitle>
          <CardDescription>
            Fetched from <code>/api/model-providers</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {modelProvidersQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : modelProvidersQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load model providers:{" "}
              {getApiErrorMessage(modelProvidersQuery.error)}
            </div>
          ) : (
            <pre className="max-h-[520px] overflow-auto rounded-md border bg-muted/30 p-3 text-xs">
              {pretty(modelProvidersQuery.data)}
            </pre>
          )}
        </CardContent>
      </Card>
    </div>
  )
}