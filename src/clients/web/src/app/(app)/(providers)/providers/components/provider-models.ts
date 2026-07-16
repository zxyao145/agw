import type { ProviderAuthConfigRequest, ProviderType } from "./types";

export type ProviderModelOption = {
  id: string | null;
  name: string;
  isNew: boolean;
};

export function mergeProviderModelOptions(
  existingModels: ReadonlyArray<{ id: string; name: string }>,
  discoveredModelNames: readonly string[],
): ProviderModelOption[] {
  const optionByName = new Map<string, ProviderModelOption>();
  for (const model of existingModels) {
    optionByName.set(model.name, {
      id: model.id,
      name: model.name,
      isNew: false,
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
    });
  }

  return [...optionByName.values()].sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0,
  );
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
  return providerType === "OpenAIChatCompletions" || providerType === "OpenAIResponses";
}
