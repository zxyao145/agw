"use client";

import * as React from "react";
import { useMutation, useQueryClient, type UseQueryResult } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiPost } from "@/api/client";
import { getApiErrorMessage } from "@/api/utils";
import {
  SearchableSelect,
  type SearchableSelectOption,
} from "@/components/SearchableSelect/searchable-select";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";

import type { ModelDto, ModelProviderCreateRequest, ProviderDto } from "./types";

type CreateModelProviderDialogProps = {
  modelsQuery: UseQueryResult<ModelDto[], Error>;
  providersQuery: UseQueryResult<ProviderDto[], Error>;
};

export function CreateModelProviderDialog({
  modelsQuery,
  providersQuery,
}: CreateModelProviderDialogProps) {
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = React.useState(false);
  const [modelId, setModelId] = React.useState("");
  const [providerId, setProviderId] = React.useState("");

  const modelOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (modelsQuery.data ?? []).map((model) => ({
        value: model.id,
        title: model.name,
        subtitle: model.id,
      })),
    [modelsQuery.data],
  );

  const providerOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      (providersQuery.data ?? []).map((provider) => ({
        value: provider.id,
        title: `${provider.name} - ${provider.providerType}`,
        subtitle: provider.id,
      })),
    [providersQuery.data],
  );

  const createMutation = useMutation({
    mutationFn: async (body: ModelProviderCreateRequest) => {
      return await apiPost("/api/model-providers", { body });
    },
    onSuccess: async () => {
      toast.success("Model provider created");
      setCreateOpen(false);
      setModelId("");
      setProviderId("");
      await queryClient.invalidateQueries({ queryKey: ["model-providers"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const createDisabled = !modelId.trim() || !providerId.trim() || createMutation.isPending;

  return (
    <Dialog open={createOpen} onOpenChange={setCreateOpen}>
      <DialogTrigger asChild>
        <Button>Create</Button>
      </DialogTrigger>

      <DialogContent size="lg">
        <DialogHeader>
          <DialogTitle>Create model provider</DialogTitle>
          <DialogDescription>Associate an existing model with a provider.</DialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          <SearchableSelect
            id="modelId"
            label="Model"
            value={modelId}
            onValueChange={setModelId}
            options={modelOptions}
            placeholder="Select a model"
            searchPlaceholder="Search models..."
            isLoading={modelsQuery.isLoading}
            errorMessage={modelsQuery.isError ? getApiErrorMessage(modelsQuery.error) : null}
          />

          <SearchableSelect
            id="providerId"
            label="Provider"
            value={providerId}
            onValueChange={setProviderId}
            options={providerOptions}
            placeholder="Select a provider"
            searchPlaceholder="Search providers..."
            isLoading={providersQuery.isLoading}
            errorMessage={providersQuery.isError ? getApiErrorMessage(providersQuery.error) : null}
          />
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
                inputPrice: 0,
                outputPrice: 0,
                cacheRead: 0,
                cacheWrite: 0,
                rpsLimit: 0,
              })
            }
          >
            {createMutation.isPending ? "Creating..." : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
