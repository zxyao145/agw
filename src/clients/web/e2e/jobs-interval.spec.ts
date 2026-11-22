import { resolve } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { expect, type Locator, type Page } from "@playwright/test";

import { readApiResult as readResult, standaloneHostTest } from "./standalone-host";

const defaultProjectId = "11111111-1111-1111-1111-000000000001";
const agentName = "Interval Test Agent";
const storedJobName = "stored-interval";
const adjustmentNotice = "总时长必须是 30 秒的整数倍，最短为 30 秒";

type Job = { id: string; dueAt: string; instructions: string; schedule: { interval: string } };

standaloneHostTest(
  "JobInterval_Blur_AdjustsOnlyWhenNeededAndShowsOneToast",
  async ({ page, standaloneHost }) => {
    const dialog = await openCreateDialogWithAgent(page, standaloneHost.origin);
    const interval = await selectInterval(page, dialog);
    const instructions = dialog.getByLabel("Instructions", { exact: true });
    const notice = page.getByText(adjustmentNotice, { exact: true });
    const submit = dialog.getByRole("button", { name: "Create Job" });

    await expect(interval).toHaveValue("00:01:00");
    await interval.fill("00:00:30");
    await instructions.focus();
    await expect(interval).toHaveValue("00:00:30");
    await expect(notice).toHaveCount(0);

    await interval.fill("00:00:45");
    await instructions.focus();
    await expect(interval).toHaveValue("00:01:00");
    await expect(notice).toHaveCount(1);

    await expect(submit).toBeEnabled();
    await interval.fill("00:60:00");
    await expect(submit).toBeDisabled();
    await expect(
      dialog.getByText("Enter a duration in HH:mm:ss, for example 00:01:00."),
    ).toBeVisible();
  },
);

standaloneHostTest(
  "CreateJob_IntervalBetweenSteps_PersistsRoundedValueAndShowsOneToast",
  async ({ page, standaloneHost }) => {
    const dialog = await openCreateDialogWithAgent(page, standaloneHost.origin);
    const interval = await selectInterval(page, dialog);
    await interval.fill("01:02:03");

    const before = Date.now();
    const creation = waitForJobWrite(page, "/api/jobs/create");
    await dialog.getByRole("button", { name: "Create Job" }).click();
    const response = await creation;
    const job = await readResult<Job>(response);
    const after = Date.now();

    expect(response.request().postDataJSON().schedule).toEqual({
      kind: "Interval",
      interval: "PT1H2M",
    });
    expect(job.schedule.interval).toBe("PT1H2M");
    expect(Date.parse(job.dueAt)).toBeGreaterThanOrEqual(before + 3_720_000);
    expect(Date.parse(job.dueAt)).toBeLessThanOrEqual(after + 3_720_000);
    await expect(dialog).toBeHidden();
    await expect(page.getByText(adjustmentNotice, { exact: true })).toHaveCount(1);
  },
);

standaloneHostTest(
  "EditJob_InstructionsWithLegacyInterval_KeepsIntervalAndNextRun",
  async ({ page, standaloneHost }) => {
    const { root, origin } = standaloneHost;
    const original = await createIntervalJob(page, origin, "P1D");
    const legacyInterval = "PT45.5S";
    const database = new DatabaseSync(resolve(root, "agw.db"));
    try {
      database.exec("PRAGMA busy_timeout = 5000; BEGIN IMMEDIATE;");
      const source = database
        .prepare(
          "UPDATE event_source SET config_json = json_set(config_json, '$.interval', ?) WHERE managed_key = ?",
        )
        .run(legacyInterval, original.id);
      const schedule = database
        .prepare(
          `UPDATE event_schedule SET interval = ? WHERE business_key = (
             SELECT 'source:' || lower(id) FROM event_source WHERE managed_key = ?
           )`,
        )
        .run("00:00:45.5000000", original.id);
      expect(source.changes).toBe(1);
      expect(schedule.changes).toBe(1);
      database.exec("COMMIT");
    } finally {
      database.close();
    }

    const dialog = await openStoredJobDialog(page);
    await expect(dialog.getByLabel("Interval", { exact: true })).toHaveValue(legacyInterval);
    await dialog.getByLabel("Instructions", { exact: true }).fill("Changed instructions");
    const update = waitForJobWrite(page, "/api/jobs/update");
    await dialog.getByRole("button", { name: "Save Changes" }).click();
    const response = await update;
    await readResult<Job>(response);

    expect(response.request().postDataJSON().schedule.interval).toBe(legacyInterval);
    const saved = await readResult<Job>(
      await page.request.get(`${origin}/api/jobs/get?id=${original.id}`),
    );
    expect(saved.schedule.interval).toBe(legacyInterval);
    expect(saved.instructions).toBe("Changed instructions");
    expect(saved.dueAt).toBe(original.dueAt);
    await expect(page.getByText(adjustmentNotice, { exact: true })).toHaveCount(0);
  },
);

