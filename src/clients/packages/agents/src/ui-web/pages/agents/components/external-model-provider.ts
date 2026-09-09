import type { ExternalAgentKind, ExternalAgentOptionDto, ModelProviderDto } from "./types";

export function getExternalModelProviderError(
  kind: ExternalAgentKind,
  modelProviderId: string,
  modelProviders: ModelProviderDto[] | undefined,
  externalOptions: ExternalAgentOptionDto[] | undefined,
): string | null {
  if (!modelProviderId) return null;
  if (!modelProviders || !externalOptions) return "Loading model provider compatibility...";
  const modelProvider = modelProviders.find((provider) => provider.id === modelProviderId);
  const option = externalOptions.find((option) => option.kind === kind);
  if (!modelProvider)
    return "This model provider is unavailable. Select another or clear the selection.";
  if (!option?.supportedProviderTypes?.includes(modelProvider.providerType)) {
    return "This model provider is incompatible with the external agent. Select another or clear the selection.";
  }
  return null;
}
