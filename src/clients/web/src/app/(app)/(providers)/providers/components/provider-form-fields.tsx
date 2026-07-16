"use client";

import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";

import { ProviderAuthConfigEditor } from "./provider-auth-config-editor";
import { ProviderModelsEditor } from "./provider-models-editor";
import type { ProviderAuthConfigRequest, ProviderModelDto, ProviderType } from "./types";

const providerTypeOptions: ProviderType[] = [
  "OpenAIChatCompletions",
  "OpenAIResponses",
  "Anthropic",
];

interface ProviderFormFieldsProps {
  idPrefix: string;
  name: string;
  setName: (value: string) => void;
  endpoint: string;
  setEndpoint: (value: string) => void;
  providerType: ProviderType;
  setProviderType: (value: ProviderType) => void;
  description: string;
  setDescription: (value: string) => void;
  authConfigs: ProviderAuthConfigRequest[];
  setAuthConfigs: (value: ProviderAuthConfigRequest[]) => void;
  models: ProviderModelDto[];
  selectedModelNames: string[];
  setSelectedModelNames: (value: string[]) => void;
  modelsLoading: boolean;
  modelsError: unknown;
  retryModels: () => void;
}

export function ProviderFormFields({
  idPrefix,
  name,
  setName,
  endpoint,
  setEndpoint,
  providerType,
  setProviderType,
  description,
  setDescription,
  authConfigs,
  setAuthConfigs,
  models,
  selectedModelNames,
  setSelectedModelNames,
  modelsLoading,
  modelsError,
  retryModels,
}: ProviderFormFieldsProps) {
  return (
    <div className="grid min-h-0 flex-1 grid-rows-[minmax(0,45%)_minmax(0,1fr)] overflow-hidden border-t lg:grid-cols-[400px_minmax(0,1fr)] lg:grid-rows-1">
      <div className="overflow-y-auto border-b bg-muted/20 p-6 lg:border-r lg:border-b-0">
        <div className="grid gap-5">
          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}name`}>Name</Label>
            <Input
              id={`${idPrefix}name`}
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="openai"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}endpoint`}>Endpoint</Label>
            <Input
              id={`${idPrefix}endpoint`}
              value={endpoint}
              onChange={(event) => setEndpoint(event.target.value)}
              placeholder="https://api.openai.com/v1"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor={`${idPrefix}providerType`}>Provider type</Label>
            <Select
              value={providerType}
              onValueChange={(value) => setProviderType(value as ProviderType)}
            >
              <SelectTrigger id={`${idPrefix}providerType`} className="w-full">
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
            <Label htmlFor={`${idPrefix}description`}>Description</Label>
            <Textarea
              id={`${idPrefix}description`}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              rows={5}
              placeholder="Describe this provider..."
            />
          </div>
        </div>
      </div>

      <div className="min-h-0 overflow-hidden bg-background">
        <Tabs defaultValue="auth-configs" className="flex h-full min-h-0 flex-col">
          <div className="shrink-0 border-b px-6 py-3">
            <TabsList className="h-auto">
              <TabsTrigger value="auth-configs">Auth Configs</TabsTrigger>
              <TabsTrigger value="models">Models</TabsTrigger>
            </TabsList>
          </div>

          <TabsContent
            forceMount
            value="auth-configs"
            className="m-0 min-h-0 flex-1 overflow-y-auto p-6 data-[state=inactive]:hidden"
          >
            <ProviderAuthConfigEditor value={authConfigs} onChange={setAuthConfigs} />
          </TabsContent>

          <TabsContent
            forceMount
            value="models"
            className="m-0 min-h-0 flex-1 overflow-y-auto p-6 data-[state=inactive]:hidden"
          >
            <ProviderModelsEditor
              idPrefix={idPrefix}
              providerType={providerType}
              endpoint={endpoint}
              authConfigs={authConfigs}
              models={models}
              selectedModelNames={selectedModelNames}
              onSelectedModelNamesChange={setSelectedModelNames}
              isLoading={modelsLoading}
              error={modelsError}
              onRetry={retryModels}
            />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}
