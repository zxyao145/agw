"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { toast } from "sonner";

import { apiGet, apiPut } from "@agw/api";
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
import type {
  ProviderAuthConfigRequest,
  ProviderDto,
  ProviderModelDto,
  ProviderModelRelationDto,
  ProviderType,
  ProviderUpdateRequest,
} from "./types";

interface EditProviderDialogProps {
  provider: ProviderDto | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function EditProviderDialog({ provider, open, onOpenChange }: EditProviderDialogProps) {
  const queryClient = useQueryClient();
  const initializedProviderId = React.useRef<string | null>(null);
  const [name, setName] = React.useState("");
  const [providerType, setProviderType] = React.useState<ProviderType>("OpenAIChatCompletions");
  const [description, setDescription] = React.useState("");
  const [endpoint, setEndpoint] = React.useState("");
  const [authConfigs, setAuthConfigs] = React.useState<ProviderAuthConfigRequest[]>([]);
  const [selectedModelNames, setSelectedModelNames] = React.useState<string[]>([]);

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => (await apiGet("/api/models")) as unknown as ProviderModelDto[],
    enabled: open,
  });

  const modelRelationsQuery = useQuery({
    queryKey: ["model-providers", provider?.id],
    queryFn: async () => {
      if (!provider) {
        return [];
      }
      return (await apiGet("/api/model-providers", {
        params: { query: { providerId: provider.id } },
      })) as unknown as ProviderModelRelationDto[];
    },
    enabled: open && provider !== null,
  });

  React.useEffect(() => {
    if (!open) {
      initializedProviderId.current = null;
      return;
    }
    if (!provider || initializedProviderId.current === provider.id) {
      return;
    }

    setName(provider.name);
    setProviderType(provider.providerType);
    setDescription(provider.description ?? "");
    setEndpoint(provider.endpoint);
    setAuthConfigs(
      (provider.authConfigs ?? []).map((config) => ({
        authType: config.authType,
        apiKey: config.apiKey,
        envKey: config.envKey,
        enable: config.enable,
      })),
    );
  }, [provider, open]);

  React.useEffect(() => {
    if (
      !open ||
      !provider ||
      !modelsQuery.data ||
      !modelRelationsQuery.data ||
      initializedProviderId.current === provider.id
    ) {
      return;
    }

    const modelNameById = new Map(modelsQuery.data.map((model) => [model.id, model.name]));
    setSelectedModelNames(
      modelRelationsQuery.data
        .map((relation) => modelNameById.get(relation.modelId))
        .filter((modelName): modelName is string => modelName !== undefined),
    );
    initializedProviderId.current = provider.id;
  }, [modelRelationsQuery.data, modelsQuery.data, open, provider]);

  const updateProviderMutation = useMutation({
    mutationFn: async (body: ProviderUpdateRequest) => {
      if (!provider) {
        throw new Error("Provider is required");
      }
      return await apiPut("/api/providers/{id}", {
        params: { path: { id: provider.id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Provider updated");
      onOpenChange(false);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["providers"] }),
        queryClient.invalidateQueries({ queryKey: ["models"] }),
        queryClient.invalidateQueries({ queryKey: ["model-providers"] }),
      ]);
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const handleUpdate = () => {
    if (!provider) {
      return;
    }

    updateProviderMutation.mutate({
      name,
      providerType,
      endpoint,
      description: description.trim() || null,
      authConfigs: normalizeAuthConfigs(authConfigs),
      modelNames: selectedModelNames,
    });
  };

  const modelDraftReady =
    provider !== null &&
    initializedProviderId.current === provider.id &&
    modelsQuery.isSuccess &&
    modelRelationsQuery.isSuccess;
  const isDisabled =
    !provider ||
    !name.trim() ||
    !endpoint.trim() ||
    !modelDraftReady ||
    updateProviderMutation.isPending;
  const modelsError = modelsQuery.error ?? modelRelationsQuery.error;

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) =>
        applyDialogOpenChange({
          isPending: updateProviderMutation.isPending,
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
                <DialogTitle>Edit provider</DialogTitle>
                <DialogDescription className="mt-1">
                  Update provider metadata, authentication, and available models.
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <DialogClose asChild>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={updateProviderMutation.isPending}
                  >
                    Cancel
                  </Button>
                </DialogClose>
                <Button type="button" size="sm" onClick={handleUpdate} disabled={isDisabled}>
                  {updateProviderMutation.isPending ? "Updating..." : "Update"}
                </Button>
              </div>
            </div>
          </DialogHeader>

          <ProviderFormFields
            idPrefix="edit-provider-"
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
            models={modelsQuery.data ?? []}
            selectedModelNames={selectedModelNames}
            setSelectedModelNames={setSelectedModelNames}
            modelsLoading={modelsQuery.isLoading || modelRelationsQuery.isLoading}
            modelsError={modelsError}
            retryModels={() => {
              void modelsQuery.refetch();
              void modelRelationsQuery.refetch();
            }}
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
