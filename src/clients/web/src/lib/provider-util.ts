export function getProviderTypeName(providerType: number): string {
  switch (providerType) {
    case 0:
      return "Azure";
    case 1:
      return "Anthropic";
    default:
      return "Unknown";
  }
}
