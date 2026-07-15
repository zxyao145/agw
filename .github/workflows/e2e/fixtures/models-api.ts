import type { Page, Route } from "@playwright/test";

export type MockModel = {
  id: string;
  name: string;
  description: string | null;
  maxTokens: number;
  createTime: string;
};

type ModelsApiOptions = {
  createFailure?: boolean;
  initialModels?: MockModel[];
  getDelayMs?: number;
};

function fulfillJson(route: Route, data: unknown, status = 200) {
  return route.fulfill({
    status,
    contentType: "application/json",
    body: JSON.stringify(data),
  });
}

function result(data?: unknown) {
  return { code: 200, title: "OK", data };
}

export async function installModelsApi(page: Page, options: ModelsApiOptions = {}) {
  let models: MockModel[] = options.initialModels ?? [
    {
      id: "model-1",
      name: "gpt-4o-mini",
      description: "Fast general model",
      maxTokens: 4096,
      createTime: "2026-07-14T10:00:00Z",
    },
  ];

  await page.route("**/api/auth/session", (route) =>
    fulfillJson(route, result({ authenticated: true, accessMode: "cookie", apiMajorVersion: 1 })),
  );

  await page.route("**/api/auth/antiforgery", (route) =>
    fulfillJson(route, result({ requestToken: "e2e-token" })),
  );

  await page.route("**/api/models**", async (route) => {
    const request = route.request();
    const url = new URL(request.url());

    if (request.method() === "GET" && url.pathname === "/api/models") {
      if (options.getDelayMs) {
        await new Promise((resolve) => setTimeout(resolve, options.getDelayMs));
      }
      return fulfillJson(route, result(models));
    }

    if (request.method() === "POST" && url.pathname === "/api/models") {
      if (options.createFailure) {
        return fulfillJson(
          route,
          { code: 4000001, title: "Validation failed", detail: "Model name is already used." },
          400,
        );
      }

      const body = request.postDataJSON() as {
        name: string;
        description: string | null;
        maxTokens: number;
      };
      models = [
        ...models,
        {
          id: "model-created",
          ...body,
          createTime: "2026-07-14T11:00:00Z",
        },
      ];
      return fulfillJson(route, result());
    }

    if (request.method() === "DELETE" && url.pathname.startsWith("/api/models/")) {
      const id = decodeURIComponent(url.pathname.slice("/api/models/".length));
      models = models.filter((model) => model.id !== id);
      return fulfillJson(route, result());
    }

    return route.fallback();
  });
}
