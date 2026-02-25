"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";

import { getApiErrorMessage } from "../agents/components/utils";

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
};

type McpToolServerRequest = {
  name: string;
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
  const argumentsList = form.argumentsText
    .split("\n")
    .map((value) => value.trim())
    .filter(Boolean);

  return {
    name: form.name.trim(),
    description: form.description.trim() || null,
    transportType: form.transportType.trim() || "stdio",
    command: form.command.trim() || null,
    arguments: argumentsList.length > 0 ? argumentsList : [],
    workingDirectory: form.workingDirectory.trim() || null,
    environmentVariables: parseJsonMap(
      "Environment variables",
      form.environmentVariablesText,
    ),
    url: form.url.trim() || null,
    headers: parseJsonMap("Headers", form.headersText),
    enabled: form.enabled,
  };
}

function fromServer(server: McpToolServerDto): FormState {
  return {
    name: server.name,
    description: server.description ?? "",
    transportType: server.transportType,
    command: server.command ?? "",
    argumentsText: (server.arguments ?? []).join("\n"),
    workingDirectory: server.workingDirectory ?? "",
    environmentVariablesText: JSON.stringify(
      server.environmentVariables ?? {},
      null,
      2,
    ),
    url: server.url ?? "",
    headersText: JSON.stringify(server.headers ?? {}, null, 2),
    enabled: server.enabled,
  };
}

export default function McpToolServersPage() {
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editOpen, setEditOpen] = React.useState(false);
  const [createForm, setCreateForm] = React.useState<FormState>(defaultForm);
  const [editForm, setEditForm] = React.useState<FormState>(defaultForm);
  const [editing, setEditing] = React.useState<McpToolServerDto | null>(null);

  const mcpToolServersQuery = useQuery({
    queryKey: ["mcpToolServers"],
    queryFn: async () => {
      return (await apiGet(
        "/api/mcp-tool-servers",
      )) as unknown as McpToolServerDto[];
    },
  });

  const createMutation = useMutation({
    mutationFn: async (body: McpToolServerRequest) => {
      return await apiPost("/api/mcp-tool-servers", { body });
    },
    onSuccess: async () => {
      toast.success("MCP tool server created");
      setCreateOpen(false);
      setCreateForm(defaultForm);
      await queryClient.invalidateQueries({ queryKey: ["mcpToolServers"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({
      id,
      body,
    }: {
      id: string;
      body: McpToolServerRequest;
    }) => {
      return await apiPut("/api/mcp-tool-servers/{id}", {
        params: { path: { id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("MCP tool server updated");
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
      await queryClient.invalidateQueries({ queryKey: ["mcpToolServers"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
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
          <Button onClick={() => setCreateOpen(true)}>
            <Plus className="mr-2 h-4 w-4" />
            Add Server
          </Button>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>MCP Tool Servers</CardTitle>
          <CardDescription>
            Fetched from <code>/api/mcp-tool-servers</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {mcpToolServersQuery.isLoading ? (
            <div className="text-sm text-muted-foreground">Loading...</div>
          ) : mcpToolServersQuery.isError ? (
            <div className="text-sm text-destructive">
              Failed to load servers:{" "}
              {getApiErrorMessage(mcpToolServersQuery.error)}
            </div>
          ) : (mcpToolServersQuery.data?.length ?? 0) > 0 ? (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Name</TableHead>
                  <TableHead>Transport</TableHead>
                  <TableHead>Target</TableHead>
                  <TableHead>Enabled</TableHead>
                  <TableHead className="w-32 text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {mcpToolServersQuery.data?.map((server) => (
                  <TableRow key={server.id}>
                    <TableCell>
                      <div className="font-medium">{server.name}</div>
                      <div className="text-xs text-muted-foreground truncate max-w-xs">
                        {server.description || "-"}
                      </div>
                    </TableCell>
                    <TableCell className="uppercase text-xs">
                      {server.transportType}
                    </TableCell>
                    <TableCell className="max-w-sm truncate font-mono text-xs">
                      {server.transportType === "stdio"
                        ? server.command || "-"
                        : server.url || "-"}
                    </TableCell>
                    <TableCell>{server.enabled ? "Yes" : "No"}</TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-2">
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => onEdit(server)}
                        >
                          <Pencil className="h-4 w-4" />
                        </Button>
                        <Button
                          type="button"
                          variant="destructive"
                          size="sm"
                          onClick={() => onDelete(server)}
                          disabled={deleteMutation.isPending}
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <div className="text-sm text-muted-foreground">
              No MCP tool servers found. Create one to get started.
            </div>
          )}
        </CardContent>
      </Card>

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
  const title =
    mode === "create" ? "Create MCP Tool Server" : "Edit MCP Tool Server";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-3xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            Configure stdio or http MCP transport settings.
          </DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-name`}>Name</Label>
            <Input
              id={`${mode}-name`}
              value={form.name}
              onChange={(event) =>
                setForm((prev) => ({ ...prev, name: event.target.value }))
              }
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
            <Input
              id={`${mode}-transportType`}
              value={form.transportType}
              onChange={(event) =>
                setForm((prev) => ({
                  ...prev,
                  transportType: event.target.value,
                }))
              }
              placeholder="stdio or http"
            />
          </div>

          <div className="flex items-end gap-3">
            <Switch
              id={`${mode}-enabled`}
              checked={form.enabled}
              onCheckedChange={(checked) =>
                setForm((prev) => ({ ...prev, enabled: checked }))
              }
            />
            <Label htmlFor={`${mode}-enabled`}>Enabled</Label>
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-command`}>Command</Label>
            <Input
              id={`${mode}-command`}
              value={form.command}
              onChange={(event) =>
                setForm((prev) => ({ ...prev, command: event.target.value }))
              }
              placeholder="npx"
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor={`${mode}-workingDirectory`}>
              Working Directory
            </Label>
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
            <Label htmlFor={`${mode}-arguments`}>
              Arguments (one per line)
            </Label>
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
            <Label htmlFor={`${mode}-url`}>URL</Label>
            <Input
              id={`${mode}-url`}
              value={form.url}
              onChange={(event) =>
                setForm((prev) => ({ ...prev, url: event.target.value }))
              }
              placeholder="http://localhost:3001/sse"
            />
          </div>

          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-env`}>
              Environment Variables (JSON object)
            </Label>
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
