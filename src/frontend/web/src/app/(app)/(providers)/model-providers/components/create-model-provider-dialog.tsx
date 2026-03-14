"use client"

import * as React from "react"
import { useMutation, useQueryClient, type UseQueryResult } from "@tanstack/react-query"
import { toast } from "sonner"

import { apiPost } from "@/api/client"
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
import type { ModelProviderCreateRequest, ModelDto, ProviderDto, SearchableSelectOption } from "./types"
import { getApiErrorMessage, parseFloatOrNull, parseIntOrNull } from "./utils"
import { SearchableSelect } from "./searchable-select"

type CreateModelProviderDialogProps = {
  modelsQuery: UseQueryResult<ModelDto[], Error>
  providersQuery: UseQueryResult<ProviderDto[], Error>
}

export function CreateModelProviderDialog({
  modelsQuery,
  providersQuery,
}: CreateModelProviderDialogProps) {
  const queryClient = useQueryClient()
  const [createOpen, setCreateOpen] = React.useState(false)
  const [modelId, setModelId] = React.useState("")
  const [providerId, setProviderId] = React.useState("")
  const [inputPrice, setInputPrice] = React.useState("0")
  const [outputPrice, setOutputPrice] = React.useState("0")
  const [cacheRead, setCacheRead] = React.useState("0")
  const [cacheWrite, setCacheWrite] = React.useState("0")
  const [rpsLimit, setRpsLimit] = React.useState("60")

  const modelOptions = React.useMemo<SearchableSelectOption[]>(() => {
    return (modelsQuery.data ?? []).map((m) => ({
      value: m.id,
      title: m.name,
      subtitle: m.id,
    }))
  }, [modelsQuery.data])

  const providerOptions = React.useMemo<SearchableSelectOption[]>(() => {
    return (providersQuery.data ?? []).map((p) => ({
      value: p.id,
      title: p.name,
      subtitle: p.id,
    }))
  }, [providersQuery.data])

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
    <Dialog open={createOpen} onOpenChange={setCreateOpen}>
      <DialogTrigger asChild>
        <Button>Create</Button>
      </DialogTrigger>

      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <UiDialogTitle>Create model provider</UiDialogTitle>
          <UiDialogDescription>
            Uses <code>/api/model-providers</code> with{" "}
            <code>ModelProviderCreateRequest</code>.
          </UiDialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          <SearchableSelect
            id="modelId"
            label="Model ID"
            value={modelId}
            onValueChange={setModelId}
            options={modelOptions}
            placeholder="Select a model"
            searchPlaceholder="Search models (name/id)..."
            isLoading={modelsQuery.isLoading}
            errorMessage={
              modelsQuery.isError ? getApiErrorMessage(modelsQuery.error) : null
            }
          />

          <SearchableSelect
            id="providerId"
            label="Provider ID"
            value={providerId}
            onValueChange={setProviderId}
            options={providerOptions}
            placeholder="Select a provider"
            searchPlaceholder="Search providers (name/id)..."
            isLoading={providersQuery.isLoading}
            errorMessage={
              providersQuery.isError
                ? getApiErrorMessage(providersQuery.error)
                : null
            }
          />

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
  )
}
