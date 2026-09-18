import { expect, test, type Page, type Route } from "@playwright/test";

const agentflow = { id: "flow-1", name: "Review flow", enable: true, description: "" };

async function mockApi(page: Page, handleWrite?: (route: Route) => Promise<void>) {
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() !== "GET") {
      if (handleWrite) return handleWrite(route);
      throw new Error(`Unexpected mutation: ${request.method()} ${path}`);
    }
    const responses: Record<string, unknown> = {
      "/api/auth/session": {
        authenticated: true,
        accessMode: "cookie",
        apiMajorVersion: 1,
        userId: "1001",
      },
      "/api/auth/antiforgery": { requestToken: "test-csrf" },
      "/api/auth/oidc/providers": [],
      "/api/agentflows/paged": { items: [agentflow], total: 1 },
      "/api/agentflows": [agentflow],
      "/api/agents": [{ id: "agent-1", name: "Reviewer", enable: true }],
      "/api/model-providers": [],
      "/api/agentflows/flow-1/nodes": [
        { nodeId: "input", kind: 10, name: "Input", relateId: null, positionJson: '{"x":0,"y":0}' },
        {
          nodeId: "agent",
          kind: 0,
          name: "Reviewer",
          relateId: "agent-1",
          positionJson: '{"x":300,"y":0}',
        },
      ],
      "/api/agentflows/flow-1/edges": [
        { edgeId: "edge-1", sourceNodeId: "input", targetNodeId: "agent", kind: 0 },
      ],
    };
    if (!(path in responses)) throw new Error(`Unexpected query: ${path}`);
    await route.fulfill({ json: { code: 0, title: "OK", data: responses[path] } });
  });
}

test("login failure stays editable and a successful retry returns to the requested page", async ({
  page,
}) => {
  let attempts = 0;
  await mockApi(page, async (route) => {
    expect(new URL(route.request().url()).pathname).toBe("/api/auth/login");
    expect(route.request().headers()["x-csrf-token"]).toBe("test-csrf");
    expect(route.request().postDataJSON()).toEqual({
      password: attempts === 0 ? "incorrect" : "correct",
    });
    attempts += 1;
    await route.fulfill(
      attempts === 1
        ? { status: 401, json: { code: 4010001, title: "Unauthorized" } }
        : { json: { code: 0, title: "OK" } },
    );
  });
  await page.goto("/login/?returnUrl=%2Fagentflows%2F");
  await page.getByLabel("Administrator password").fill("incorrect");
  await page.getByRole("button", { name: "Continue", exact: true }).click();
  await expect(page.getByText("The administrator password was not accepted.")).toBeVisible();
  await page.getByLabel("Administrator password").fill("correct");
  await page.getByRole("button", { name: "Continue", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Agentflows", exact: true })).toBeVisible();
  expect(attempts).toBe(2);
});

test("Agentflow saving freezes edits, failed writes preserve the draft, and retry closes on success", async ({
  page,
}) => {
  let release!: () => void;
  let started!: () => void;
  const responseReady = new Promise<void>((resolve) => {
    release = resolve;
  });
  const requestStarted = new Promise<void>((resolve) => {
    started = resolve;
  });
  let writes = 0;
  await mockApi(page, async (route) => {
    expect(route.request().method()).toBe("PUT");
    expect(new URL(route.request().url()).pathname).toBe("/api/agentflows/flow-1");
    expect(route.request().postDataJSON().name).toBe("Submitted draft");
    writes += 1;
    if (writes === 1) {
      started();
      await responseReady;
      await route.fulfill({ status: 503, json: { code: 5030001, title: "Unavailable" } });
    } else {
      await route.fulfill({ json: { code: 0, title: "OK" } });
    }
  });
  await page.goto("/agentflows");
  await page.getByTitle("Edit agentflow", { exact: true }).click();
  const dialog = page.getByRole("dialog");
  const name = page.locator("#agentflowName");
  await name.fill("Submitted draft");
  await dialog.getByRole("button", { name: "Update", exact: true }).click();
  await requestStarted;
  try {
    await name.click({ force: true });
    await page.keyboard.type("Unsent edit");
    await expect(name).toHaveValue("Submitted draft");
    await expect(dialog.getByRole("button", { name: "Updating...", exact: true })).toBeDisabled();
  } finally {
    release();
  }
  await expect(dialog.getByRole("button", { name: "Update", exact: true })).toBeEnabled();
  await expect(name).toHaveValue("Submitted draft");
  await expect(dialog.getByText("Unsaved changes", { exact: true })).toBeVisible();
  await dialog.getByRole("button", { name: "Update", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(writes).toBe(2);
});
