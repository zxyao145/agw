"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@agw/components/query";
import { Pencil, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete } from "@agw/api";
import { Button } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";

import type { ModelDto } from "./types";
import { getApiErrorMessage } from "@agw/api";
import { StaticTable } from "@agw/components";
import { Empty } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";
import { ButtonGroup } from "@agw/components";

import { EditModelDialog } from "./edit-model-dialog";

const tokenNumberFormatter = new Intl.NumberFormat("en-US");

interface ModelsTableProps {
  models: ModelDto[] | undefined;
  isLoading: boolean;
  isError: boolean;
  error: unknown;
}

export function ModelsTable({ models, isLoading, isError, error }: ModelsTableProps) {
  const queryClient = useQueryClient();
  const [editingModel, setEditingModel] = React.useState<ModelDto | null>(null);
  const [editOpen, setEditOpen] = React.useState(false);

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
    <>
      <div>
        <StaticTable isEmpty={models === undefined || models.length === 0}>
          <Empty>No models found. Create one to get started.</Empty>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Description</TableHead>
              <TableHead className="text-right">Context window</TableHead>
              <TableHead className="text-right">Maximum output</TableHead>
              <TableHead>Created</TableHead>
              <TableHead className="w-24 text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {models!.map((model) => (
              <TableRow key={model.id}>
                <TableCell className="font-medium">{model.name}</TableCell>
                <TableCell className="max-w-xs truncate">{model.description || "-"}</TableCell>
                <TableCell className="text-right text-sm tabular-nums">
                  {tokenNumberFormatter.format(model.maxContextWindowTokens)}
                </TableCell>
                <TableCell className="text-right text-sm tabular-nums">
                  {tokenNumberFormatter.format(model.maxOutputTokens)}
                </TableCell>
                <TableCell className="text-xs text-muted-foreground">
                  {formatLocalDateTime(model.createTime)}
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-2">
                    <ButtonGroup>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Edit ${model.name}`}
                        title="Edit model"
                        onClick={() => {
                          setEditingModel(model);
                          setEditOpen(true);
                        }}
                        className="cursor-pointer"
                      >
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Delete ${model.name}`}
                        title="Delete model"
                        disabled={deleteModelMutation.isPending}
                        onClick={() => {
                          const ok = window.confirm(
                            `Delete model "${model.name}"?\n\nThis action cannot be undone.`,
                          );
                          if (!ok) return;
                          deleteModelMutation.mutate(model.id);
                        }}
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
      </div>

      <EditModelDialog
        model={editingModel}
        open={editOpen}
        onOpenChange={(nextOpen) => {
          setEditOpen(nextOpen);
          if (!nextOpen) {
            setEditingModel(null);
          }
        }}
      />
    </>
  );
}
