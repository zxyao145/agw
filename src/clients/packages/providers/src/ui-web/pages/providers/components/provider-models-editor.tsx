"use client";

import * as React from "react";
import { useMutation } from "@agw/components/query";
import { RefreshCw } from "lucide-react";
import { toast } from "sonner";

import { apiPost } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import { Checkbox } from "@agw/components";
import { Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";

import {
  findDiscoveryApiKey,
  isProviderModelDiscoverySupported,
  mergeProviderModelOptions,
} from "./provider-models";
import type {
  ProviderAuthConfigRequest,
  ProviderModelDiscoveryResponse,
  ProviderModelDto,
  ProviderType,
} from "./types";

interface ProviderModelsEditorProps {
  idPrefix: string;
  providerType: ProviderType;
  endpoint: string;
  authConfigs: ProviderAuthConfigRequest[];
  models: ProviderModelDto[];
  selectedModelNames: string[];
  onSelectedModelNamesChange: (modelNames: string[]) => void;
  isLoading: boolean;
  error: unknown;
  onRetry: () => void;
}

export function ProviderModelsEditor({
  idPrefix,
  providerType,
  endpoint,
  authConfigs,
  models,
  selectedModelNames,
  onSelectedModelNamesChange,
  isLoading,
  error,
  onRetry,
}: ProviderModelsEditorProps) {
  const [discoveredModelNames, setDiscoveredModelNames] = React.useState<string[]>([]);
  const apiKey = findDiscoveryApiKey(authConfigs);
  const discoverySupported = isProviderModelDiscoverySupported(providerType);
  const modelOptions = React.useMemo(
    () => mergeProviderModelOptions(models, discoveredModelNames),
    [models, discoveredModelNames],
  );
  const selectedNames = React.useMemo(() => new Set(selectedModelNames), [selectedModelNames]);

  React.useEffect(() => {
    setDiscoveredModelNames([]);
  }, [endpoint, providerType]);

  const discoveryMutation = useMutation({
    mutationFn: async () => {
      if (!apiKey) {
        throw new Error("An enabled ApiKey is required");
      }

      return (await apiPost("/api/providers/discover-models", {
        body: {
          providerType,
          endpoint,
          apiKey,
        },
      })) as unknown as ProviderModelDiscoveryResponse;
    },
    onSuccess: (result) => {
      setDiscoveredModelNames((current) => [
        ...current,
        ...result.modelNames.filter((name) => !current.includes(name)),
      ]);
      toast.success(`Discovered ${result.modelNames.length} models`);
    },
    onError: (mutationError) => {
      toast.error(`Model discovery failed: ${getApiErrorMessage(mutationError)}`);
    },
  });

  const toggleModel = (modelName: string, checked: boolean) => {
    const nextNames = new Set(selectedNames);
    if (checked) {
      nextNames.add(modelName);
    } else {
      nextNames.delete(modelName);
    }
    onSelectedModelNamesChange([...nextNames]);
  };

  const discoveryDisabled =
    !discoverySupported || !endpoint.trim() || !apiKey || discoveryMutation.isPending;

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h3 className="font-medium">Models</h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Select the models available through this provider. New discoveries are created when you
            save.
          </p>
        </div>
        <Tooltip>
          <TooltipTrigger asChild>
            <span tabIndex={discoveryDisabled ? 0 : undefined} className="inline-flex">
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={discoveryDisabled}
                onClick={() => discoveryMutation.mutate()}
              >
                <RefreshCw
                  className={`mr-2 h-4 w-4 ${discoveryMutation.isPending ? "animate-spin" : ""}`}
                />
                {discoveryMutation.isPending ? "Fetching..." : "Fetch Models"}
              </Button>
            </span>
          </TooltipTrigger>
          <TooltipContent side="top">
            Only OpenAI APIs are supported, and an ApiKey must be configured before fetching models.
          </TooltipContent>
        </Tooltip>
      </div>

      {!discoverySupported ? (
        <p className="text-xs text-muted-foreground">
          Model discovery is available only for OpenAI Chat Completions and OpenAI Responses.
        </p>
      ) : !apiKey ? (
        <p className="text-xs text-muted-foreground">
          Add and enable an ApiKey in Auth Configs before fetching models.
        </p>
      ) : null}

      {isLoading ? (
        <div className="rounded-lg border border-dashed p-6 text-sm text-muted-foreground">
          Loading models...
        </div>
      ) : error ? (
        <div className="flex items-center justify-between gap-4 rounded-lg border border-destructive/30 bg-destructive/5 p-4">
          <p className="text-sm text-destructive">
            Failed to load models: {getApiErrorMessage(error)}
          </p>
          <Button type="button" size="sm" variant="outline" onClick={onRetry}>
            Retry
          </Button>
        </div>
      ) : modelOptions.length === 0 ? (
        <div className="rounded-lg border border-dashed p-8 text-center text-sm text-muted-foreground">
          No models available. Fetch from the provider or create a model first.
        </div>
      ) : (
        <div className="overflow-hidden rounded-lg border">
          <div className="border-b bg-muted/30 px-4 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
            {selectedModelNames.length} of {modelOptions.length} selected
          </div>
          <div className="max-h-[calc(100vh-260px)] divide-y overflow-y-auto agw-scrollbar">
            {modelOptions.map((model, index) => {
              const checkboxId = `${idPrefix}model-${index}`;
              return (
                <label
                  key={model.name}
                  htmlFor={checkboxId}
                  className="flex cursor-pointer items-center gap-3 px-4 py-3 transition-colors hover:bg-muted/30"
                >
                  <Checkbox
                    id={checkboxId}
                    checked={selectedNames.has(model.name)}
                    onCheckedChange={(checked) => toggleModel(model.name, checked === true)}
                  />
                  <span className="min-w-0 flex-1 truncate font-mono text-sm">{model.name}</span>
                  {model.isNew ? (
                    <Badge variant="secondary" className="shrink-0">
                      New
                    </Badge>
                  ) : null}
                </label>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
