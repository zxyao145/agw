"use client"

import { useMutation, useQueryClient } from "@tanstack/react-query"
import { Trash2 } from "lucide-react"
import { toast } from "sonner"

import { apiDelete } from "@/api/client"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"

import { getModelTypeLabel } from "./types"
import type { ModelDto } from "./types"
import { getApiErrorMessage } from "./utils"

interface ModelsTableProps {
  models: ModelDto[] | undefined
  isLoading: boolean
  isError: boolean
  error: unknown
}

export function ModelsTable({
  models,
  isLoading,
  isError,
  error,
}: ModelsTableProps) {
  const queryClient = useQueryClient()

  const deleteModelMutation = useMutation({
    mutationFn: async (id: string) => {
      // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
      return await apiDelete("/api/models/{id}", {
        params: { path: { id } },
      })
    },
    onSuccess: async () => {
      toast.success("Model deleted")
      await queryClient.invalidateQueries({ queryKey: ["models"] })
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`)
    },
  })

  return (
    <Card>
      <CardHeader>
        <CardTitle>Models</CardTitle>
        <CardDescription>
          Fetched from <code>/api/models</code>.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <div className="text-sm text-muted-foreground">Loading...</div>
        ) : isError ? (
          <div className="text-sm text-destructive">
            Failed to load models: {getApiErrorMessage(error)}
          </div>
        ) : models && models.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Max Tokens</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-24 text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {models.map((model) => (
                <TableRow key={model.id}>
                  <TableCell className="font-medium">{model.name}</TableCell>
                  <TableCell className="max-w-xs truncate">
                    {model.description || "-"}
                  </TableCell>
                  <TableCell>
                    <div className="text-sm">{getModelTypeLabel(model.type)}</div>
                    {/* <div className="text-xs text-muted-foreground font-mono">
                      {model.type}
                    </div> */}
                  </TableCell>
                  <TableCell className="text-right">
                    {model.maxTokens.toLocaleString()}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {model.createTime
                      ? new Date(model.createTime).toLocaleString()
                      : "-"}
                  </TableCell>
                  <TableCell className="text-right">
                    <Button
                      type="button"
                      variant="destructive"
                      size="sm"
                      disabled={deleteModelMutation.isPending}
                      onClick={() => {
                        const ok = window.confirm(
                          `Delete model "${model.name}"?\n\nThis action cannot be undone.`
                        )
                        if (!ok) return
                        deleteModelMutation.mutate(model.id)
                      }}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <div className="text-sm text-muted-foreground">
            No models found. Create one to get started.
          </div>
        )}
      </CardContent>
    </Card>
  )
}
