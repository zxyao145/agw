"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@agw/components/query";
import { Pencil, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete } from "@agw/api";
import { Button } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";

import { EditProviderDialog } from "./edit-provider-dialog";
import type { ProviderDto } from "./types";
import { getApiErrorMessage } from "@agw/api";
import { StaticTable } from "@agw/components";
import { Empty } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";
import { ButtonGroup } from "@agw/components";

interface ProvidersTableProps {
  providers?: ProviderDto[];
  isLoading: boolean;
  isError: boolean;
  error: unknown;
}

export function ProvidersTable({ providers, isLoading, isError, error }: ProvidersTableProps) {
  const queryClient = useQueryClient();
  const [editingProvider, setEditingProvider] = React.useState<ProviderDto | null>(null);

  const deleteProviderMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/providers/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Provider deleted");
      await queryClient.invalidateQueries({ queryKey: ["providers"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const handleDelete = (provider: ProviderDto) => {
    const ok = window.confirm(
      `Delete provider "${provider.name}"?\n\nThis action cannot be undone.`,
    );
    if (!ok) return;
    deleteProviderMutation.mutate(provider.id);
  };

  if (isLoading) {
    return <div className="text-sm text-muted-foreground">Loading...</div>;
  }
  if (isError) {
    return (
      <div className="text-sm text-destructive">
        Failed to load providers: {getApiErrorMessage(error)}
      </div>
    );
  }

  return (
    <StaticTable isEmpty={providers === undefined || providers.length === 0}>
      <Empty>
        <div className="text-sm text-muted-foreground">
          No providers found. Create one to get started.
        </div>
      </Empty>
      <TableHeader>
        <TableRow>
          <TableHead>Name</TableHead>
          <TableHead>Description</TableHead>
          <TableHead>Provider Type</TableHead>
          <TableHead>Endpoint</TableHead>
          <TableHead>Auth Configs</TableHead>
          <TableHead>Created</TableHead>
          <TableHead className="w-24 text-right">Actions</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {providers!.map((provider) => (
          <TableRow key={provider.id}>
            <TableCell className="font-medium">{provider.name}</TableCell>
            <TableCell className="max-w-xs truncate">{provider.description || "-"}</TableCell>
            <TableCell>{provider.providerType}</TableCell>
            <TableCell className="max-w-sm truncate font-mono text-xs">
              {provider.endpoint}
            </TableCell>
            <TableCell>{provider.authConfigs?.length ?? 0}</TableCell>
            <TableCell className="text-xs text-muted-foreground">
              {formatLocalDateTime(provider.createTime)}
            </TableCell>
            <TableCell className="text-right">
              <div className="flex justify-end gap-2">
                <ButtonGroup>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    onClick={() => setEditingProvider(provider)}
                  >
                    <Pencil className="h-4 w-4" />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    disabled={deleteProviderMutation.isPending}
                    onClick={() => handleDelete(provider)}
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

      <EditProviderDialog
        provider={editingProvider}
        open={editingProvider !== null}
        onOpenChange={(nextOpen) => {
          if (!nextOpen) {
            setEditingProvider(null);
          }
        }}
      />
    </StaticTable>
  );
}
