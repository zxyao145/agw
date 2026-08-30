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
  return (await apiGet("/api/auth/session" as never)) as unknown as AuthSession;
}

export async function login(password: string): Promise<void> {
  await apiPost("/api/auth/login" as never, { body: { password } } as never);
  clearAntiforgeryToken();
}

export async function logout(): Promise<void> {
  await apiPost("/api/auth/logout" as never);
  clearAntiforgeryToken();
}

export async function listApiTokens(): Promise<ApiTokenSummary[]> {
  return (await apiGet("/api/auth/tokens" as never)) as unknown as ApiTokenSummary[];
}

export async function createApiToken(name: string): Promise<CreatedApiToken> {
  return (await apiPost(
    "/api/auth/tokens" as never,
    { body: { name } } as never,
  )) as unknown as CreatedApiToken;
}

export async function revokeApiToken(id: string): Promise<void> {
  await apiDelete("/api/auth/tokens/{id}" as never, { params: { path: { id } } } as never);
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  await apiPut("/api/auth/password" as never, { body: { currentPassword, newPassword } } as never);
  clearAntiforgeryToken();
}
