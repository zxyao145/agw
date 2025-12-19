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
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"

type ProviderCreateRequest = components["schemas"]["ProviderCreateRequest"]

type ProviderDto = {
  id: string
  name: string
  description: string | null
  endpoint: string
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
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

export default function ProvidersPage() {
  const queryClient = useQueryClient()

  const providersQuery = useQuery({
    queryKey: ["providers"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/providers")) as unknown as ProviderDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [name, setName] = React.useState("")
  const [description, setDescription] = React.useState<string>("")
  const [endpoint, setEndpoint] = React.useState("")

  const createProviderMutation = useMutation({
    mutationFn: async (body: ProviderCreateRequest) => {
      return await apiPost("/api/providers", { body })
    },
    onSuccess: async () => {
      toast.success("Provider created")
      setCreateOpen(false)
      setName("")
      setDescription("")
      setEndpoint("")
      await queryClient.invalidateQueries({ queryKey: ["providers"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Providers</h1>
          <p className="text-sm text-muted-foreground">
            Manage model providers endpoints.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => providersQuery.refetch()}
            disabled={providersQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create provider</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create provider</UiDialogTitle>
                <UiDialogDescription>
                  Uses <code>/api/providers</code>.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="name">Name</Label>
                  <Input
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="openai"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="endpoint">Endpoint</Label>
                  <Input
                    id="endpoint"
                    value={endpoint}
                    onChange={(e) => setEndpoint(e.target.value)}
                    placeholder="https://api.openai.com/v1"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="description">Description</Label>
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
                  onClick={() =>
                    createProviderMutation.mutate({
                      name,
                      endpoint,
                      description: description.length ? description : null,
                    })
                  }
                  disabled={
                    !name.trim() ||
                    !endpoint.trim() ||
                    createProviderMutation.isPending
                  }
                >
                  {createProviderMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Providers</CardTitle>
          <CardDescription>
            Fetched from <code>/api/providers</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {providersQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : providersQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load providers: {getApiErrorMessage(providersQuery.error)}
            </div>
          ) : providersQuery.data && providersQuery.data.length > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Description</TableHead>
                  <TableHead>Endpoint</TableHead>
                  <TableHead>Created</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {providersQuery.data.map((provider) => (
                  <TableRow key={provider.id}>
                    <TableCell className="font-medium">{provider.name}</TableCell>
                    <TableCell className="max-w-xs truncate">
                      {provider.description || "-"}
                    </TableCell>
                    <TableCell className="max-w-sm truncate font-mono text-xs">
                      {provider.endpoint}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {provider.createTime
                        ? new Date(provider.createTime).toLocaleString()
                        : "-"}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <div className="text-sm text-muted-foreground">
              No providers found. Create one to get started.
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}