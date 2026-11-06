import assert from "node:assert/strict";
import { resolve } from "node:path";
import test, { after, before } from "node:test";
import {
  type BrowserFixture,
  startBrowserFixture,
} from "../../packages/test-harness/src/browser-fixture";

let fixture: BrowserFixture;

before(async () => {
  fixture = await startBrowserFixture({
    root: resolve(import.meta.dirname, "bare-repository-workspace"),
    outDir: resolve(import.meta.dirname, "../../../../.scratch/bare-repository-components"),
  });
});

after(async () => {
  await fixture?.close();
});

test("CloneDialog_DefaultDestination_SubmitsProjectSubdirectory", async (t) => {
  const { page, errors } = await fixture.newPage();
  t.after(() => page.close());
  await page.goto(fixture.origin);
  assert.equal(await page.getByLabel("Destination").inputValue(), "subdirectory");
  await page.getByLabel("Repository URL").fill(" https://example.com/source.git ");
  await page.getByLabel("Directory name").fill("checkout");
  await page.getByRole("button", { name: "Clone", exact: true }).click();
  assert.deepEqual(JSON.parse((await page.getByLabel("Submitted event").textContent())!), {
    url: "https://example.com/source.git",
    name: "checkout",
    intoRoot: false,
  });
  assert.deepEqual(errors, []);
});

test("CloneDialog_ProjectRoot_HidesAndOmitsDirectoryName", async (t) => {
  const { page, errors } = await fixture.newPage();
  t.after(() => page.close());
  await page.goto(fixture.origin);
  await page.getByLabel("Repository URL").fill("https://example.com/source.git");
  await page.getByLabel("Directory name").fill("custom-name");
  await page.getByLabel("Destination").selectOption("root");
  assert.equal(await page.getByLabel("Directory name").count(), 0);
  await page.getByRole("button", { name: "Clone", exact: true }).click();
  assert.deepEqual(JSON.parse((await page.getByLabel("Submitted event").textContent())!), {
    url: "https://example.com/source.git",
    intoRoot: true,
  });
  assert.deepEqual(errors, []);
});

test("CloneDialog_ReturnToSubdirectory_PreservesDirectoryName", async (t) => {
  const { page, errors } = await fixture.newPage();
  t.after(() => page.close());
  await page.goto(fixture.origin);
  await page.getByLabel("Repository URL").fill("https://example.com/source.git");
  await page.getByLabel("Directory name").fill("checkout");
  await page.getByLabel("Destination").selectOption("root");
  await page.getByLabel("Destination").selectOption("subdirectory");
  assert.equal(await page.getByLabel("Directory name").inputValue(), "checkout");
  await page.getByRole("button", { name: "Clone", exact: true }).click();
  assert.deepEqual(JSON.parse((await page.getByLabel("Submitted event").textContent())!), {
    url: "https://example.com/source.git",
    name: "checkout",
    intoRoot: false,
  });
  assert.deepEqual(errors, []);
});

test("CloneDialog_CancelRootClone_AbortsSubmittedOperation", async (t) => {
  const { page, errors } = await fixture.newPage();
  t.after(() => page.close());
  await page.goto(fixture.origin);
  await page.getByLabel("Repository URL").fill("https://example.com/source.git");
  await page.getByLabel("Destination").selectOption("root");
  await page.getByRole("button", { name: "Clone", exact: true }).click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  assert.deepEqual(JSON.parse((await page.getByLabel("Submitted event").textContent())!), {
    closed: true,
    aborted: true,
  });
  assert.equal(await page.getByRole("dialog").count(), 0);
  assert.deepEqual(errors, []);
});

test("FeatureDialog_RootRepository_SelectsRepositoryAndPreviewsFlatPath", async (t) => {
  const { page, errors } = await fixture.newPage();
  t.after(() => page.close());
  await page.goto(`${fixture.origin}/?view=root-feature`);
  assert.equal(await page.getByRole("checkbox", { name: "project main" }).isChecked(), true);
  await page.getByLabel("Feature name").fill("search");
  const branch = await page.locator(".preview code").first().textContent();
  assert.equal(await page.locator(".preview code").last().textContent(), `.worktrees/${branch}`);
  await page.getByRole("button", { name: "Create worktrees" }).click();
  assert.deepEqual(JSON.parse((await page.getByLabel("Submitted event").textContent())!), {
    name: "search",
    repositories: ["project"],
  });
  assert.deepEqual(errors, []);
});

test("FeatureDialog_SubdirectoryRepository_RequiresSelectionAndPreviewsRepositoryPath", async (t) => {
  const { page, errors } = await fixture.newPage();
  t.after(() => page.close());
  await page.goto(`${fixture.origin}/?view=multi-feature`);
  assert.equal(await page.getByRole("checkbox", { name: "project main" }).isChecked(), false);
  await page.getByLabel("Feature name").fill("search");
  assert.equal(await page.getByRole("button", { name: "Create worktrees" }).isDisabled(), true);
  const branch = await page.locator(".preview code").first().textContent();
  assert.equal(
    await page.locator(".preview code").last().textContent(),
    `.worktrees/${branch}/<repo>`,
  );
  await page.getByRole("checkbox", { name: "project main" }).check();
  await page.getByRole("button", { name: "Create worktrees" }).click();
  assert.deepEqual(JSON.parse((await page.getByLabel("Submitted event").textContent())!), {
    name: "search",
    repositories: ["project"],
  });
  assert.deepEqual(errors, []);
});
