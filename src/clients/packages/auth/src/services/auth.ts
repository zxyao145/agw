import { apiDelete, apiGet, apiPost, apiPut, clearAntiforgeryToken } from "@agw/api";

export type AuthSession = {
  authenticated: boolean;
  accessMode: "anonymous" | "localTrusted" | "cookie" | "bearer";
  apiMajorVersion: number;
  userId: string | null;
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
  // These Auth responses do not yet declare a response body in OpenAPI.
  return (await apiGet("/api/auth/session")) as AuthSession;
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
