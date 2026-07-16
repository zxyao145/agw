"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiGet, apiPost } from "@/api/client";
import { getApiErrorMessage } from "@/api/utils";
import { applyDialogOpenChange } from "@/components/definition-capabilities";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

import { ProviderFormFields } from "./provider-form-fields";
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

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => (await apiGet("/api/models")) as unknown as ProviderModelDto[],
    enabled: open,
  });

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

  const handleCreate = () => {
    createProviderMutation.mutate({
      name,
      providerType,
      endpoint,
      description: description.trim() || null,
      authConfigs: normalizeAuthConfigs(authConfigs),
      modelNames: selectedModelNames,
    });
  };

  const isDisabled = !name.trim() || !endpoint.trim() || createProviderMutation.isPending;

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
        className="fixed inset-0 h-screen w-screen max-w-none translate-x-0 translate-y-0 gap-0 rounded-none border-0 p-0 sm:max-w-none"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <div className="flex min-h-0 flex-col">
          <DialogHeader className="shrink-0 border-b px-6 py-4">
            <div className="flex items-start justify-between gap-4">
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
