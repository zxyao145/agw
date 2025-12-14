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

type ApiKeyCreateRequest = components["schemas"]["ApiKeyCreateRequest"]

type ModelProviderApiKeyDto = {
  id: string
  modelId: string
  providerId: string
  apiKey?: string | null
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

function parseBooleanOrNull(value: string): boolean | null {
  const v = value.trim().toLowerCase()
  if (!v.length) return null
  if (v === "true" || v === "1" || v === "yes" || v === "y") return true
  if (v === "false" || v === "0" || v === "no" || v === "n") return false
  return null
}

export default function ModelProviderKeysPage() {
  const queryClient = useQueryClient()

  const keysQuery = useQuery({
    queryKey: ["model-provider-keys"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/model-provider-keys")) as unknown as ModelProviderApiKeyDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [modelId, setModelId] = React.useState("")
  const [providerId, setProviderId] = React.useState("")
  const [apiKey, setApiKey] = React.useState("")
  const [enableText, setEnableText] = React.useState("true")

  const createMutation = useMutation({
    mutationFn: async (body: ApiKeyCreateRequest) => {
      return await apiPost("/api/model-provider-keys", { body })
    },
    onSuccess: async () => {
      toast.success("API key created")
      setCreateOpen(false)
      setModelId("")
      setProviderId("")
      setApiKey("")
      setEnableText("true")
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  const parsedEnable = parseBooleanOrNull(enableText)

  const createDisabled =
    !modelId.trim() ||
    !providerId.trim() ||
    !apiKey.trim() ||
    parsedEnable === null ||
    createMutation.isPending

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Model Provider Keys</h1>
          <p className="text-sm text-muted-foreground">
            Manage API keys for a (model, provider) pair.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => keysQuery.refetch()}
            disabled={keysQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create key</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create API key</UiDialogTitle>
                <UiDialogDescription>
                  Uses <code>/api/model-provider-keys</code> with{" "}
                  <code>ApiKeyCreateRequest</code>.
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
                  <Label htmlFor="apiKey">API Key</Label>
                  <Input
                    id="apiKey"
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    placeholder="sk-..."
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="enable">Enable (boolean)</Label>
                  <Input
                    id="enable"
                    value={enableText}
                    onChange={(e) => setEnableText(e.target.value)}
                    placeholder="true / false"
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
                      apiKey,
                      enable: parsedEnable ?? true,
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
          <CardTitle>Keys</CardTitle>
          <CardDescription>
            Fetched from <code>/api/model-provider-keys</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {keysQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : keysQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load keys: {getApiErrorMessage(keysQuery.error)}
            </div>
          ) : (
            <pre className="max-h-[520px] overflow-auto rounded-md border bg-muted/30 p-3 text-xs">
              {pretty(keysQuery.data)}
            </pre>
          )}
        </CardContent>
      </Card>
    </div>
  )
}