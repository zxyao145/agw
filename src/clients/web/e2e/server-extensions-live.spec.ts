import { expect, test, type Page } from "@playwright/test";
import { readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";

type Credentials = {
  adminPassword: string;
  extensionId: string;
  extensionName: string;
  extensionDisplayName: string;
};
type ExtensionStatus = { id: string; currentVersion: string; isEnabled: boolean; state: string };
const scratchDirectory = resolve(__dirname, "../../../../.scratch/server-extension-web-e2e");

async function readStatus(page: Page): Promise<ExtensionStatus> {
  return page.evaluate(async () => {
    const response = await fetch("/api/extensions", { cache: "no-store" });
    if (!response.ok) throw new Error(`Extension list returned HTTP ${response.status}.`);
    const envelope = (await response.json()) as { code: number; data: ExtensionStatus[] };
    if (envelope.code !== 0 || envelope.data.length !== 1)
      throw new Error(`Unexpected extension list: ${JSON.stringify(envelope)}`);
    return envelope.data[0]!;
  });
}

async function readSkillIds(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    const response = await fetch("/api/skills", { cache: "no-store" });
    if (!response.ok) throw new Error(`Skill list returned HTTP ${response.status}.`);
    const envelope = (await response.json()) as { code: number; data: { id: string }[] };
    if (envelope.code !== 0) throw new Error(`Unexpected Skill list: ${JSON.stringify(envelope)}`);
    return envelope.data.map((skill) => skill.id).sort();
  });
}

async function toggle(page: Page, credentials: Credentials, enabled: boolean) {
  const kind = enabled ? "enable" : "disable";
  const requestPromise = page.waitForRequest(
    (request) =>
      new URL(request.url()).pathname === `/api/extensions/${kind}` && request.method() === "POST",
  );
  const responsePromise = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === `/api/extensions/${kind}` &&
      response.request().method() === "POST",
  );
  await page
    .getByRole("button", {
      name: `${enabled ? "Enable" : "Disable"} ${credentials.extensionDisplayName}`,
      exact: true,
    })
    .click();
  const request = await requestPromise;
  expect(request.postDataJSON()).toEqual({ extensionId: credentials.extensionId });
  expect(new URL(request.url()).origin).toBe("http://127.0.0.1:3102");
  expect((await responsePromise).status()).toBe(200);
  await expect
    .poll(async () => (await readStatus(page)).state)
    .toBe(enabled ? "Active" : "Disabled");
  expect((await readStatus(page)).isEnabled).toBe(enabled);
  await expect(
    page.getByRole("button", {
      name: `${enabled ? "Disable" : "Enable"} ${credentials.extensionDisplayName}`,
      exact: true,
    }),
  ).toBeEnabled();
}

test("admin enables startup-discovered extensions through the real Web app and Host", async ({
  page,
}) => {
  const credentials = JSON.parse(
    await readFile(join(scratchDirectory, "credentials.json"), "utf8"),
  ) as Credentials;
  await page.goto("/extensions");
  await expect(page).toHaveURL(/\/login\/?\?returnUrl=/u);
  await page.getByLabel("Administrator password").fill(credentials.adminPassword);
  const loginResponse = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/auth/login" &&
      response.request().method() === "POST",
  );
  await page.getByRole("button", { name: "Continue", exact: true }).click();
  expect((await loginResponse).status()).toBe(200);
  await expect(page).toHaveURL(/\/extensions\/?$/u);
  await expect(page.getByRole("heading", { name: "Server extensions" })).toBeVisible();
  await expect(
    page.getByRole("complementary").getByRole("link", { name: "Server extensions", exact: true }),
  ).toHaveAttribute("href", "/extensions");
  const row = page.getByRole("row").filter({ hasText: credentials.extensionDisplayName });
  await expect(row).toBeVisible();
  await expect(row).toContainText("1.0.0");
  await expect(row).toContainText("Server");
  await expect(row).toContainText("Disabled");
  expect(await readStatus(page)).toMatchObject({
    id: credentials.extensionId,
    currentVersion: "1.0.0",
    isEnabled: false,
    state: "Disabled",
  });

  const skillIds = await readSkillIds(page);
  await toggle(page, credentials, true);
  expect(await readSkillIds(page)).toEqual(skillIds);
  await expect(row.getByText("Active", { exact: true })).toBeVisible();
  await page.reload();
  await expect(row.getByText("Active", { exact: true })).toBeVisible();

  const manifestPath = join(
    scratchDirectory,
    "Extensions",
    credentials.extensionName,
    "manifest.json",
  );
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as { version: string };
  manifest.version = "1.0.1";
  await writeFile(manifestPath, `${JSON.stringify(manifest)}\n`);
  await toggle(page, credentials, false);
  await toggle(page, credentials, true);
  await expect(row).toContainText("1.0.0");
  expect((await readStatus(page)).currentVersion).toBe("1.0.0");
  await toggle(page, credentials, false);
  await page.reload();
  await expect(row).toContainText("Disabled");
  expect((await readStatus(page)).isEnabled).toBe(false);
  expect(await readSkillIds(page)).toEqual(skillIds);
});
