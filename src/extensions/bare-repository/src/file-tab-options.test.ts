import assert from "node:assert/strict";
import test from "node:test";
import { workspaceOpenOptions } from "@agw/extension-sdk/workspace-params";
import { repositoryFileTabOptions } from "./file-tab-options";

test("RepositoryFileTabOptions_OrdinaryFile_UsesBaseNameAndRequestedPreview", async () => {
  const file = { repository: "example", branch: "feature/topic", path: "docs/guide.md" };
  const preview = await repositoryFileTabOptions(file, true);
  const pinned = await repositoryFileTabOptions(file, false);

  assert.match(preview.key, /^[0-9a-f]{64}$/u);
  assert.deepEqual(preview, { key: preview.key, title: "guide.md", preview: true });
  assert.deepEqual(pinned, { key: preview.key, title: "guide.md", preview: false });
});

test("RepositoryFileTabOptions_LongPaths_ReturnsDistinctBoundedKeysAndReadableTitles", async () => {
  const folder = `documentation-${"a".repeat(140)}`;
  const namePrefix = "guide-" + "a".repeat(92);
  const first = {
    repository: "example",
    branch: "feature/topic",
    path: `${folder}/${folder}/${namePrefix}👩‍🔬-first.md`,
  };
  const second = { ...first, path: first.path.replace("-first.md", "-second.md") };
  const firstTab = await repositoryFileTabOptions(first, true);
  const secondTab = await repositoryFileTabOptions(second, true);

  assert.ok(first.path.length > 256);
  assert.deepEqual(workspaceOpenOptions(firstTab, "view"), firstTab);
  assert.deepEqual(workspaceOpenOptions(secondTab, "view"), secondTab);
  assert.notEqual(firstTab.key, secondTab.key);
  assert.equal(firstTab.title, `${namePrefix}…`);
  assert.equal(secondTab.title, firstTab.title);
  assert.equal(firstTab.preview, true);
});

test("RepositoryFileTabOptions_SlashesInBranchesAndPaths_PreservesPartBoundaries", async () => {
  const first = { repository: "example", branch: "feature", path: "topic/guide.md" };
  const second = { repository: "example", branch: "feature/topic", path: "guide.md" };

  assert.notEqual(
    (await repositoryFileTabOptions(first, false)).key,
    (await repositoryFileTabOptions(second, false)).key,
  );
});
