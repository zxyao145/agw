"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete } from "@/api/client";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
import { getApiErrorMessage, listKeysByPair } from "./utils";

type ModelProviderActionsProps = {
  modelProviderId: string;
};

export function ModelProviderActions({
  modelProviderId,
}: ModelProviderActionsProps) {
  const queryClient = useQueryClient();

  const deleteModelProviderMutation = useMutation({
    mutationFn: async () => {
      const keys = await listKeysByPair({ modelProviderId });
      await Promise.all(
        keys.map(async (k) => {
          const response = await fetch(`/api/model-provider-keys/${encodeURIComponent(k.id)}`, {
            method: "DELETE",
          });

          if (!response.ok) {
            throw new Error(`Failed to delete API key ${k.id}`);
          }
        }),
      );
      await apiDelete("/api/model-providers/{id}", {
        params: { path: { id: modelProviderId } },
      });
    },
    onSuccess: async () => {
      toast.success("Deleted model provider and keys");
      await queryClient.invalidateQueries({ queryKey: ["model-providers"] });
      await queryClient.invalidateQueries({ queryKey: ["model-provider-keys"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  return (
    <div className="flex justify-end gap-2">
      <ButtonGroup>
        <Button
          type="button"
          className="cursor-pointer"
          variant="destructive"
          size="sm"
          disabled={deleteModelProviderMutation.isPending}
          onClick={() => {
            const ok = window.confirm("Delete this model-provider link and all related keys?");
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
