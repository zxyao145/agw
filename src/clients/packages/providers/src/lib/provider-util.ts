export function getProviderTypeName(providerType: number): string {
  switch (providerType) {
    case 0:
      return "OpenAIChatCompletions";
    case 1:
      return "OpenAIResponses";
    case 2:
      return "Anthropic";
    default:
      return "Unknown";
  }
}
