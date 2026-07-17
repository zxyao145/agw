import type { FormField, SecretFieldState } from "./form-state";

export type AuthSchemeType = "OAuth2" | "ApiKey" | "AkSk";
export type ConnectionStatus =
  | "NeedsConfiguration"
  | "PendingAuthorization"
  | "Unverified"
  | "Ready"
  | "Expired"
  | "Invalid"
  | "Disabled"
  | "DefinitionUnavailable";

export type PluginInstallation = {
  id: string;
  enabled: boolean;
  configuration: Record<string, string | null>;
  secrets: Record<string, SecretFieldState>;
};

export type OAuthDefinition = {
  authorizationEndpoint: string;
  scopes: string[];
  usePkce: boolean;
  supportsRefresh: boolean;
};

export type AuthSchemeDefinition = {
  id: string;
  displayName: string;
  type: AuthSchemeType;
  oauth2AuthorizationCode?: OAuthDefinition | null;
  installationFields: FormField[];
  connectionFields: FormField[];
  installation?: PluginInstallation | null;
};

export type ConnectorDefinition = {
  id: string;
  displayName: string;
  description?: string | null;
  authSchemes: AuthSchemeDefinition[];
  capabilitySources: Array<{
    id: string;
    kind: "Native" | "Mcp";
    provider?: string | null;
  }>;
};

export type PluginDefinition = {
  id: string;
  version: string;
  displayName: string;
  description?: string | null;
  tags: string[];
  connectors: ConnectorDefinition[];
  skills: Array<{ id: string; description: string }>;
};

export type Connection = {
  id: string;
  pluginId: string;
  connectorId: string;
  authSchemeId: string;
  displayName: string;
  alias: string;
  enabled: boolean;
  status: ConnectionStatus;
  subject?: string | null;
  expiresAtUtc?: string | null;
  lastValidatedAtUtc?: string | null;
  lastValidationErrorCode?: string | null;
  configuration: Record<string, string | null>;
  secrets: Record<string, SecretFieldState>;
};

export type IntegrationSelection = {
  plugin: PluginDefinition;
  connector: ConnectorDefinition;
  authScheme: AuthSchemeDefinition;
};

export const integrationQueryKeys = {
  plugins: ["integrations", "plugins"] as const,
  connections: ["integrations", "connections"] as const,
};

export function findIntegrationSelection(
  plugins: readonly PluginDefinition[],
  connection: Pick<Connection, "pluginId" | "connectorId" | "authSchemeId">,
): IntegrationSelection | null {
  const plugin = plugins.find((candidate) => candidate.id === connection.pluginId);
  const connector = plugin?.connectors.find((candidate) => candidate.id === connection.connectorId);
  const authScheme = connector?.authSchemes.find(
    (candidate) => candidate.id === connection.authSchemeId,
  );
  return plugin && connector && authScheme ? { plugin, connector, authScheme } : null;
}

export function connectionStatusPresentation(status: ConnectionStatus): {
  label: string;
  variant: "default" | "secondary" | "destructive" | "outline";
} {
  switch (status) {
    case "Ready":
      return { label: "Ready", variant: "default" };
    case "Expired":
    case "Invalid":
      return { label: status, variant: "destructive" };
    case "Disabled":
    case "DefinitionUnavailable":
      return {
        label: status === "DefinitionUnavailable" ? "Definition unavailable" : status,
        variant: "secondary",
      };
    default:
      return {
        label: status.replace(/([a-z])([A-Z])/g, "$1 $2"),
        variant: "outline",
      };
  }
}
