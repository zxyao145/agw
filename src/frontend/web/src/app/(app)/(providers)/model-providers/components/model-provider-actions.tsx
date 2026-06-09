"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiRequest } from "@/api/client";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
import { listKeysByPair } from "./utils";
import { getApiErrorMessage } from "@/api/utils";

type ModelProviderActionsProps = {
  modelProviderId: string;
};

export function ModelProviderActions({ modelProviderId }: ModelProviderActionsProps) {
  const queryClient = useQueryClient();

  const deleteModelProviderMutation = useMutation({
    mutationFn: async () => {
      const keys = await listKeysByPair({ modelProviderId });
      const deleteKey = apiRequest as unknown as (
        path: string,
        method: "delete",
        options: { params: { path: { id: string } } },
      ) => Promise<unknown>;

      await Promise.all(
        keys.map((k) =>
          deleteKey("/api/model-provider-keys/{id}", "delete", {
            params: { path: { id: k.id } },
          }),
        ),
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
