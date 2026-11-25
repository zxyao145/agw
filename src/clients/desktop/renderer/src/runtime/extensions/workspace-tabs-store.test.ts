import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test, { type TestContext } from "node:test";
import { createScratchDirectory } from "@desktop/main/scratch-directory.test-helper";
import { WorkspaceTabsFile } from "@desktop/main/settings/workspace-tabs-file";

import { activeTabOf, workspaceChatState, WorkspaceTabsStore } from "./workspace-tabs-store";
import { BUILT_IN_VIEWS } from "./workspace-ids";

const A = { serverId: "local", projectId: "project-a" };
const B = { serverId: "local", projectId: "project-b" };
const file = (path: string, directoryId: string | null = null) => ({
  directoryId,
  path,
  diffScope: null,
});
const pinned = { preview: false };
const preview = { preview: true };

async function setup(t: TestContext, options: { answer?: boolean; content?: string } = {}) {
  const scratch = await createScratchDirectory("workspace-tabs-store");
  const path = join(scratch.path, "workspace-tabs.json");
  if (options.content !== undefined) await writeFile(path, options.content, "utf8");
  const storage = new WorkspaceTabsFile(scratch.path);
  t.after(async () => {
    if (options.content === undefined) await storage.read(A.serverId, A.projectId);
    await scratch.dispose();
  });
  const released: { ids: readonly string[]; keepUnsaved: boolean }[] = [];
  const confirmed: (readonly string[])[] = [];
  const discarded: string[] = [];
  const store = new WorkspaceTabsStore({
    bridge: {
      getWorkspaceTabs: storage.read.bind(storage),
      saveWorkspaceTabs: storage.save.bind(storage),
    },
    resources: {
      confirmDiscard: async (ids) => {
        confirmed.push(ids);
        return options.answer ?? true;
      },
      release: (ids, keepUnsaved) => released.push({ ids, keepUnsaved }),
      discardDrafts: (_scope, ids) => discarded.push(...ids),
    },
    reportError: (_message, error) => {
      throw error;
    },
  });
  await store.setScope(A);
  const group = () => store.getSnapshot().group!;
  return {
    store,
    group,
    path,
    released,
    confirmed,
    discarded,
    active: () => activeTabOf(group()),
    saved: () => storage.read(A.serverId, A.projectId),
    order: () =>
      group().tabs.map((tab) => {
        if (tab.kind === "file") return tab.path;
        if (tab.kind === "chat") return tab.conversationId ?? "draft";
        return tab.kind;
      }),
  };
}

test("a Project without saved tabs saves its active pinned draft", async (t) => {
  const { active, saved } = await setup(t);
  assert.equal(active()?.kind, "chat");
  assert.equal(active()?.preview, false);
  assert.equal((await saved())?.activeTabId, active()?.id);
});

test("opening the same conversation activates the original tab", async (t) => {
  const { store, order, active } = await setup(t);
  store.openConversation("c1", pinned);
  const id = active()?.id;
  store.openFile(file("a.md"), pinned);
  store.openConversation("c1", preview);
  assert.deepEqual(order(), ["draft", "c1", "a.md"]);
  assert.equal(active()?.id, id);
  assert.equal(active()?.preview, false);
});

test("new tabs open to the right of the active tab", async (t) => {
  const { store, group, order } = await setup(t);
  store.openFile(file("a.md"), pinned);
  store.openFile(file("b.md"), pinned);
  store.activate(group().tabs[1].id);
  store.openFile(file("c.md"), pinned);
  assert.deepEqual(order(), ["draft", "a.md", "c.md", "b.md"]);
});

test("opening a preview replaces the old preview in place and releases it", async (t) => {
  const { store, active, released, order } = await setup(t);
  store.openFile(file("a.md"), preview);
  const oldId = active()!.id;
  store.openJobs();
  store.openFile(file("b.md"), preview);
  assert.deepEqual(order(), ["draft", "b.md", "jobs"]);
  assert.notEqual(active()?.id, oldId);
  assert.equal(active()?.preview, true);
  assert.deepEqual(released, [{ ids: [oldId], keepUnsaved: false }]);
});

test("a preview pinned explicitly or by reopening survives the next preview", async (t) => {
  const { store, group, active, order } = await setup(t);
  store.openFile(file("a.md"), preview);
  store.pin(active()!.id);
  store.openFile(file("b.md"), preview);
  store.openFile(file("b.md"), pinned);
  store.openFile(file("b.md"), preview);
  store.openFile(file("c.md"), preview);
  assert.deepEqual(order(), ["draft", "a.md", "b.md", "c.md"]);
  assert.deepEqual(
    group().tabs.map((tab) => tab.preview),
    [false, false, false, true],
  );
});

