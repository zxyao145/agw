export type AppInstanceOption = {
  id: string;
  appName: string;
  displayName: string;
  provider: string;
  clientId: string;
  isAuthorized: boolean;
  isAuthorizationExpired: boolean;
  authorizationSubject?: string | null;
};

export function getAppAuthorizationState(
  app: Pick<AppInstanceOption, "isAuthorized" | "isAuthorizationExpired">,
): string {
  if (app.isAuthorizationExpired) {
    return "Expired";
  }

  if (app.isAuthorized) {
    return "Authorized";
  }

  return "Not authorized";
}

export function buildAppOptionLabel(
  app: Pick<AppInstanceOption, "displayName" | "clientId">,
): string {
  return `${app.displayName} · ${app.clientId}`;
}

export function filterAppOptions(
  options: readonly AppInstanceOption[],
  term: string,
): AppInstanceOption[] {
  const normalized = term.trim().toLowerCase();
  if (!normalized) {
    return [...options];
  }

  return options.filter((option) =>
    [option.displayName, option.provider, option.clientId, option.authorizationSubject ?? ""].some(
      (value) => value.toLowerCase().includes(normalized),
    ),
  );
}
