"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { toast } from "sonner";

import { apiGet, apiPost } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import { applyDialogOpenChange } from "@agw/integrations";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import { ProviderFormFields } from "./provider-form-fields";
import {
  findDiscoveryApiKey,
  getProviderModelOptions,
  getSelectedOllamaContextWindows,
  getVisibleSelectedModelNames,
} from "./provider-models";
import { useProviderModelDiscovery } from "./use-provider-model-discovery";
import type {
  ProviderAuthConfigRequest,
  ProviderCreateRequest,
  ProviderModelDto,
  ProviderType,
} from "./types";

interface CreateProviderDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CreateProviderDialog({ open, onOpenChange }: CreateProviderDialogProps) {
  const queryClient = useQueryClient();
  const [name, setName] = React.useState("");
  const [providerType, setProviderType] = React.useState<ProviderType>("OpenAIChatCompletions");
  const [description, setDescription] = React.useState("");
  const [endpoint, setEndpoint] = React.useState("");
  const [authConfigs, setAuthConfigs] = React.useState<ProviderAuthConfigRequest[]>([]);
  const [selectedModelNames, setSelectedModelNames] = React.useState<string[]>([]);
  const apiKey = findDiscoveryApiKey(authConfigs);
  const discoveryScope = React.useMemo(
    () => ({
      open,
      providerId: "create",
      providerType,
      endpoint,
      apiKey,
    }),
    [open, providerType, endpoint, apiKey],
  );
  const discovery = useProviderModelDiscovery(discoveryScope);

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => (await apiGet("/api/models")) as unknown as ProviderModelDto[],
    enabled: open,
  });
  const modelOptions = React.useMemo(
    () => getProviderModelOptions(modelsQuery.data ?? [], discovery.data, providerType),
    [discovery.data, modelsQuery.data, providerType],
  );

  const createProviderMutation = useMutation({
    mutationFn: async (body: ProviderCreateRequest) => {
      return await apiPost("/api/providers", { body });
    },
    onSuccess: async () => {
      toast.success("Provider created");
      onOpenChange(false);
      setName("");
      setProviderType("OpenAIChatCompletions");
      setDescription("");
      setEndpoint("");
      setAuthConfigs([]);
      setSelectedModelNames([]);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["providers"] }),
        queryClient.invalidateQueries({ queryKey: ["models"] }),
        queryClient.invalidateQueries({ queryKey: ["model-providers"] }),
      ]);
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const modelSelectionReady = selectedModelNames.length === 0 || modelsQuery.isSuccess;
  const handleCreate = () => {
    if (!modelSelectionReady) return;
    const visibleModelNames = getVisibleSelectedModelNames(selectedModelNames, modelOptions);
    const ollamaContextWindows = getSelectedOllamaContextWindows(
      providerType,
      visibleModelNames,
      modelsQuery.isSuccess ? modelsQuery.data : undefined,
      discovery.data?.ollamaContextWindows ?? {},
    );

    createProviderMutation.mutate({
      name,
      providerType,
      endpoint,
      description: description.trim() || null,
      authConfigs: normalizeAuthConfigs(authConfigs),
      modelNames: visibleModelNames,
      ...(ollamaContextWindows ? { ollamaContextWindows } : {}),
    });
  };

  const isDisabled =
    !name.trim() || !endpoint.trim() || !modelSelectionReady || createProviderMutation.isPending;

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) =>
        applyDialogOpenChange({
          isPending: createProviderMutation.isPending,
          nextOpen,
          setOpen: onOpenChange,
        })
      }
    >
      <DialogContent
        size="fullscreen"
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex h-full min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-2">
            <div className="flex items-center justify-between gap-4">
              <div className="min-w-0">
                <DialogTitle>Create provider</DialogTitle>
                <DialogDescription className="mt-1">
                  Configure provider metadata, authentication, and available models.
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <DialogClose asChild>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={createProviderMutation.isPending}
                  >
                    Cancel
                  </Button>
                </DialogClose>
                <Button type="button" size="sm" onClick={handleCreate} disabled={isDisabled}>
                  {createProviderMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </div>
            </div>
          </DialogHeader>

          <ProviderFormFields
            idPrefix="create-provider-"
            modelOptions={modelOptions}
            isDiscoveryPending={discovery.isPending}
            fetchModels={discovery.fetchModels}
            hasApiKey={apiKey !== null}
            name={name}
            setName={setName}
            endpoint={endpoint}
            setEndpoint={setEndpoint}
            providerType={providerType}
            setProviderType={setProviderType}
            description={description}
            setDescription={setDescription}
            authConfigs={authConfigs}
            setAuthConfigs={setAuthConfigs}
            selectedModelNames={selectedModelNames}
            setSelectedModelNames={setSelectedModelNames}
            modelsLoading={modelsQuery.isLoading}
            modelsError={modelsQuery.error}
            retryModels={() => void modelsQuery.refetch()}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

function normalizeAuthConfigs(
  authConfigs: ProviderAuthConfigRequest[],
): ProviderAuthConfigRequest[] {
  return authConfigs.map((config) => ({
    ...config,
    apiKey: config.apiKey?.trim() || null,
    envKey: null,
  }));
}
