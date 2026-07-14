"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiPut } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";

import { ProviderAuthConfigEditor } from "./provider-auth-config-editor";
import type {
  ProviderAuthConfigRequest,
  ProviderType,
  ProviderDto,
  ProviderUpdateRequest,
} from "./types";
import { getApiErrorMessage } from "@/api/utils";

const providerTypeOptions: ProviderType[] = [
  "OpenAIChatCompletions",
  "OpenAIResponses",
  "Anthropic",
];

interface EditProviderDialogProps {
  provider: ProviderDto | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function EditProviderDialog({ provider, open, onOpenChange }: EditProviderDialogProps) {
  const queryClient = useQueryClient();

  const [name, setName] = React.useState("");
  const [providerType, setProviderType] = React.useState<ProviderType>("OpenAIChatCompletions");
  const [description, setDescription] = React.useState("");
  const [endpoint, setEndpoint] = React.useState("");
  const [authConfigs, setAuthConfigs] = React.useState<ProviderAuthConfigRequest[]>([]);

  React.useEffect(() => {
    if (!provider || !open) {
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
      await queryClient.invalidateQueries({ queryKey: ["providers"] });
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
      description: description.length ? description : null,
      authConfigs: authConfigs.map((config) => ({
        ...config,
        apiKey: config.authType === "ApiKey" ? config.apiKey?.trim() || null : null,
        envKey: config.authType === "EnvVariable" ? config.envKey?.trim() || null : null,
      })),
    });
  };

  const isDisabled =
    !provider || !name.trim() || !endpoint.trim() || updateProviderMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogHeader>
          <DialogTitle>Edit provider</DialogTitle>
          <DialogDescription>
            Uses <code>/api/providers</code>.
          </DialogDescription>
        </DialogHeader>
        <div className="grid max-h-[calc(100vh-200px)] gap-4 overflow-y-auto pr-2">
          <div className="grid gap-2">
            <Label htmlFor="edit-name">Name</Label>
            <Input
              id="edit-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="openai"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="edit-endpoint">Endpoint</Label>
            <Input
              id="edit-endpoint"
              value={endpoint}
              onChange={(e) => setEndpoint(e.target.value)}
              placeholder="https://api.openai.com/v1"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="edit-providerType">Provider type</Label>
            <Select
              value={providerType}
              onValueChange={(value) => setProviderType(value as ProviderType)}
            >
              <SelectTrigger id="edit-providerType" className="w-full">
                <SelectValue placeholder="Select a provider type" />
              </SelectTrigger>
              <SelectContent>
                {providerTypeOptions.map((option) => (
                  <SelectItem key={option} value={option}>
                    {option}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="grid gap-2">
            <Label htmlFor="edit-description">Description</Label>
            <Textarea
              id="edit-description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={3}
            />
          </div>

          <ProviderAuthConfigEditor value={authConfigs} onChange={setAuthConfigs} />
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>
          <Button type="button" onClick={handleUpdate} disabled={isDisabled}>
            {updateProviderMutation.isPending ? "Saving..." : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