test("closing the active tab uses recency and closing all saves the empty group", async (t) => {
  const { store, group, active, saved } = await setup(t);
  const draft = active()!;
  store.openFile(file("a.md"), pinned);
  store.openFile(file("b.md"), pinned);
  const last = active()!;
  store.activate(draft.id);
  store.activate(last.id);
  assert.equal(await store.close([last.id]), true);
  assert.equal(active()?.id, draft.id);
  assert.equal(await store.close(group().recentTabIds), true);
  assert.deepEqual((await saved())?.tabs, []);
  assert.equal(group().activeTabId, null);
});

test("canceling an unsaved confirmation closes nothing", async (t) => {
  const { store, group, confirmed, released } = await setup(t, { answer: false });
  store.openFile(file("a.md"), pinned);
  const ids = group().tabs.map((tab) => tab.id);
  assert.equal(await store.close(ids), false);
  assert.deepEqual(confirmed, [ids]);
  assert.equal(group().tabs.length, 2);
  assert.deepEqual(released, []);
});

test("closing a draft tab removes its input and image drafts", async (t) => {
  const { store, active, discarded } = await setup(t);
  const draft = active();
  assert.ok(draft?.kind === "chat");
  await store.close([draft.id]);
  assert.deepEqual(discarded, [draft.draftId]);
});

test("a submitted draft is accepted only with its own conversation id", async (t) => {
  const { store, active } = await setup(t);
  const draft = active();
  assert.ok(draft?.kind === "chat");
  assert.throws(() => store.acceptConversation(draft.id, "other"));
  store.setDraftSubmitted(draft.id, true);
  store.acceptConversation(draft.id, draft.draftId);
  assert.deepEqual(active(), { ...draft, conversationId: draft.draftId });
});

test("opening a conversation with a pending draft activates that draft", async (t) => {
  const { store, active, group } = await setup(t);
  const draft = active();
  assert.ok(draft?.kind === "chat");
  store.setDraftSubmitted(draft.id, true);
  store.openJobs();
  store.openConversation(draft.draftId, pinned);
  assert.equal(active()?.id, draft.id);
  store.acceptConversation(draft.id, draft.draftId);
  assert.equal(group().tabs.filter((tab) => tab.kind === "chat").length, 1);
});

test("opening a pending floating conversation moves its draft into the tab", async (t) => {
  const { store, group, active, discarded } = await setup(t);
  const id = group().floatingChat.draftId;
  store.floatingChat.setDraftSubmitted(true);
  store.openConversation(id, preview);
  const tab = active();
  assert.ok(tab?.kind === "chat");
  assert.equal(tab.draftId, id);
  assert.equal(tab.draftSubmitted, true);
  assert.equal(tab.preview, true);
  assert.notEqual(group().floatingChat.draftId, id);
  assert.deepEqual(discarded, []);
  store.openConversation(id, pinned);
  assert.equal(active()?.id, tab.id);
  assert.equal(active()?.preview, false);
});

test("opening the floating conversation gives the floating chat a new draft", async (t) => {
  const { store, group, active, discarded } = await setup(t);
  const id = group().floatingChat.draftId;
  store.floatingChat.acceptConversation(id);
  store.openConversation(id, preview);
  const tab = active();
  assert.ok(tab?.kind === "chat" && tab.conversationId === id);
  assert.equal(group().floatingChat.conversationId, null);
  assert.notEqual(group().floatingChat.draftId, id);
  assert.deepEqual(discarded, []);
});

test("replacing the floating draft discards its input and image drafts", async (t) => {
  const { store, group, discarded } = await setup(t);
  const id = group().floatingChat.draftId;
  store.floatingChat.newConversation();
  assert.notEqual(group().floatingChat.draftId, id);
  assert.deepEqual(discarded, [id]);
});

test("deletions remove matching conversation and descendant file tabs without confirmation", async (t) => {
  const { store, group, order, confirmed } = await setup(t);
  store.openConversation("c1", pinned);
  store.openConversation("c2", pinned);
  store.openFile(file("docs/a.md"), pinned);
  store.openFile(file("docs/sub/b.md"), pinned);
  store.openFile(file("docs-extra/c.md"), pinned);
  store.openFile(file("docs/a.md", "d2"), pinned);
  store.removeFiles(null, "docs");
  store.removeConversations(["c1"]);
  assert.deepEqual(order(), ["draft", "c2", "docs-extra/c.md", "docs/a.md"]);
  store.floatingChat.acceptConversation(group().floatingChat.draftId);
  store.removeConversations("all");
  assert.deepEqual(order(), ["docs-extra/c.md", "docs/a.md"]);
  assert.equal(group().floatingChat.conversationId, null);
  assert.deepEqual(confirmed, []);
});

