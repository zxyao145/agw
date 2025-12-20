"use client"

import * as React from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { ChevronDownIcon } from "lucide-react"
import { toast } from "sonner"

import { ApiError, apiDelete, apiGet, apiPost, apiPut } from "@/api/client"
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
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { ButtonGroup } from "@/components/ui/button-group"

type ModelProviderCreateRequest =
  components["schemas"]["ModelProviderCreateRequest"]

type ApiKeyCreateRequest = components["schemas"]["ApiKeyCreateRequest"]
type ApiKeyUpdateRequest = components["schemas"]["ApiKeyUpdateRequest"]

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

type SearchableSelectOption = {
  value: string
  title: string
  subtitle?: string
}

type SearchableSelectProps = {
  id: string
  label: string
  value: string
  onValueChange: (value: string) => void
  options: SearchableSelectOption[]
  placeholder: string
  searchPlaceholder: string
  disabled?: boolean
  isLoading?: boolean
  errorMessage?: string | null
}

function SearchableSelect({
  id,
  label,
  value,
  onValueChange,
  options,
  placeholder,
  searchPlaceholder,
  disabled,
  isLoading,
  errorMessage,
}: SearchableSelectProps) {
  const [open, setOpen] = React.useState(false)
  const [search, setSearch] = React.useState("")
  const rootRef = React.useRef<HTMLDivElement | null>(null)
  const searchInputRef = React.useRef<HTMLInputElement | null>(null)

  React.useEffect(() => {
    if (!open) return

    const t = window.setTimeout(() => {
      searchInputRef.current?.focus()
    }, 0)

    const onPointerDown = (e: MouseEvent) => {
      const el = rootRef.current
      if (!el) return
      if (e.target instanceof Node && !el.contains(e.target)) {
        setOpen(false)
        setSearch("")
      }
    }

    document.addEventListener("mousedown", onPointerDown, true)

    return () => {
      window.clearTimeout(t)
      document.removeEventListener("mousedown", onPointerDown, true)
    }
  }, [open])

  const selected = React.useMemo(
    () => options.find((x) => x.value === value) ?? null,
    [options, value]
  )

  const filteredOptions = React.useMemo(() => {
    const q = search.trim().toLowerCase()
    if (!q.length) return options
    return options.filter((opt) => {
      const haystack = `${opt.title} ${opt.subtitle ?? ""} ${opt.value}`.toLowerCase()
      return haystack.includes(q)
    })
  }, [options, search])

  const triggerText = selected ? selected.title : placeholder

  const itemClassName =
    "flex w-full cursor-pointer select-none items-start gap-2 rounded-sm px-2 py-1.5 text-left text-sm outline-none hover:bg-accent hover:text-accent-foreground focus:bg-accent focus:text-accent-foreground"

  return (
    <div ref={rootRef} className="grid gap-2">
      <Label htmlFor={id}>{label}</Label>

      <div className="relative">
        <Button
          id={id}
          type="button"
          variant="outline"
          className="w-full justify-between gap-2 overflow-hidden font-normal"
          disabled={disabled}
          aria-haspopup="listbox"
          aria-expanded={open}
          onClick={() => setOpen((x) => !x)}
        >
          <span className={selected ? "truncate" : "truncate text-muted-foreground"}>
            {triggerText}
          </span>
          <ChevronDownIcon className="size-4 opacity-50" />
        </Button>

        {open && (
          <div
            className="bg-popover text-popover-foreground absolute left-0 top-full z-50 mt-1 w-full rounded-md border p-2 shadow-md"
            role="listbox"
            aria-label={label}
          >
            <div className="pb-2">
              <Input
                ref={searchInputRef}
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder={searchPlaceholder}
                onKeyDown={(e) => {
                  e.stopPropagation()
                  if (e.key === "Escape") {
                    setOpen(false)
                    setSearch("")
                  }
                }}
              />
            </div>

            {errorMessage ? (
              <div className="px-2 py-1.5 text-sm text-destructive">{errorMessage}</div>
            ) : isLoading ? (
              <div className="px-2 py-1.5 text-sm text-muted-foreground">Loading...</div>
            ) : (
              <div className="max-h-64 overflow-auto">
                {value.trim().length > 0 && (
                  <button
                    type="button"
                    className={`${itemClassName} text-muted-foreground`}
                    onClick={() => {
                      onValueChange("")
                      setOpen(false)
                      setSearch("")
                    }}
                  >
                    Clear selection
                  </button>
                )}

                {filteredOptions.length === 0 ? (
                  <div className="px-2 py-1.5 text-sm text-muted-foreground">
                    No results.
                  </div>
                ) : (
                  filteredOptions.map((opt) => (
                    <button
                      key={opt.value}
                      type="button"
                      className={itemClassName}
                      onClick={() => {
                        onValueChange(opt.value)
                        setOpen(false)
                        setSearch("")
                      }}
                    >
                      <div className="min-w-0">
                        <div className="truncate text-sm">{opt.title}</div>
                        {opt.subtitle ? (
                          <div className="truncate font-mono text-xs text-muted-foreground">
                            {opt.subtitle}
                          </div>
                        ) : null}
                      </div>
                    </button>
                  ))
                )}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

type SwitchProps = {
  checked: boolean
  onCheckedChange: (checked: boolean) => void
  disabled?: boolean
  label?: string
}

function Switch({ checked, onCheckedChange, disabled, label }: SwitchProps) {
  return (
    <label
      className="inline-flex items-center gap-2"
      aria-label={label}
      title={label}
    >
      <input
        type="checkbox"
        className="peer sr-only"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onCheckedChange(e.target.checked)}
      />
      <span
        className={[
          "relative inline-flex h-5 w-9 items-center rounded-full border transition-colors",
          "bg-muted peer-checked:bg-primary",
          "peer-disabled:cursor-not-allowed peer-disabled:opacity-50",
        ].join(" ")}
      >
        <span
          className={[
            "absolute left-0.5 top-0.5 h-4 w-4 rounded-full bg-background shadow-sm transition-transform",
            checked ? "translate-x-4" : "translate-x-0",
          ].join(" ")}
        />
      </span>
    </label>
  )
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

async function listKeysByPair(args: {
  modelId: string
  providerId: string
}): Promise<ModelProviderApiKeyDto[]> {
  // NOTE: openapi-typescript didn't generate query param types for this endpoint
  // so we narrow the typing at the boundary.
  return (await apiGet("/api/model-provider-keys", {
    params: {
      query: { modelId: args.modelId, providerId: args.providerId },
    },
  } as never)) as unknown as ModelProviderApiKeyDto[];
}

function ModelProviderActions({
  modelId,
  providerId,
}: {
  modelId: string
  providerId: string
}) {
  const queryClient = useQueryClient()
  const [viewKeysOpen, setViewKeysOpen] = React.useState(false)
  const [addKeyOpen, setAddKeyOpen] = React.useState(false)
  const [apiKey, setApiKey] = React.useState("")
  const [enable, setEnable] = React.useState(true)

  const pairKeysQuery = useQuery({
    queryKey: ["model-provider-keys", modelId, providerId],
    enabled: viewKeysOpen,
    queryFn: async () => await listKeysByPair({ modelId, providerId }),
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

  const deleteModelProviderMutation = useMutation({
    mutationFn: async () => {
      const keys = await listKeysByPair({ modelId, providerId })
      await Promise.all(
        keys.map((k) =>
          apiDelete("/api/model-provider-keys/{id}", { params: { path: { id: k.id } } } as never)
        )
      )
      await apiDelete("/api/model-providers/{modelId}/{providerId}", {
        params: { path: { modelId, providerId } },
      } as never)
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
            <Button type="button" variant="outline" size="sm">
              查看Keys
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
            <Button type="button" variant="outline" size="sm">
              增加 key
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
                    modelId,
                    providerId,
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
          删除
        </Button>
      </ButtonGroup>
    </div>
  );
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

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/models")) as unknown as ModelDto[]
    },
  })

  const providersQuery = useQuery({
    queryKey: ["providers"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/providers")) as unknown as ProviderDto[]
    },
  })

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

  const modelNameById = React.useMemo(() => {
    return new Map((modelsQuery.data ?? []).map((m) => [m.id, m.name] as const))
  }, [modelsQuery.data])

  const providerNameById = React.useMemo(() => {
    return new Map((providersQuery.data ?? []).map((p) => [p.id, p.name] as const))
  }, [providersQuery.data])

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
            onClick={() => {
              modelProvidersQuery.refetch()
              modelsQuery.refetch()
              providersQuery.refetch()
            }}
            disabled={
              modelProvidersQuery.isFetching || modelsQuery.isFetching || providersQuery.isFetching
            }
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
          ) : (modelProvidersQuery.data?.length ?? 0) === 0 ? (
            <div className="text-sm text-muted-foreground">No model providers found.</div>
          ) : (
            <div className="rounded-md border">
              <Table className="min-w-[960px]">
                <TableHeader className="bg-muted/30">
                  <TableRow>
                    <TableHead>Model</TableHead>
                    <TableHead>Provider</TableHead>
                    <TableHead className="text-right">Input</TableHead>
                    <TableHead className="text-right">Output</TableHead>
                    <TableHead className="text-right">Cache read</TableHead>
                    <TableHead className="text-right">Cache write</TableHead>
                    <TableHead className="text-right">RPS</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {(modelProvidersQuery.data ?? []).map((item) => {
                    const modelName = modelNameById.get(item.modelId) ?? "Unknown model"
                    const providerName =
                      providerNameById.get(item.providerId) ?? "Unknown provider"

                    return (
                      <TableRow key={`${item.modelId}:${item.providerId}`} className="align-top">
                        <TableCell className="min-w-48">
                          <div className="min-w-0">
                            <div className="truncate">{modelName}</div>
                            <div className="truncate font-mono text-xs text-muted-foreground">
                              {item.modelId}
                            </div>
                          </div>
                        </TableCell>
                        <TableCell className="min-w-48">
                          <div className="min-w-0">
                            <div className="truncate">{providerName}</div>
                            <div className="truncate font-mono text-xs text-muted-foreground">
                              {item.providerId}
                            </div>
                          </div>
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                          {String(item.inputPrice)}
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                          {String(item.outputPrice)}
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                          {String(item.cacheRead)}
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                          {String(item.cacheWrite)}
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                          {String(item.rpsLimit)}
                        </TableCell>
                        <TableCell className="whitespace-nowrap text-right">
                          <ModelProviderActions modelId={item.modelId} providerId={item.providerId} />
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>

    </div>
  )
}