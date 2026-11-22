"use client";

import * as React from "react";
import { RefreshCw } from "lucide-react";

import { getApiErrorMessage } from "@agw/api";
import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import { Checkbox } from "@agw/components";
import { Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";

import {
  getVisibleSelectedModelNames,
  isProviderModelDiscoveryApiKeyRequired,
  isProviderModelDiscoverySupported,
} from "./provider-models";
import type { ProviderModelOption } from "./provider-models";
import type { ProviderType } from "./types";

const contextWindowFormatter = new Intl.NumberFormat("en-US");

interface ProviderModelsEditorProps {
  idPrefix: string;
  providerType: ProviderType;
  endpoint: string;
  hasApiKey: boolean;
  modelOptions: ProviderModelOption[];
  isDiscoveryPending: boolean;
  fetchModels: () => void;
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
  hasApiKey,
  modelOptions,
  isDiscoveryPending,
  fetchModels,
  selectedModelNames,
  onSelectedModelNamesChange,
  isLoading,
  error,
  onRetry,
}: ProviderModelsEditorProps) {
  const discoverySupported = isProviderModelDiscoverySupported(providerType);
  const apiKeyRequired = isProviderModelDiscoveryApiKeyRequired(providerType);
  const visibleSelectedModelNames = React.useMemo(
    () => getVisibleSelectedModelNames(selectedModelNames, modelOptions),
    [modelOptions, selectedModelNames],
  );
  const selectedNames = React.useMemo(
    () => new Set(visibleSelectedModelNames),
    [visibleSelectedModelNames],
  );

  React.useEffect(() => {
    if (!isLoading && !error && visibleSelectedModelNames.length !== selectedModelNames.length) {
      onSelectedModelNamesChange(visibleSelectedModelNames);
    }
  }, [error, isLoading, onSelectedModelNamesChange, selectedModelNames, visibleSelectedModelNames]);

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
    !discoverySupported || !endpoint.trim() || (apiKeyRequired && !hasApiKey) || isDiscoveryPending;

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
                onClick={fetchModels}
              >
                <RefreshCw className={`mr-2 h-4 w-4 ${isDiscoveryPending ? "animate-spin" : ""}`} />
                {isDiscoveryPending ? "Fetching..." : "Fetch Models"}
              </Button>
            </span>
          </TooltipTrigger>
          <TooltipContent side="top">
            OpenAI APIs and Ollama are supported. OpenAI APIs require an ApiKey before fetching
            models.
          </TooltipContent>
        </Tooltip>
      </div>

      {!discoverySupported ? (
        <p className="text-xs text-muted-foreground">
          Model discovery is available only for OpenAI Chat Completions, OpenAI Responses, and
          Ollama.
        </p>
      ) : apiKeyRequired && !hasApiKey ? (
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
            {visibleSelectedModelNames.length} of {modelOptions.length} selected
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
                  {model.contextWindowTokens !== undefined ? (
                    <Badge variant="outline" className="shrink-0 font-normal tabular-nums">
                      {contextWindowFormatter.format(model.contextWindowTokens)} token context
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
