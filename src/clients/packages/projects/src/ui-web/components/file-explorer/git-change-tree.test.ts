import assert from "node:assert/strict";
import test from "node:test";
import type { FileItem } from "../../../services/files";
import { buildGitChangeGroups, formatFileCount } from "./git-change-tree";

test("git change tree separates scopes and duplicates partially staged files", () => {
  const items: FileItem[] = [
    {
      name: "mixed.cs",
      path: "src/server/Agw.Projects/Domain/Services/mixed.cs",
      type: "file",
      gitStatus: "modified",
      gitStagedStatus: "modified",
      gitUnstagedStatus: "modified",
    },
    {
      name: "staged.cs",
      path: "src/server/Agw.Shared/Extensions/staged.cs",
      type: "file",
      gitStatus: "added",
      gitStagedStatus: "added",
      gitUnstagedStatus: null,
    },
    {
      name: "unstaged.cs",
      path: "tests/Agw.Projects.Tests/unstaged.cs",
      type: "file",
      gitStatus: "modified",
      gitStagedStatus: null,
      gitUnstagedStatus: "modified",
    },
    {
      name: "new.cs",
      path: "tests/Agw.Shared.Tests/new.cs",
      type: "file",
      gitStatus: "untracked",
      gitUnstagedStatus: "untracked",
    },
  ];

  const groups = buildGitChangeGroups(items, "");

  assert.deepEqual(
    groups.map((group) => [group.scope, group.fileCount]),
    [
      ["staged", 2],
      ["unstaged", 3],
    ],
  );
  assert.equal(groups[0].items[0].name, "src/server");
  assert.equal(groups[0].items[0].changeCount, 2);
  assert.deepEqual(
    groups[0].items[0].children?.map((item) => [item.name, item.changeCount]),
    [
      ["Agw.Projects/Domain/Services", 1],
      ["Agw.Shared/Extensions", 1],
    ],
  );

  const stagedMixed = groups[0].items[0].children?.[0].children?.[0];
  const unstagedMixed = groups[1].items[0].children?.[0];
  assert.equal(stagedMixed?.gitScope, "staged");
  assert.equal(stagedMixed?.gitStatus, "modified");
  assert.equal(unstagedMixed?.gitScope, "unstaged");
  assert.equal(unstagedMixed?.gitStatus, "modified");
});

test("formatFileCount uses singular and plural labels", () => {
  assert.equal(formatFileCount(1), "1 file");
  assert.equal(formatFileCount(2), "2 files");
});
