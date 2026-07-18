"use client";

import * as React from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { Link2, Pencil, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@agw/api";
import { PaginatedTable } from "@agw/components";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@agw/components";
import { Switch } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";
import { Textarea } from "@agw/components";

import { getApiErrorMessage } from "@agw/api";
import { StaticTable } from "@agw/components";
import { Empty } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";
import { DEFAULT_PAGE_SIZE, getClampedPageIndex, type PagedResult } from "@agw/components";
import { ButtonGroup } from "@agw/components";

type McpToolServerDto = {
  id: string;
  name: string;
  description?: string | null;
  transportType: string;
  command?: string | null;
  arguments?: string[] | null;
  workingDirectory?: string | null;
  environmentVariables?: Record<string, string> | null;
  url?: string | null;
  headers?: Record<string, string> | null;
  enabled: boolean;
  createTime?: string;
  updateTime?: string | null;
};

type McpConnectResponse = {
  status: "success" | "failed";
  tools: McpToolItem[];
};

type McpToolItem = {
  name: string;
};

type McpToolServerRequest = {
  name: string;
  agentIds: string[] | null;
  description: string | null;
  transportType: string;
  command: string | null;
  arguments: string[] | null;
  workingDirectory: string | null;
  environmentVariables: Record<string, string> | null;
  url: string | null;
  headers: Record<string, string> | null;
  enabled: boolean;
};

type FormState = {
  name: string;
  description: string;
  transportType: string;
  command: string;
  argumentsText: string;
  workingDirectory: string;
  environmentVariablesText: string;
  url: string;
  headersText: string;
  enabled: boolean;
};

const defaultForm: FormState = {
  name: "",
  description: "",
  transportType: "stdio",
  command: "",
  argumentsText: "",
  workingDirectory: "",
  environmentVariablesText: "{}",
  url: "",
  headersText: "{}",
  enabled: true,
};

function normalizeTransportType(value: string | null | undefined): "stdio" | "http" {
  return value?.trim().toLowerCase() === "http" ? "http" : "stdio";
}

function parseJsonMap(label: string, value: string): Record<string, string> {
  const trimmed = value.trim();
  if (!trimmed) return {};
  const parsed = JSON.parse(trimmed);
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(`${label} must be a JSON object`);
  }

  return Object.fromEntries(
    Object.entries(parsed).map(([key, mapValue]) => [key, String(mapValue)]),
  );
}

function toRequest(form: FormState): McpToolServerRequest {
  const transportType = normalizeTransportType(form.transportType);
  const isStdio = transportType === "stdio";

  const argumentsList = form.argumentsText
    .split("\n")
    .map((value) => value.trim())
    .filter(Boolean);

  return {
    name: form.name.trim(),
    agentIds: null,
    description: form.description.trim() || null,
    transportType,
    command: isStdio ? form.command.trim() || null : null,
    arguments: isStdio ? (argumentsList.length > 0 ? argumentsList : []) : [],
    workingDirectory: isStdio ? form.workingDirectory.trim() || null : null,
    environmentVariables: isStdio
      ? parseJsonMap("Environment variables", form.environmentVariablesText)
      : {},
    url: isStdio ? null : form.url.trim() || null,
    headers: isStdio ? {} : parseJsonMap("Headers", form.headersText),
    enabled: form.enabled,
  };
}

function fromServer(server: McpToolServerDto): FormState {
  return {
    name: server.name,
    description: server.description ?? "",
    transportType: normalizeTransportType(server.transportType),
    command: server.command ?? "",
    argumentsText: (server.arguments ?? []).join("\n"),
    workingDirectory: server.workingDirectory ?? "",
    environmentVariablesText: JSON.stringify(server.environmentVariables ?? {}, null, 2),
    url: server.url ?? "",
    headersText: JSON.stringify(server.headers ?? {}, null, 2),
    enabled: server.enabled,
  };
}

