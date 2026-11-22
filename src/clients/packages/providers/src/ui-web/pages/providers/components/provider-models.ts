import type {
  OllamaContextWindows,
  ProviderAuthConfigRequest,
  ProviderModelDiscoveryResponse,
  ProviderType,
} from "./types";

export type ProviderModelOption = {
  id: string | null;
  name: string;
  isNew: boolean;
  contextWindowTokens?: number;
};

export function getProviderModelOptions(
  existingModels: ReadonlyArray<{ id: string; name: string }>,
  discovery: ProviderModelDiscoveryResponse | undefined,
  providerType: ProviderType,
): ProviderModelOption[] {
  return mergeProviderModelOptions(
    existingModels,
    discovery?.modelNames ?? [],
    providerType === "OllamaNative" ? (discovery?.ollamaContextWindows ?? undefined) : undefined,
  );
}

export function getVisibleSelectedModelNames(
  selectedModelNames: readonly string[],
  modelOptions: readonly ProviderModelOption[],
): string[] {
  const visibleModelNames = new Set(modelOptions.map((model) => model.name));
  return selectedModelNames.filter((modelName) => visibleModelNames.has(modelName));
}

export function mergeProviderModelOptions(
  existingModels: ReadonlyArray<{ id: string; name: string }>,
  discoveredModelNames: readonly string[],
  ollamaContextWindows?: Readonly<OllamaContextWindows>,
): ProviderModelOption[] {
  const optionByName = new Map<string, ProviderModelOption>();
  for (const model of existingModels) {
    optionByName.set(model.name, {
      id: model.id,
      name: model.name,
      isNew: false,
      ...getContextWindow(model.name, ollamaContextWindows),
    });
  }

  for (const rawName of discoveredModelNames) {
    const name = rawName.trim();
    if (!name || optionByName.has(name)) {
      continue;
    }

    optionByName.set(name, {
      id: null,
      name,
      isNew: true,
      ...getContextWindow(name, ollamaContextWindows),
    });
  }

  return [...optionByName.values()].sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0,
  );
}

export function getSelectedOllamaContextWindows(
  providerType: ProviderType,
  selectedModelNames: readonly string[],
  existingModels: ReadonlyArray<{ id: string; name: string }> | undefined,
  measuredContextWindows: Readonly<OllamaContextWindows>,
): OllamaContextWindows | undefined {
  if (providerType !== "OllamaNative" || !existingModels) {
    return undefined;
  }

  const existingModelNames = new Set(existingModels.map((model) => model.name));
  const selectedContextWindows: OllamaContextWindows = {};

  for (const modelName of new Set(selectedModelNames)) {
    const contextWindowTokens = measuredContextWindows[modelName];
    if (
      existingModelNames.has(modelName) ||
      typeof contextWindowTokens !== "number" ||
      !Number.isInteger(contextWindowTokens) ||
      contextWindowTokens <= 0
    ) {
      continue;
    }

    selectedContextWindows[modelName] = contextWindowTokens;
  }

  return Object.keys(selectedContextWindows).length > 0 ? selectedContextWindows : undefined;
}

export function mergeProviderModelDiscoveryResults(
  previous: ProviderModelDiscoveryResponse | undefined,
  next: ProviderModelDiscoveryResponse,
): ProviderModelDiscoveryResponse {
  return {
    ...next,
    modelNames: [...new Set([...(previous?.modelNames ?? []), ...next.modelNames])],
  };
}

function getContextWindow(
  modelName: string,
  ollamaContextWindows?: Readonly<OllamaContextWindows>,
): { contextWindowTokens?: number } {
  const contextWindowTokens = ollamaContextWindows?.[modelName];
  if (
    typeof contextWindowTokens === "number" &&
    Number.isInteger(contextWindowTokens) &&
    contextWindowTokens > 0
  ) {
    return { contextWindowTokens };
  }
  return {};
}

export function findDiscoveryApiKey(
  authConfigs: readonly ProviderAuthConfigRequest[],
): string | null {
  for (const config of authConfigs) {
    if (config.enable && config.authType === "ApiKey") {
      const apiKey = config.apiKey?.trim();
      if (apiKey) {
        return apiKey;
      }
    }
  }

  return null;
}

export function isProviderModelDiscoverySupported(providerType: ProviderType): boolean {
  return (
    providerType === "OpenAIChatCompletions" ||
    providerType === "OpenAIResponses" ||
    providerType === "OllamaNative"
  );
}

// Ollama 允许无认证访问；OpenAI 模型发现需要启用的 ApiKey。
// Ollama allows unauthenticated access; OpenAI model discovery needs an enabled ApiKey.
export function isProviderModelDiscoveryApiKeyRequired(providerType: ProviderType): boolean {
  return providerType !== "OllamaNative";
}
