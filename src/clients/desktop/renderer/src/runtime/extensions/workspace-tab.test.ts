import assert from "node:assert/strict";
import test from "node:test";

import { BUILT_IN_VIEWS } from "./workspace-ids";
import {
  activeViewIdOf,
  createInitialTabGroup,
  fromPersistedTabGroup,
  toPersistedTabGroup,
  type ExtensionTab,
} from "./workspace-tab";

const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;

test("a new group has one active pinned draft conversation and a separate floating draft", () => {
  const group = createInitialTabGroup();
  const [tab] = group.tabs;
  assert.equal(group.tabs.length, 1);
  assert.ok(tab.kind === "chat");
  assert.equal(tab.conversationId, null);
  assert.equal(tab.preview, false);
  assert.equal(tab.draftSubmitted, false);
  assert.match(tab.draftId, UUID_V7);
  assert.equal(group.activeTabId, tab.id);
  assert.deepEqual(group.recentTabIds, [tab.id]);
  assert.match(group.floatingChat.draftId, UUID_V7);
  assert.notEqual(group.floatingChat.draftId, tab.draftId);
  assert.equal(group.floatingChat.draftSubmitted, false);
});

test("persisting drops the extension sequence and reading starts it at 0", () => {
  const extension: ExtensionTab = {
    id: "e",
    kind: "extension",
    preview: false,
    viewId: "x:main",
    key: null,
    title: null,
    params: { a: 1 },
    sequence: 4,
  };
  const group = {
    ...createInitialTabGroup(),
    tabs: [extension],
    activeTabId: "e",
    recentTabIds: ["e"],
  };
  const persisted = toPersistedTabGroup(group);
  assert.equal("sequence" in persisted.tabs[0], false);
  assert.deepEqual(fromPersistedTabGroup(persisted).tabs[0], { ...extension, sequence: 0 });
});

test("activeViewIdOf maps tabs to their view ids", () => {
  assert.equal(activeViewIdOf(createInitialTabGroup().tabs[0]), BUILT_IN_VIEWS.chat);
  assert.equal(
    activeViewIdOf({
      id: "f",
      kind: "file",
      preview: false,
      directoryId: null,
      path: "a",
      diffScope: null,
      diffFileView: false,
    }),
    BUILT_IN_VIEWS.file,
  );
  assert.equal(activeViewIdOf({ id: "j", kind: "jobs", preview: false }), BUILT_IN_VIEWS.jobs);
  assert.equal(
    activeViewIdOf({
      id: "e",
      kind: "extension",
      preview: false,
      viewId: "x:main",
      key: null,
      title: null,
      params: null,
      sequence: 0,
    }),
    "x:main",
  );
});
