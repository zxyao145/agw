import {
  apiDelete,
  apiGet,
  apiPost,
  apiPut,
  clearAntiforgeryToken,
  ApiError,
  getApiRuntime,
} from "@agw/api";

export type AuthSession = {
  authenticated: boolean;
  accessMode: "anonymous" | "localTrusted" | "cookie" | "bearer";
  apiMajorVersion: number;
  userId: string | null;
  displayName: string | null;
  loginProvider: string | null;
  isAdmin: boolean;
};

export const ADMIN_USER_ID = "1001";

export type ApiTokenSummary = {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
};

export type CreatedApiToken = ApiTokenSummary & { token: string };

export async function getAuthSession(): Promise<AuthSession> {
  const session = (await apiGet("/api/auth/session")) as AuthSession;
  return {
    ...session,
    displayName: session.displayName ?? null,
    loginProvider: session.loginProvider ?? null,
    isAdmin: session.isAdmin ?? (session.authenticated && session.userId === ADMIN_USER_ID),
  };
}

export async function login(password: string): Promise<void> {
  await apiPost("/api/auth/login", { body: { password } });
  clearAntiforgeryToken();
}

export async function logout(): Promise<void> {
  await apiPost("/api/auth/logout");
  clearAntiforgeryToken();
}

export async function listApiTokens(): Promise<ApiTokenSummary[]> {
  return (await apiGet("/api/auth/tokens")) as ApiTokenSummary[];
}

export async function createApiToken(name: string): Promise<CreatedApiToken> {
  return (await apiPost("/api/auth/tokens", { body: { name } })) as CreatedApiToken;
}

export async function revokeApiToken(id: string): Promise<void> {
  await apiDelete("/api/auth/tokens/{id}", { params: { path: { id } } });
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  await apiPut("/api/auth/password", { body: { currentPassword, newPassword } });
  clearAntiforgeryToken();
}

export type OidcProvider = { id: string; displayName: string; type: "Oidc" | "OAuth2" };

export async function getOidcProviders(): Promise<OidcProvider[]> {
  try {
    const providers = (await apiGet("/api/auth/oidc/providers")) as Array<{
      id: string;
      displayName: string;
      type?: string;
    }>;
    return providers.map((provider) => ({
      ...provider,
      type: provider.type === "OAuth2" ? "OAuth2" : "Oidc",
    }));
  } catch (error) {
    // Older Servers protect unknown routes before returning 404.
    if (error instanceof ApiError && (error.status === 404 || error.status === 401)) return [];
    throw error;
  }
}

export function oidcLoginUrl(providerId: string, returnUrl: string): string {
  const query = new URLSearchParams({ providerId, client: "web", returnUrl });
  return getApiRuntime().baseUrl + "/api/auth/oidc/login?" + query.toString();
}
