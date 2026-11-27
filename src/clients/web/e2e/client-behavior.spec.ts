import { expect, test, type Page, type Route } from "@playwright/test";
import { fulfillApiData, fulfillShellRead } from "./web-session-api";
import {
  agentflowTest,
  createFlow,
  flowNameInput,
  openEditor,
  sequentialDefinition,
} from "./agentflow-editor-fixture";

const agentflow = {
  id: "flow-1",
  name: "Review flow",
  enable: true,
  description: "",
  latestRevision: 1,
  publishedRevision: 0,
};

async function mockApi(page: Page, handleWrite?: (route: Route) => Promise<void>) {
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() !== "GET") {
      if (handleWrite) return handleWrite(route);
      throw new Error(`Unexpected mutation: ${request.method()} ${path}`);
    }
    const shellResponse = fulfillShellRead(route, path);
    if (shellResponse) return shellResponse;
    const responses: Record<string, unknown> = {
      "/api/auth/oidc/providers": [],
      "/api/agentflows/paged": { items: [agentflow], total: 1 },
      "/api/agentflows": [agentflow],
      "/api/agents": [{ id: "agent-1", name: "Reviewer", enable: true }],
      "/api/model-providers": [],
    };
    if (!(path in responses)) throw new Error(`Unexpected query: ${path}`);
    return fulfillApiData(route, responses[path]);
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
  await page.waitForURL(/\/agentflows\/?$/u, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("heading", { name: "Agentflows", exact: true })).toBeVisible();
  expect(attempts).toBe(2);
});

agentflowTest(
  "Agentflow saving freezes edits, a failed write keeps the draft, and a retry shows the new version",
  async ({ page, standaloneHost, requestToken, agentIds }) => {
    const flow = await createFlow(
      page,
      standaloneHost.origin,
      requestToken,
      "Review flow",
      sequentialDefinition(agentIds[0]),
    );
    let release!: () => void;
    let started!: () => void;
    const responseReady = new Promise<void>((resolve) => {
      release = resolve;
    });
    const requestStarted = new Promise<void>((resolve) => {
      started = resolve;
    });
    let writes = 0;
    await page.route("**/api/agentflows/revisions", async (route) => {
      if (route.request().method() !== "POST") return route.fallback();
      expect(route.request().postDataJSON()).toMatchObject({
        name: "Submitted draft",
        baseRevision: 1,
      });
      writes += 1;
      if (writes === 1) {
        started();
        await responseReady;
        await route.abort("connectionreset");
      } else {
        await route.fallback();
      }
    });
    const dialog = await openEditor(page, flow.name);
    const name = flowNameInput(dialog);
    const save = dialog.getByRole("button", { name: "Save", exact: true });
    await name.fill("Submitted draft");
    await save.click();
    await requestStarted;
    try {
      await expect(name).toBeDisabled();
      await expect(name).toHaveValue("Submitted draft");
      await expect(dialog.getByRole("button", { name: "Saving…", exact: true })).toBeDisabled();
    } finally {
      release();
    }
    await expect(save).toBeEnabled();
    await expect(name).toHaveValue("Submitted draft");
    await expect(dialog.getByText("Unsaved changes", { exact: true })).toBeVisible();
    await save.click();
    const header = dialog.locator('[data-slot="dialog-header"]');
    await expect(header.getByText("v2", { exact: true })).toBeVisible();
    await expect(dialog).toBeVisible();
    expect(writes).toBe(2);
  },
);
