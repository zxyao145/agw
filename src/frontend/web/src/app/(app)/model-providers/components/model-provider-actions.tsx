"use client"

import * as React from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { BookKey, KeyRound, Trash2 } from "lucide-react"
import { toast } from "sonner"

import { apiDelete, apiPost, apiPut } from "@/api/client"
import { Button } from "@/components/ui/button"
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
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { ButtonGroup } from "@/components/ui/button-group"
import type { ApiKeyCreateRequest, ApiKeyUpdateRequest } from "./types"
import { getApiErrorMessage, listKeysByPair } from "./utils"
import { Switch } from "./switch"

type ModelProviderActionsProps = {
  modelProviderId: string
  modelId: string
  providerId: string
}

export function ModelProviderActions({
  modelProviderId,
  modelId,
  providerId,
}: ModelProviderActionsProps) {
  const queryClient = useQueryClient()
  const [viewKeysOpen, setViewKeysOpen] = React.useState(false)
  const [addKeyOpen, setAddKeyOpen] = React.useState(false)
  const [apiKey, setApiKey] = React.useState("")
  const [enable, setEnable] = React.useState(true)

  const pairKeysQuery = useQuery({
    queryKey: ["model-provider-keys", modelProviderId],
    enabled: viewKeysOpen,
    queryFn: async () => await listKeysByPair({ modelProviderId }),
  })

  const createKeyMutation = useMutation({
    mutationFn: async (body: ApiKeyCreateRequest) => {
      return await apiPost("/api/model-provider-keys", { body })
    },
    onSuccess: async () => {
      toast.success("API key created")
      setAddKeyOpen(false)
      setApiKey("")
      setEnable(true)
      await pairKeysQuery.refetch()
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  const updateKeyMutation = useMutation({
    mutationFn: async (args: { id: string; body: ApiKeyUpdateRequest }) => {
      return await apiPut("/api/model-provider-keys/{id}", {
        params: { path: { id: args.id } },
        body: args.body,
      })
    },
    onSuccess: async () => {
      await pairKeysQuery.refetch()
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] })
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`)
    },
  })

  const deleteKeyMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/model-provider-keys/{id}", {
        params: { path: { id } },
      } as never)
    },
    onSuccess: async () => {
      toast.success("API key deleted")
      await pairKeysQuery.refetch()
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] })
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`)
    },
  })

  const deleteModelProviderMutation = useMutation({
    mutationFn: async () => {
      const keys = await listKeysByPair({ modelProviderId })
      await Promise.all(
        keys.map((k) =>
          // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
          apiDelete("/api/model-provider-keys/{id}", { params: { path: { id: k.id } } })
        )
      )
      // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
      await apiDelete("/api/model-providers/{modelProviderId}", {
        params: { path: { modelProviderId } },
      })
    },
    onSuccess: async () => {
      toast.success("Deleted model provider and keys")
      await queryClient.invalidateQueries({ queryKey: ["model-providers"] })
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] })
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`)
    },
  })

  const createKeyDisabled = !apiKey.trim() || createKeyMutation.isPending

  return (
    <div className="flex justify-end gap-2">
      <ButtonGroup>
        <Dialog open={viewKeysOpen} onOpenChange={setViewKeysOpen}>
          <DialogTrigger asChild>
            <Button type="button" variant="outline" size="sm" className="cursor-pointer">
              <BookKey className="h-4 w-4" />
            </Button>
          </DialogTrigger>

          <DialogContent className="max-h-[calc(100vh-4rem)] overflow-hidden">
            <DialogHeader>
              <UiDialogTitle>Keys</UiDialogTitle>
              <UiDialogDescription>
                Keys for selected (model, provider).
              </UiDialogDescription>
            </DialogHeader>

            <div className="max-h-[calc(100vh-16rem)] overflow-auto rounded-md border">
              {pairKeysQuery.isLoading ? (
                <div className="p-3 text-sm text-muted-foreground">
                  Loading...
                </div>
              ) : pairKeysQuery.isError ? (
                <div className="p-3 text-sm text-destructive">
                  Failed to load keys: {getApiErrorMessage(pairKeysQuery.error)}
                </div>
              ) : (pairKeysQuery.data?.length ?? 0) === 0 ? (
                <div className="p-3 text-sm text-muted-foreground">
                  No keys.
                </div>
              ) : (
                <Table>
                  <TableHeader className="bg-muted/30">
                    <TableRow>
                      <TableHead>Api Key</TableHead>
                      <TableHead className="w-32">Enable</TableHead>
                      <TableHead className="w-24 text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {(pairKeysQuery.data ?? []).map((k) => (
                      <TableRow key={k.id} className="align-top">
                        <TableCell className="min-w-0">
                          <div className="truncate font-mono text-xs">
                            {k.apiKey ?? ""}
                          </div>
                          <div className="truncate font-mono text-[10px] text-muted-foreground">
                            {k.id}
                          </div>
                        </TableCell>
                        <TableCell>
                          <Switch
                            checked={k.enable}
                            disabled={updateKeyMutation.isPending || !k.apiKey}
                            label="Enable"
                            onCheckedChange={(checked) =>
                              updateKeyMutation.mutate({
                                id: k.id,
                                body: {
                                  apiKey: k.apiKey ?? "",
                                  enable: checked,
                                },
                              })
                            }
                          />
                        </TableCell>
                        <TableCell className="text-right">
                          <Button
                            type="button"
                            variant="destructive"
                            size="sm"
                            disabled={deleteKeyMutation.isPending}
                            onClick={() => {
                              const ok = window.confirm(
                                `Delete this API key?\n\n${k.apiKey ?? k.id}`
                              )
                              if (!ok) return
                              deleteKeyMutation.mutate(k.id)
                            }}
                          >
                            <Trash2 className="h-4 w-4" />
                          </Button>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>

            <DialogFooter>
              <DialogClose asChild>
                <Button type="button" variant="outline">
                  Close
                </Button>
              </DialogClose>
            </DialogFooter>
          </DialogContent>
        </Dialog>

        <Dialog
          open={addKeyOpen}
          onOpenChange={(open) => {
            setAddKeyOpen(open);
            if (open) {
              setApiKey("");
              setEnable(true);
            }
          }}
        >
          <DialogTrigger asChild>
            <Button type="button" variant="outline" size="sm" className="cursor-pointer">
                <KeyRound className="h-4 w-4" />
            </Button>
          </DialogTrigger>

          <DialogContent>
            <DialogHeader>
              <UiDialogTitle>Add API key</UiDialogTitle>
              <UiDialogDescription>
                Add API key for selected (model, provider).
              </UiDialogDescription>
            </DialogHeader>

            <div className="grid gap-4">
              <div className="grid gap-2">
                <Label htmlFor="apiKey">API Key</Label>
                <Input
                  id="apiKey"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder="sk-..."
                />
              </div>

              <div className="flex items-center justify-between gap-3 rounded-md border p-3">
                <div className="text-sm">
                  <div className="font-medium">Enable</div>
                  <div className="text-xs text-muted-foreground">
                    Whether this key is active.
                  </div>
                </div>
                <Switch checked={enable} onCheckedChange={setEnable} />
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
                disabled={createKeyDisabled}
                onClick={() =>
                  createKeyMutation.mutate({
                    modelProviderId: modelProviderId,
                    apiKey,
                    enable,
                  })
                }
              >
                {createKeyMutation.isPending ? "Creating..." : "Create"}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>

        <Button
          type="button"
          className="cursor-pointer"
          variant="destructive"
          size="sm"
          disabled={deleteModelProviderMutation.isPending}
          onClick={() => {
            const ok = window.confirm(
              "Delete this model-provider link and all related keys?"
            );
            if (!ok) return;
            deleteModelProviderMutation.mutate();
          }}
        >
          <Trash2 className="w-4 h-4" />
        </Button>
      </ButtonGroup>
    </div>
  );
}