test("only an on-screen conversation tab suppresses floating chat", async (t) => {
  const { store, group } = await setup(t);
  const tabId = group().activeTabId;
  const floatingId = group().floatingChat.draftId;
  store.floatingChat.acceptConversation(floatingId);
  assert.deepEqual(workspaceChatState(group(), null, true), {
    floatingAllowed: false,
    session: { id: tabId, conversationId: null },
  });
  for (const viewId of [BUILT_IN_VIEWS.events, BUILT_IN_VIEWS.notifications]) {
    assert.deepEqual(workspaceChatState(group(), viewId, true), {
      floatingAllowed: true,
      session: { id: floatingId, conversationId: floatingId },
    });
    assert.deepEqual(workspaceChatState(group(), viewId, false), {
      floatingAllowed: true,
      session: null,
    });
  }
  await store.close(group().recentTabIds);
  assert.equal(workspaceChatState(group(), null, false).floatingAllowed, true);
});

test("reopening a file from Diff mode sets the diff view of the existing tab", async (t) => {
  const { store, active } = await setup(t);
  store.openFile({ ...file("a.md"), diffScope: "staged" }, pinned);
  const id = active()!.id;
  store.setFileView(id, { diffScope: "staged", diffFileView: true });
  store.openFile(file("a.md"), { ...preview, replaceDiffView: true });
  const tab = active();
  assert.ok(tab?.kind === "file");
  assert.deepEqual(
    [tab.id, tab.diffScope, tab.diffFileView, tab.preview],
    [id, null, false, false],
  );
});

test("reopening a file outside Diff mode preserves the existing diff preference", async (t) => {
  const { store, active } = await setup(t);
  store.openFile({ ...file("a.md"), diffScope: "staged" }, preview);
  const id = active()!.id;
  store.setFileView(id, { diffScope: "staged", diffFileView: true });
  store.openFile(file("a.md"), pinned);
  const tab = active();
  assert.ok(tab?.kind === "file");
  assert.deepEqual(
    [tab.id, tab.diffScope, tab.diffFileView, tab.preview],
    [id, "staged", true, false],
  );
});

test("moving pins a preview and adjacent activation wraps around", async (t) => {
  const { store, active, order } = await setup(t);
  store.openFile(file("a.md"), preview);
  store.move(active()!.id, 0);
  assert.deepEqual(order(), ["a.md", "draft"]);
  assert.equal(active()?.preview, false);
  store.activateAdjacent(-1);
  assert.equal(active()?.kind, "chat");
  store.activateAdjacent(1);
  assert.equal(active()?.kind, "file");
});

test("opening extension tabs updates the same key and distinguishes different keys", async (t) => {
  const { store, group, active, saved } = await setup(t);
  const options = { key: null, title: "Repository", preview: false };
  store.openExtensionView("x:file", { a: 1 }, options);
  store.openExtensionView("x:file", { a: 2 }, { ...options, title: null });
  assert.deepEqual(active(), {
    id: active()?.id,
    kind: "extension",
    preview: false,
    viewId: "x:file",
    key: null,
    title: "Repository",
    params: { a: 2 },
    sequence: 1,
  });
  store.openExtensionView("x:file", null, { ...options, key: "a.md" });
  assert.equal(group().tabs.length, 3);
  assert.equal("sequence" in (await saved())!.tabs[1], false);
});

test("switching scopes releases resources and restores the Project's own tabs", async (t) => {
  const { store, group, released, order } = await setup(t);
  store.openFile(file("a.md"), pinned);
  const ids = group().tabs.map((tab) => tab.id);
  const switching = store.setScope(B);
  assert.equal(store.getSnapshot().group, null);
  assert.throws(() => store.openJobs(), /The tab group is not loaded\./u);
  await store.whenLoaded();
  await switching;
  assert.deepEqual(released.at(-1), { ids, keepUnsaved: true });
  assert.deepEqual(order(), ["draft"]);
  await store.setScope(A);
  assert.deepEqual(order(), ["draft", "a.md"]);
});

test("superseded reads and delayed closes cannot affect the current group", async (t) => {
  const { store, order, group } = await setup(t);
  store.openFile(file("a.md"), pinned);
  const closing = store.close(group().recentTabIds);
  await Promise.all([store.setScope(B), store.setScope(A)]);
  assert.equal(await closing, false);
  assert.deepEqual(store.getSnapshot().scope, A);
  assert.deepEqual(order(), ["draft", "a.md"]);
});

test("a failed read stays in the snapshot and preserves the file", async (t) => {
  const { store, path } = await setup(t, { content: "{" });
  assert.equal(store.getSnapshot().group, null);
  assert.ok(store.getSnapshot().error instanceof Error);
  await assert.rejects(store.whenLoaded());
  assert.throws(() => store.openJobs(), /The tab group is not loaded\./u);
  assert.equal(await readFile(path, "utf8"), "{");
});