export default function McpToolServersPage() {
  const queryClient = useQueryClient();
  const [pageIndex, setPageIndex] = React.useState(1);
  const [pageSize, setPageSize] = React.useState(DEFAULT_PAGE_SIZE);
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editOpen, setEditOpen] = React.useState(false);
  const [createForm, setCreateForm] = React.useState<FormState>(defaultForm);
  const [editForm, setEditForm] = React.useState<FormState>(defaultForm);
  const [editing, setEditing] = React.useState<McpToolServerDto | null>(null);
  const [toolsCount, setToolsCount] = React.useState<Record<string, number>>({});

  const mcpToolServersQuery = useQuery({
    queryKey: ["mcpToolServers", "paged", pageIndex, pageSize],
    queryFn: async () => {
      return (await apiGet("/api/mcp-tool-servers/paged", {
        params: { query: { pageIndex, pageSize } },
      })) as unknown as PagedResult<McpToolServerDto>;
    },
    placeholderData: keepPreviousData,
  });

  const servers = mcpToolServersQuery.data?.items ?? [];
  const total = Number(mcpToolServersQuery.data?.total ?? 0);

  React.useEffect(() => {
    if (!mcpToolServersQuery.data) return;
    const clampedPageIndex = getClampedPageIndex(total, pageIndex, pageSize);
    if (clampedPageIndex !== pageIndex) {
      setPageIndex(clampedPageIndex);
    }
  }, [mcpToolServersQuery.data, pageIndex, pageSize, total]);

  const createMutation = useMutation({
    mutationFn: async (body: McpToolServerRequest) => {
      return await apiPost("/api/mcp-tool-servers", { body });
    },
    onSuccess: async () => {
      toast.success("MCP tool server created");
      setPageIndex(1);
      setCreateOpen(false);
      setCreateForm(defaultForm);
      await queryClient.invalidateQueries({ queryKey: ["mcpToolServers"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: McpToolServerRequest }) => {
      return await apiPut("/api/mcp-tool-servers/{id}", {
        params: { path: { id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("MCP tool server updated");
      setPageIndex(1);
      setEditOpen(false);
      setEditing(null);
      await queryClient.invalidateQueries({ queryKey: ["mcpToolServers"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/mcp-tool-servers/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("MCP tool server deleted");
      setPageIndex(getClampedPageIndex(Math.max(0, total - 1), pageIndex, pageSize));
      await queryClient.invalidateQueries({ queryKey: ["mcpToolServers"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const connectMutation = useMutation({
    mutationFn: async (mcpToolServerId: string) => {
      setToolsCount((prev) => {
        const { [mcpToolServerId]: _, ...rest } = prev;
        return rest;
      });
      return (await apiPost("/api/mcp-tool-servers/connect", {
        body: { mcpToolServerId },
      })) as unknown as McpConnectResponse;
    },
    onSuccess: (result, id) => {
      if (result.status !== "success") {
        toast.error("MCP connect failed");
        return;
      }

      const count = result.tools?.length ?? 0;
      setToolsCount((prev) => ({ ...prev, [id]: count }));
      toast.success(`Connected successfully`);
      console.log("MCP Tools:", result.tools);
    },
    onError: (error) => {
      toast.error(`Connect failed: ${getApiErrorMessage(error)}`);
    },
  });

  const submitCreate = () => {
    try {
      const body = toRequest(createForm);
      if (!body.name) {
        toast.error("Name is required");
        return;
      }
      createMutation.mutate(body);
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  const submitEdit = () => {
    if (!editing) return;

    try {
      const body = toRequest(editForm);
      if (!body.name) {
        toast.error("Name is required");
        return;
      }
      updateMutation.mutate({ id: editing.id, body });
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  const onEdit = (server: McpToolServerDto) => {
    setEditing(server);
    setEditForm(fromServer(server));
    setEditOpen(true);
  };

  const onDelete = (server: McpToolServerDto) => {
    const ok = window.confirm(
      `Delete MCP tool server "${server.name}"?\n\nThis action cannot be undone.`,
    );
    if (!ok) return;
    deleteMutation.mutate(server.id);
  };

  const onToggleEnabled = (server: McpToolServerDto, checked: boolean) => {
    const body: McpToolServerRequest = {
      name: server.name,
      agentIds: null,
      description: server.description ?? null,
      transportType: server.transportType,
      command: server.command ?? null,
      arguments: server.arguments ?? [],
      workingDirectory: server.workingDirectory ?? null,
      environmentVariables: server.environmentVariables ?? {},
      url: server.url ?? null,
      headers: server.headers ?? {},
      enabled: checked,
    };

    updateMutation.mutate({ id: server.id, body });
  };

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">MCP Tool Servers</h1>
          <p className="text-sm text-muted-foreground">
            Manage MCP tool server records stored in the McpToolServer table.
          </p>
        </div>

        <div className="flex gap-2">
          <Button
            variant="outline"
            onClick={() => mcpToolServersQuery.refetch()}
            disabled={mcpToolServersQuery.isFetching}
          >
            Refresh
          </Button>
          <Button onClick={() => setCreateOpen(true)}>Add Server</Button>
        </div>
      </div>

      {mcpToolServersQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading...</div>
      ) : mcpToolServersQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load servers: {getApiErrorMessage(mcpToolServersQuery.error)}
        </div>
      ) : (
        <PaginatedTable
          pageIndex={pageIndex}
          pageSize={pageSize}
          total={total}
          isFetching={mcpToolServersQuery.isFetching}
          onPageIndexChange={setPageIndex}
          onPageSizeChange={(value) => {
            setPageSize(value);
            setPageIndex(1);
          }}
        >
          <StaticTable embedded isEmpty={servers.length === 0}>
            <Empty>
              <div className="text-sm text-muted-foreground">
                No MCP tool servers found. Create one to get started.
              </div>
            </Empty>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Transport</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Enabled</TableHead>
                <TableHead>Updated</TableHead>
                <TableHead className="w-52 text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {servers.map((server) => (
                <TableRow key={server.id}>
                  <TableCell>
                    <div className="font-medium">{server.name}</div>
                    {toolsCount[server.id] !== undefined && (
                      <div className="text-xs text-green-600 max-w-xs">
                        {toolsCount[server.id]} tools available
                      </div>
                    )}
                  </TableCell>
                  <TableCell>{server.description || "-"}</TableCell>
                  <TableCell className="uppercase text-xs">{server.transportType}</TableCell>
                  <TableCell className="max-w-sm truncate font-mono text-xs">
                    {server.transportType === "stdio" ? server.command || "-" : server.url || "-"}
                  </TableCell>
                  <TableCell>
                    <div className="flex items-center">
                      <Switch
                        checked={server.enabled}
                        onCheckedChange={(checked) => onToggleEnabled(server, checked)}
                        disabled={updateMutation.isPending}
                        aria-label={`${server.name} enabled`}
                      />
                    </div>
                  </TableCell>
                  <TableCell className="min-w-40 text-sm text-muted-foreground">
                    {formatLocalDateTime(server.updateTime ?? server.createTime)}
                  </TableCell>
                  <TableCell className="text-right">
                    <div className="flex justify-end gap-2">
                      <ButtonGroup>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          className="cursor-pointer"
                          onClick={() => connectMutation.mutate(server.id)}
                          disabled={connectMutation.isPending}
                          title="Connect and list tools"
                        >
                          <Link2 className="h-4 w-4" />
                          {/* <Cable className="h-4 w-4" /> */}
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          onClick={() => onEdit(server)}
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          onClick={() => onDelete(server)}
                          disabled={deleteMutation.isPending}
                          className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </ButtonGroup>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </StaticTable>
        </PaginatedTable>
      )}

      <McpToolServerDialog
        mode="create"
        open={createOpen}
        onOpenChange={setCreateOpen}
        form={createForm}
        setForm={setCreateForm}
        onSubmit={submitCreate}
        isSubmitting={createMutation.isPending}
      />

      <McpToolServerDialog
        mode="edit"
        open={editOpen}
        onOpenChange={setEditOpen}
        form={editForm}
        setForm={setEditForm}
        onSubmit={submitEdit}
        isSubmitting={updateMutation.isPending}
      />
    </div>
  );
}

function McpToolServerDialog({
  mode,
  open,
  onOpenChange,
  form,
  setForm,
  onSubmit,
  isSubmitting,
}: {
  mode: "create" | "edit";
  open: boolean;
  onOpenChange: (value: boolean) => void;
  form: FormState;
  setForm: React.Dispatch<React.SetStateAction<FormState>>;
  onSubmit: () => void;
  isSubmitting: boolean;
}) {
  const title = mode === "create" ? "Create MCP Tool Server" : "Edit MCP Tool Server";
  const isStdio = normalizeTransportType(form.transportType) === "stdio";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="xl" className="max-h-[90vh] overflow-y-auto agw-scrollbar">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>Configure stdio or http MCP transport settings.</DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-name`}>Name</Label>
            <Input
              id={`${mode}-name`}
              value={form.name}
              onChange={(event) => setForm((prev) => ({ ...prev, name: event.target.value }))}
              placeholder="github-mcp"
            />
          </div>

          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-description`}>Description</Label>
            <Input
              id={`${mode}-description`}
              value={form.description}
              onChange={(event) =>
                setForm((prev) => ({
                  ...prev,
                  description: event.target.value,
                }))
              }
              placeholder="MCP tool server for GitHub"
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-transportType`}>Transport Type</Label>
            <Select
              value={form.transportType}
              onValueChange={(value) =>
                setForm((prev) => ({
                  ...prev,
                  transportType: value,
                }))
              }
            >
              <SelectTrigger id={`${mode}-transportType`}>
                <SelectValue placeholder="Select transport type" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="stdio">stdio</SelectItem>
                <SelectItem value="http">http</SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div className="flex items-end gap-3">
            <Switch
              id={`${mode}-enabled`}
              checked={form.enabled}
              onCheckedChange={(checked) => setForm((prev) => ({ ...prev, enabled: checked }))}
            />
            <Label htmlFor={`${mode}-enabled`}>Enabled</Label>
          </div>

          {isStdio ? (
            <>
              <div className="space-y-2">
                <Label htmlFor={`${mode}-command`}>Command</Label>
                <Input
                  id={`${mode}-command`}
                  value={form.command}
                  onChange={(event) =>
                    setForm((prev) => ({
                      ...prev,
                      command: event.target.value,
                    }))
                  }
                  placeholder="npx"
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor={`${mode}-workingDirectory`}>Working Directory</Label>
                <Input
                  id={`${mode}-workingDirectory`}
                  value={form.workingDirectory}
                  onChange={(event) =>
                    setForm((prev) => ({
                      ...prev,
                      workingDirectory: event.target.value,
                    }))
                  }
                  placeholder="/workspace"
                />
              </div>

              <div className="space-y-2 sm:col-span-2">
                <Label htmlFor={`${mode}-arguments`}>Arguments (one per line)</Label>
                <Textarea
                  id={`${mode}-arguments`}
                  value={form.argumentsText}
                  onChange={(event) =>
                    setForm((prev) => ({
                      ...prev,
                      argumentsText: event.target.value,
                    }))
                  }
                  rows={4}
                  placeholder="-y\n@modelcontextprotocol/server-github"
                />
              </div>

              <div className="space-y-2 sm:col-span-2">
                <Label htmlFor={`${mode}-env`}>Environment Variables (JSON object)</Label>
                <Textarea
                  id={`${mode}-env`}
                  value={form.environmentVariablesText}
                  onChange={(event) =>
                    setForm((prev) => ({
                      ...prev,
                      environmentVariablesText: event.target.value,
                    }))
                  }
                  rows={5}
                />
              </div>
            </>
          ) : (
            <>
              <div className="space-y-2 sm:col-span-2">
                <Label htmlFor={`${mode}-url`}>URL</Label>
                <Input
                  id={`${mode}-url`}
                  value={form.url}
                  onChange={(event) => setForm((prev) => ({ ...prev, url: event.target.value }))}
                  placeholder="http://localhost:3001/sse"
                />
              </div>

              <div className="space-y-2 sm:col-span-2">
                <Label htmlFor={`${mode}-headers`}>Headers (JSON object)</Label>
                <Textarea
                  id={`${mode}-headers`}
                  value={form.headersText}
                  onChange={(event) =>
                    setForm((prev) => ({
                      ...prev,
                      headersText: event.target.value,
                    }))
                  }
                  rows={5}
                />
              </div>
            </>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={onSubmit} disabled={isSubmitting}>
            {mode === "create" ? "Create" : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
