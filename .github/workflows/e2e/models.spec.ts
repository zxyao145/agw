import { expect, test } from "@playwright/test";

import { installModelsApi } from "./fixtures/models-api";

test("shows loading and empty states", async ({ page }) => {
  await installModelsApi(page, { initialModels: [], getDelayMs: 200 });
  await page.goto("/models");

  await expect(page.getByText("Loading...")).toBeVisible();
  await expect(page.getByText("No models found. Create one to get started.")).toBeVisible();
});

test("cancels the create dialog with Escape and restores focus", async ({ page }) => {
  await installModelsApi(page);
  await page.goto("/models");

  const trigger = page.getByRole("button", { name: "Create model" });
  await trigger.click();
  const createDialog = page.getByRole("dialog", { name: "Create model" });
  await expect(createDialog.getByLabel("Name")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(createDialog).toBeHidden();
  await expect(trigger).toBeFocused();

  await trigger.click();
  await createDialog.getByRole("button", { name: "Cancel" }).click();
  await expect(createDialog).toBeHidden();
});

test("creates and deletes a model through accessible overlays", async ({ page }) => {
  await installModelsApi(page);
  await page.goto("/models");

  await expect(page.getByRole("heading", { name: "Models" })).toBeVisible();
  await page.getByRole("button", { name: "Create model" }).click();

  const createDialog = page.getByRole("dialog", { name: "Create model" });
  await expect(createDialog).toBeVisible();
  await expect(createDialog.getByRole("button", { name: "Create" })).toBeDisabled();
  await createDialog.getByLabel("Max tokens").fill("not-a-number");
  await expect(createDialog.getByText("Enter a valid integer.")).toBeVisible();
  await createDialog.getByLabel("Name").fill("gpt-4.1-mini");
  await createDialog.getByLabel("Max tokens").fill("8192");
  await createDialog.getByLabel("Description").fill("New compact model");
  await createDialog.getByRole("button", { name: "Create" }).click();

  await expect(createDialog).toBeHidden();
  await expect(page.getByText("Model created")).toBeVisible();
  await expect(page.getByRole("row", { name: /gpt-4\.1-mini/ })).toBeVisible();

  await page.getByRole("button", { name: "Delete gpt-4.1-mini" }).click();
  const deleteDialog = page.getByRole("alertdialog", { name: "Delete model" });
  await expect(deleteDialog).toContainText("This action cannot be undone");
  await deleteDialog.getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByRole("row", { name: /gpt-4\.1-mini/ })).toBeVisible();

  await page.getByRole("button", { name: "Delete gpt-4.1-mini" }).click();
  await page
    .getByRole("alertdialog", { name: "Delete model" })
    .getByRole("button", { name: "Delete" })
    .click();
  await expect(page.getByRole("row", { name: /gpt-4\.1-mini/ })).toHaveCount(0);
  await expect(page.getByText("Model deleted")).toBeVisible();
});

test("keeps create input after an API failure", async ({ page }) => {
  await installModelsApi(page, { createFailure: true });
  await page.goto("/models");
  await page.getByRole("button", { name: "Create model" }).click();

  const createDialog = page.getByRole("dialog", { name: "Create model" });
  await createDialog.getByLabel("Name").fill("duplicate-model");
  await createDialog.getByLabel("Max tokens").fill("4096");
  await createDialog.getByRole("button", { name: "Create" }).click();

  await expect(createDialog).toBeVisible();
  await expect(createDialog.getByLabel("Name")).toHaveValue("duplicate-model");
  await expect(page.getByText(/Create failed: Model name is already used/)).toBeVisible();
});

test("uses the scoped HeroUI theme in light and dark modes", async ({ page }, testInfo) => {
  await installModelsApi(page);
  await page.emulateMedia({ colorScheme: "light" });
  await page.goto("/models");
  await expect(page.locator('[data-ui-system="heroui"]')).toHaveClass(/uber/);
  await expect(page.locator('[data-ui-system="heroui"]')).not.toHaveClass(/dark/);
  await page.screenshot({ path: testInfo.outputPath("models-light.png"), fullPage: true });

  await page.emulateMedia({ colorScheme: "dark" });
  await page.reload();
  await expect(page.locator('[data-ui-system="heroui"]')).toHaveClass(/uber/);
  await expect(page.locator('[data-ui-system="heroui"]')).toHaveClass(/dark/);
  await page.screenshot({ path: testInfo.outputPath("models-dark.png"), fullPage: true });
});
