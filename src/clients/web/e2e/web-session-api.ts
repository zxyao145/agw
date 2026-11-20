import type { Route } from "@playwright/test";

const shellResponses: Record<string, unknown> = {
  "/api/auth/session": {
    authenticated: true,
    accessMode: "cookie",
    apiMajorVersion: 1,
    userId: "1001",
  },
  "/api/auth/antiforgery": { requestToken: "test-csrf" },
  "/api/notifications/list": { items: [], total: 0, pageIndex: 1, pageSize: 20 },
};

export function fulfillApiData(route: Route, data: unknown): Promise<void> {
  return route.fulfill({ json: { code: 0, title: "OK", data } });
}

export function fulfillShellRead(route: Route, path: string): Promise<void> | undefined {
  if (path === "/api/notifications/stream") return route.fulfill({ status: 204 });
  return Object.hasOwn(shellResponses, path)
    ? fulfillApiData(route, shellResponses[path])
    : undefined;
}
