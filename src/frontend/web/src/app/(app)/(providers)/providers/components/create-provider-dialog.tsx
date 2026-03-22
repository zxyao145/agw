"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiPost } from "@/api/client";
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

import type { ProviderAuthConfigRequest, ProviderCreateRequest, ProviderType } from "./types";
import { getApiErrorMessage } from "./utils";
import { ProviderAuthConfigEditor } from "./provider-auth-config-editor";

const providerTypeOptions: ProviderType[] = ["OpenAI", "Anthropic"];

interface CreateProviderDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CreateProviderDialog({ open, onOpenChange }: CreateProviderDialogProps) {
  const queryClient = useQueryClient();

  const [name, setName] = React.useState("");
  const [providerType, setProviderType] = React.useState<ProviderType>("OpenAI");
  const [description, setDescription] = React.useState<string>("");
  const [endpoint, setEndpoint] = React.useState("");
  const [authConfigs, setAuthConfigs] = React.useState<ProviderAuthConfigRequest[]>([]);

  const createProviderMutation = useMutation({
    mutationFn: async (body: ProviderCreateRequest) => {
      return await apiPost("/api/providers", { body });
    },
    onSuccess: async () => {
      toast.success("Provider created");
      onOpenChange(false);
      setName("");
      setProviderType("OpenAI");
      setDescription("");
      setEndpoint("");
      setAuthConfigs([]);
      await queryClient.invalidateQueries({ queryKey: ["providers"] });
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
      description: description.length ? description : null,
      authConfigs: authConfigs.map((config) => ({
        ...config,
        apiKey: config.authType === "ApiKey" ? config.apiKey?.trim() || null : null,
        envKey: config.authType === "EnvVariable" ? config.envKey?.trim() || null : null,
      })),
    });
  };

  const isDisabled = !name.trim() || !endpoint.trim() || createProviderMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Create provider</DialogTitle>
          <DialogDescription>
            Uses <code>/api/providers</code>.
          </DialogDescription>
        </DialogHeader>

        <div className="grid max-h-[calc(100vh-200px)] gap-4 overflow-y-auto pr-2">
          <div className="grid gap-2">
            <Label htmlFor="name">Name</Label>
            <Input
              id="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="openai"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="endpoint">Endpoint</Label>
            <Input
              id="endpoint"
              value={endpoint}
              onChange={(e) => setEndpoint(e.target.value)}
              placeholder="https://api.openai.com/v1"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="providerType">Provider type</Label>
            <Select
              value={providerType}
              onValueChange={(value) => setProviderType(value as ProviderType)}
            >
              <SelectTrigger id="providerType">
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
            <Label htmlFor="description">Description</Label>
            <Textarea
              id="description"
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
          <Button type="button" onClick={handleCreate} disabled={isDisabled}>
            {createProviderMutation.isPending ? "Creating..." : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