standaloneHostTest(
  "EditJob_RestoredInterval_KeepsIntervalAndNextRun",
  async ({ page, standaloneHost }) => {
    const original = await createIntervalJob(page, standaloneHost.origin, "PT90S");
    const dialog = await openStoredJobDialog(page);
    const interval = dialog.getByLabel("Interval", { exact: true });
    await expect(interval).toHaveValue("00:01:30");
    await dialog.getByLabel("Instructions", { exact: true }).fill("Changed instructions");
    await interval.fill("00:02:00");
    await interval.fill("00:01:30");

    const update = waitForJobWrite(page, "/api/jobs/update");
    await dialog.getByRole("button", { name: "Save Changes" }).click();
    const response = await update;
    const saved = await readResult<Job>(response);

    expect(response.request().postDataJSON().schedule.interval).toBe("PT90S");
    expect(saved.schedule.interval).toBe("PT90S");
    expect(saved.instructions).toBe("Changed instructions");
    expect(saved.dueAt).toBe(original.dueAt);
    await expect(page.getByText(adjustmentNotice, { exact: true })).toHaveCount(0);
  },
);

async function createIntervalJob(page: Page, origin: string, interval: string) {
  const { agentId, requestToken } = await createAgent(page, origin);
  const response = await page.request.post(`${origin}/api/jobs/create`, {
    headers: { "X-CSRF-TOKEN": requestToken },
    data: {
      name: storedJobName,
      projectId: defaultProjectId,
      targetKind: 0,
      agentId,
      instructions: "Original instructions",
      schedule: { kind: "Interval", interval },
    },
  });
  return readResult<Job>(response);
}

async function openStoredJobDialog(page: Page) {
  await page.goto("/jobs");
  const row = page.getByRole("row").filter({ hasText: storedJobName });
  await row.locator("button:has(svg.lucide-pencil)").click();
  return page.getByRole("dialog", { name: "Edit Job" });
}

async function openCreateDialogWithAgent(page: Page, origin: string) {
  await createAgent(page, origin);
  await page.goto("/jobs");
  await page.getByRole("button", { name: "Add Job" }).click();
  const dialog = page.getByRole("dialog", { name: "Create Job" });
  await dialog.locator("#create-agent-id").click();
  await page.getByText(agentName, { exact: true }).click();
  await dialog.getByLabel("Instructions", { exact: true }).fill("Run the interval job.");
  return dialog;
}

async function selectInterval(page: Page, dialog: Locator) {
  await dialog.getByLabel("Schedule Type").click();
  await page.getByRole("option", { name: "Interval", exact: true }).click();
  return dialog.getByLabel("Interval", { exact: true });
}

async function createAgent(page: Page, origin: string) {
  const { requestToken } = await readResult<{ requestToken: string }>(
    await page.request.get(`${origin}/api/auth/antiforgery`),
  );
  const agent = await readResult<{ id: string }>(
    await page.request.post(`${origin}/api/agents`, {
      headers: { "X-CSRF-TOKEN": requestToken },
      data: {
        name: "interval-test-agent",
        displayName: agentName,
        description: "Interval test",
        systemPrompt: "Interval test",
        type: 1,
        externalAgentKind: 2,
      },
    }),
  );
  return { agentId: agent.id, requestToken };
}

function waitForJobWrite(page: Page, path: string) {
  return page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === path && response.request().method() === "POST",
  );
}
