export type IntegrationCategory = number | null;

export type AppDefinitionItem = {
  name: string;
  displayName: string;
  category: number;
  provider: string;
  description: string;
  authUrl: string;
  scopes: string[];
  usePkce: boolean;
  tags: string[];
  toolNames: string[];
};

export type AppInstanceItem = {
  id: string;
  appName: string;
  displayName: string;
  provider: string;
  category: IntegrationCategory;
  usePkce: boolean;
  clientId: string;
  hasClientSecret: boolean;
  isAuthorized: boolean;
  isAuthorizationExpired: boolean;
  authorizationExpiresAtUtc: string | null;
  authorizationSubject: string | null;
  createTime: string;
  createBy: string | null;
  updateTime: string | null;
  updateBy: string | null;
};

export type AppInstanceCreateRequest = {
  appName: string;
  clientId: string;
  clientSecret: string;
  usePkce: boolean;
};

export type AuthorizeStartResponse = {
  authorizeUrl: string;
};

export type CreateConnectionFormState = {
  clientId: string;
  clientSecret: string;
  usePkce: boolean;
};

export type PendingOAuthSessionState = {
  appInstanceId: string;
  createdAt: string;
  integrationId: string;
  state: string;
};

export const integrationQueryKeys = {
  appDefinitions: ["integrations", "app-definitions"],
  appInstances: ["integrations", "app-instances"],
} as const;

export function createConnectionFormState(
  definition?: Pick<AppDefinitionItem, "usePkce"> | null,
): CreateConnectionFormState {
  return {
    clientId: "",
    clientSecret: "",
    usePkce: definition?.usePkce ?? true,
  };
}

export function formatIntegrationCategory(category: IntegrationCategory): string {
  switch (category) {
    case 0:
      return "Git server";
    case 1:
      return "WebDAV storage";
    case 2:
      return "Other";
    default:
      return "Integration";
  }
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return "Not available";
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(parsed);
}

export function getAuthorizationState(instance: AppInstanceItem): {
  label: string;
  variant: "default" | "secondary" | "destructive" | "outline";
} {
  if (instance.isAuthorizationExpired) {
    return { label: "Authorization expired", variant: "destructive" };
  }

  if (instance.isAuthorized) {
    return { label: "Authorized", variant: "default" };
  }

  return { label: "Not connected", variant: "outline" };
}

export function getPendingOAuthSessionStorageKey(appInstanceId: string): string {
  return `agw.oauth2.${appInstanceId}`;
}
