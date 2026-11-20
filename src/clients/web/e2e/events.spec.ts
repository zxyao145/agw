import { expect, test } from "@playwright/test";

import { agentId, executionSourceId, projectId, stubEventsApi, timerSourceId } from "./events-api";

test("creating a Webhook needs a name and fixed type and shows its address and secret", async ({
  page,
}) => {
  const api = await stubEventsApi(page);
  await page.goto("/events");
  await page.getByRole("tab", { name: "Sources" }).click();
  await page.getByRole("button", { name: "Add source" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Name").fill("demo-ticket");
  await dialog.getByLabel("Fixed type", { exact: true }).check();
  await dialog.getByLabel("Fixed event type").fill("demo.ticket.created");
  await dialog.getByRole("button", { name: "Save source" }).click();
  await expect(dialog.getByTestId("webhook-url")).toContainText(
    "/api/events/webhooks/receive?sourceId=",
  );
  await expect(dialog.getByTestId("webhook-secret")).toHaveText("one-time-webhook-secret");
  expect(api.writes.find((write) => write.path === "/api/events/sources/create")?.body).toEqual({
    name: "webhook.demo-ticket",
    kind: 1,
    config: { fixedType: "demo.ticket.created" },
    enabled: true,
  });
});

test("creating an HTTP poll converts the interval and Bearer Token", async ({ page }) => {
  const api = await stubEventsApi(page);
  await page.goto("/events");
  await page.getByRole("tab", { name: "Sources" }).click();
  await page.getByRole("button", { name: "Add source" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Source type").selectOption("2");
  await dialog.getByLabel("Name").fill("orders");
  await dialog
    .getByLabel("Event types")
    .pressSequentially("shop.order.created, shop.order.updated");
  await dialog.getByLabel("URL").fill("https://example.test/orders");
  await dialog.getByLabel("Poll interval", { exact: true }).fill("5");
  await dialog.getByLabel("Authentication").selectOption("bearer");
  await dialog.getByLabel("Bearer Token").fill("poll-secret");
  await dialog.getByRole("button", { name: "Save source" }).click();
  await expect(dialog).toHaveCount(0);
  const body = api.writes.find((write) => write.path === "/api/events/sources/create")?.body;
  expect(body?.name).toBe("http.orders");
  expect(body?.secret).toBe("poll-secret");
  expect(body?.config).toMatchObject({
    interval: "PT5M",
    eventTypes: ["shop.order.created", "shop.order.updated"],
  });
});

test("HTTP poll pagination follows the settings and path validation from the server", async ({
  page,
}) => {
  const api = await stubEventsApi(page);
  await page.goto("/events");
  await page.getByRole("tab", { name: "Sources" }).click();
  await page.getByRole("button", { name: "Add source" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Source type").selectOption("2");
  await dialog.getByLabel("Name").fill("orders");
  await dialog.getByLabel("Fixed type", { exact: true }).check();
  await dialog.getByLabel("Fixed event type").fill("shop.order.created");
  await dialog.getByLabel("URL").fill("https://example.test/orders");
  await dialog.getByText("Advanced settings", { exact: true }).click();
  await dialog.getByLabel("Response format").selectOption("mapped");
  await dialog
    .getByLabel("Mapping JSON", { exact: true })
    .fill('{"items":"$.items[*]","key":{"var":"item.id"}}');
  await dialog.getByLabel("Pagination", { exact: true }).selectOption("cursor");
  await dialog.getByLabel("Cursor parameter").fill("cursor");
  const save = dialog.getByRole("button", { name: "Save source" });

  await dialog.getByLabel("Cursor path").fill("invalid-path");
  await expect(dialog.getByRole("alert")).toHaveText("Enter a valid JSONPath for the cursor.");
  await expect(save).toBeDisabled();
  await dialog.getByLabel("Cursor path").fill("$.next");
  await dialog.getByLabel("Maximum pages").fill("5");
  await expect(dialog.getByRole("alert")).toHaveText("Maximum pages must be between 1 and 4.");
  await expect(save).toBeDisabled();
  expect(api.writes.some((write) => write.path === "/api/events/sources/create")).toBe(false);

  await dialog.getByLabel("Maximum pages").fill("4");
  await save.click();
  await expect(dialog).toHaveCount(0);
  expect(
    api.writes.find((write) => write.path === "/api/events/sources/create")?.body.config,
  ).toMatchObject({
    pagination: { kind: "cursor", maxPages: 4, cursorPath: "$.next", cursorParameter: "cursor" },
  });
});

test("subscription conditions remain editable after saving", async ({ page }) => {
  const api = await stubEventsApi(page);
  await page.goto("/events");
  await page.getByRole("tab", { name: "Subscriptions" }).click();
  await page.getByRole("button", { name: "Add subscription" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Name").fill("Completion review");
  const sources = dialog.getByRole("combobox", { name: "Sources", exact: true });
  await expect(sources).toHaveAttribute("aria-expanded", "false");
  await sources.click();
  const sourceList = page.getByRole("listbox", { name: "Sources" });
  const execution = sourceList.getByRole("option", { name: "agw.execution" });
  const timer = sourceList.getByRole("option", { name: "agw.timer.job-1-20261114" });
  await execution.click();
  await timer.click();
  await expect(timer).toHaveAttribute("aria-selected", "true");
  await timer.click();
  await expect(timer).toHaveAttribute("aria-selected", "false");
  await expect(execution).toHaveAttribute("aria-selected", "true");
  await timer.click();
  await page.getByLabel("Search sources").press("Escape");
  await expect(sources).toHaveText("2 selected");
  await dialog.getByLabel("Event types").fill("agw.execution.completed");
  await dialog.getByRole("button", { name: "Add type" }).click();
  await dialog.locator("#subscription-agent").click();
  await page.getByRole("option", { name: "Reviewer" }).click();
  await dialog.locator("#subscription-project").click();
  await page.getByRole("option", { name: "Review project" }).click();
  await dialog.getByText("Advanced settings").click();
  await dialog.getByRole("button", { name: "Add condition" }).click();
  const values = dialog.getByLabel("Condition 1 values");
  await values.fill("proj1");
  await values.press("Enter");
  await values.evaluate((input) => {
    const clipboardData = new DataTransfer();
    clipboardData.setData("text/plain", "proj2, obsolete");
    input.dispatchEvent(
      new ClipboardEvent("paste", { bubbles: true, cancelable: true, clipboardData }),
    );
  });
  const orOperators = dialog.getByRole("img", { name: "OR", exact: true });
  await expect(orOperators).toHaveCount(2);
  await dialog.getByRole("button", { name: "Remove value obsolete from condition 1" }).click();
  await expect(orOperators).toHaveCount(1);
  await dialog.getByRole("button", { name: "Add condition" }).click();
  await dialog.getByLabel("Condition 2 field").selectOption("subject");
  await dialog.getByLabel("Condition 2 values").fill("review");
  await expect(dialog.getByRole("img", { name: "AND", exact: true })).toBeVisible();
  await dialog.getByRole("button", { name: "Save subscription" }).click();
  await expect(dialog).toHaveCount(0);
  const body = api.writes.find((write) => write.path === "/api/events/subscriptions/create")?.body;
  expect(body).toMatchObject({
    sourceIds: [executionSourceId, timerSourceId],
    eventTypes: ["agw.execution.completed"],
    agentId,
    projectId,
    filter: {
      and: [
        { in: [{ var: "scope" }, ["proj1", "proj2"]] },
        { in: [{ var: "subject" }, ["review"]] },
      ],
    },
  });
  await page.getByRole("button", { name: "Edit" }).click();
  await expect(sources).toHaveText("2 selected");
  await sources.click();
  await expect(execution).toHaveAttribute("aria-selected", "true");
  await expect(timer).toHaveAttribute("aria-selected", "true");
  await page.getByLabel("Search sources").press("Escape");
  await dialog.getByText("Advanced settings").click();
  await expect(dialog.getByRole("img", { name: "AND", exact: true })).toBeVisible();
  await expect(orOperators).toHaveCount(1);
  await expect(dialog.getByLabel("Condition 1 field")).toHaveValue("scope");
  await expect(
    dialog.getByRole("button", { name: "Remove value proj1 from condition 1" }),
  ).toBeVisible();
  await expect(
    dialog.getByRole("button", { name: "Remove value proj2 from condition 1" }),
  ).toBeVisible();
  await expect(dialog.getByLabel("Condition 2 field")).toHaveValue("subject");
  await expect(
    dialog.getByRole("button", { name: "Remove value review from condition 2" }),
  ).toBeVisible();
});
