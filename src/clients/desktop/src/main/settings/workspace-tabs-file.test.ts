import assert from "node:assert/strict";
import { readFileSync, statSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test, { type TestContext } from "node:test";

import type { PersistedChatTab, PersistedFileTab, PersistedTabGroup } from "../../shared/contracts";
import { tabKey } from "../../shared/workspace-tab-key";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { WorkspaceTabsFile } from "./workspace-tabs-file";

const DRAFT = "0196f3a4-1c2d-7e8f-9a0b-1c2d3e4f5a6b";
const chat: PersistedChatTab = {
  id: "t1",
  kind: "chat",
  preview: false,
  conversationId: null,
  draftId: DRAFT,
  draftSubmitted: false,
  title: null,
};
const doc: PersistedFileTab = {
  id: "t2",
  kind: "file",
  preview: true,
  directoryId: null,
  path: "docs/a.md",
  diffScope: null,
  diffFileView: false,
};

function group(overrides: Partial<PersistedTabGroup> = {}): PersistedTabGroup {
  return {
    tabs: [chat, doc],
    activeTabId: "t2",
    recentTabIds: ["t2", "t1"],
    floatingChat: {
      conversationId: null,
      draftId: "0196f3a4-1c2d-7e8f-9a0b-000000000001",
      draftSubmitted: false,
    },
    ...overrides,
  };
}

async function scratchFile(t: TestContext) {
  const scratch = await createScratchDirectory("workspace-tabs");
  t.after(() => scratch.dispose());
  return { path: scratch.path, file: new WorkspaceTabsFile(scratch.path) };
}

test("tabKey identifies each kind of tab", () => {
  assert.equal(tabKey({ kind: "chat", conversationId: "c1", draftId: DRAFT }), "chat:c1");
  assert.equal(tabKey({ kind: "chat", conversationId: null, draftId: DRAFT }), `draft:${DRAFT}`);
  assert.equal(tabKey({ kind: "file", directoryId: null, path: "a/b.md" }), "file::a/b.md");
  assert.equal(tabKey({ kind: "file", directoryId: "d1", path: "a/b.md" }), "file:d1:a/b.md");
  assert.equal(tabKey({ kind: "jobs" }), "jobs");
  assert.equal(tabKey({ kind: "extension", viewId: "x:file", key: null }), "view:x:file");
  assert.equal(
    tabKey({ kind: "extension", viewId: "x:file", key: "r/main/a.ts" }),
    "view:x:file:r/main/a.ts",
  );
});

test("read returns null for a Project without saved tabs", async (t) => {
  const { file } = await scratchFile(t);
  assert.equal(await file.read("local", "p1"), null);
});

test("save then read returns the group per Server and Project", async (t) => {
  const { file } = await scratchFile(t);
  await file.save("local", "p1", group());
  await file.save("remote", "p1", group({ activeTabId: "t1", recentTabIds: ["t1", "t2"] }));
  assert.deepEqual(await file.read("local", "p1"), group());
  assert.equal((await file.read("remote", "p1"))?.activeTabId, "t1");
  assert.equal(await file.read("local", "p2"), null);
});

test("invalid content makes read throw and is not overwritten", async (t) => {
  const { path, file } = await scratchFile(t);
  const content = '{"schemaVersion":1,"servers":{"local":{"p1":{"tabs":[]}}}}';
  await writeFile(join(path, "workspace-tabs.json"), content, "utf8");
  await assert.rejects(file.read("local", "p1"));
  await assert.rejects(file.save("local", "p2", group()));
  await assert.rejects(file.pruneServers(["local"]));
  assert.equal(await readFile(join(path, "workspace-tabs.json"), "utf8"), content);
});

test("save rejects groups that break the validation rules", async (t) => {
  const { file } = await scratchFile(t);
  const invalid: PersistedTabGroup[] = [
    group({ tabs: [chat, { ...doc, id: "t1" }] }),
    group({
      tabs: [chat, doc, { ...doc, id: "t3", preview: false }],
      recentTabIds: ["t2", "t1", "t3"],
    }),
    group({ tabs: [{ ...chat, preview: true }, doc] }),
    group({ recentTabIds: ["t2"] }),
    group({ recentTabIds: ["t2", "t2"] }),
    group({ activeTabId: "missing" }),
    group({ tabs: [{ ...chat, draftId: "not-a-uuid" }, doc] }),
    group({ tabs: [{ ...chat, conversationId: "c1", draftSubmitted: true }, doc] }),
    group({ floatingChat: { conversationId: "c1", draftId: DRAFT, draftSubmitted: true } }),
  ];
  for (const value of invalid) await assert.rejects(file.save("local", "p", value));
});

test("extension params over 64 KiB are rejected and exactly 64 KiB are accepted", async (t) => {
  const { file } = await scratchFile(t);
  const withTab = (params: string) =>
    group({
      tabs: [
        ...group().tabs,
        {
          id: "t3",
          kind: "extension",
          preview: false,
          viewId: "x:file",
          key: null,
          title: null,
          params,
        },
      ],
      recentTabIds: ["t2", "t1", "t3"],
    });
  // JSON 字符串的两个引号也计入。The two quotes of the JSON string count too.
  const exactly = "é".repeat(32767);
  await assert.rejects(file.save("local", "p", withTab(`${exactly}a`)));
  await file.save("local", "p", withTab(exactly));
  assert.deepEqual(await file.read("local", "p"), withTab(exactly));
});

test("extension params retain every JSON property through a save and read", async (t) => {
  const { file } = await scratchFile(t);
  const value = group({
    tabs: [
      {
        id: "e",
        kind: "extension",
        preview: false,
        viewId: "x:file",
        key: null,
        title: null,
        params: { nested: { ["__proto__"]: ["value"] } },
      },
    ],
    activeTabId: "e",
    recentTabIds: ["e"],
  });
  await file.save("local", "p", value);
  assert.deepEqual(await file.read("local", "p"), value);
});

test("consecutive saves commit the latest Project groups in one atomic replacement", async (t) => {
  const { path, file } = await scratchFile(t);
  const filePath = join(path, "workspace-tabs.json");
  const writes = [
    file.save("local", "p1", group({ activeTabId: "t1", recentTabIds: ["t1", "t2"] })),
    file.save("local", "p1", group()),
    file.save("local", "p2", group()),
  ];
  const commits = await Promise.all(
    writes.map((written) =>
      written.then(() => ({
        inode: statSync(filePath, { bigint: true }).ino,
        state: JSON.parse(readFileSync(filePath, "utf8")),
      })),
    ),
  );
  assert.equal(new Set(commits.map(({ inode }) => inode)).size, 1);
  for (const { state } of commits) {
    assert.deepEqual(state, {
      schemaVersion: 1,
      servers: { local: { p1: group(), p2: group() } },
    });
  }
});

test("a read issued after a save that is still writing returns the saved group", async (t) => {
  const { file } = await scratchFile(t);
  const saving = file.save("local", "p1", group());
  assert.deepEqual(await file.read("local", "p1"), group());
  await saving;
});

test("pruneProjects and pruneServers keep only the given ids", async (t) => {
  const { file } = await scratchFile(t);
  await file.save("local", "p1", group());
  await file.save("local", "p2", group());
  await file.save("remote", "p1", group());
  await file.pruneProjects("local", ["p2"]);
  assert.equal(await file.read("local", "p1"), null);
  assert.notEqual(await file.read("local", "p2"), null);
  await file.pruneServers(["local"]);
  assert.equal(await file.read("remote", "p1"), null);
});

test("queued reads and prunes observe their order relative to saves", async (t) => {
  const { file } = await scratchFile(t);
  const firstSave = file.save("local", "p1", group());
  const beforePrune = file.read("local", "p1");
  const prune = file.pruneProjects("local", []);
  const afterPrune = file.read("local", "p1");
  const secondGroup = group({ activeTabId: "t1", recentTabIds: ["t1", "t2"] });
  const lastSave = file.save("local", "p1", secondGroup);
  await Promise.all([firstSave, prune, lastSave]);
  assert.deepEqual(await beforePrune, group());
  assert.equal(await afterPrune, null);
  assert.deepEqual(await file.read("local", "p1"), secondGroup);
});
