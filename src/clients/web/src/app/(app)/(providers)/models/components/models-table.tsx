"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete } from "@/api/client";
import { Button } from "@/components/ui/button";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";

import type { ModelDto } from "./types";
import { getApiErrorMessage } from "@/api/utils";
import { StaticTable } from "@/components/static-table";
import { Empty } from "@/components/ui/empty";

interface ModelsTableProps {
  models: ModelDto[] | undefined;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
}

export function ModelsTable({ models, isLoading, isError, error }: ModelsTableProps) {
  const queryClient = useQueryClient();

  const deleteModelMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/models/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Model deleted");
      await queryClient.invalidateQueries({ queryKey: ["models"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  if (isLoading) {
    return <div className="text-sm text-muted-foreground">Loading...</div>;
  }
  if (isError) {
    return (
      <div className="text-sm text-destructive">
        Failed to load models: {getApiErrorMessage(error)}
      </div>
    );
  }

  return (
    <div>
      <StaticTable isEmpty={models === undefined || models.length === 0}>
        <Empty>No models found. Create one to get started.</Empty>
        <TableHeader>
          <TableRow>
            <TableHead>Name</TableHead>
            <TableHead>Description</TableHead>
            <TableHead>Max Tokens</TableHead>
            <TableHead>Created</TableHead>
            <TableHead className="w-24 text-right">Actions</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {models!.map((model) => (
            <TableRow key={model.id}>
              <TableCell className="font-medium">{model.name}</TableCell>
              <TableCell className="max-w-xs truncate">{model.description || "-"}</TableCell>
              <TableCell className="text-right">{model.maxTokens.toLocaleString()}</TableCell>
              <TableCell className="text-xs text-muted-foreground">
                {model.createTime ? new Date(model.createTime).toLocaleString() : "-"}
              </TableCell>
              <TableCell className="text-right">
                <Button
                  type="button"
                  variant="destructive"
                  size="sm"
                  disabled={deleteModelMutation.isPending}
                  onClick={() => {
                    const ok = window.confirm(
                      `Delete model "${model.name}"?\n\nThis action cannot be undone.`,
                    );
                    if (!ok) return;
                    deleteModelMutation.mutate(model.id);
                  }}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </StaticTable>
    </div>
  );
}
