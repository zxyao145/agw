import assert from "node:assert/strict";
import test from "node:test";

import {
  isNotificationRevisionNewer,
  mergeNotificationItem,
  parseNotificationStreamItem,
} from "./notification-state";

const item = {
  id: "019a4c17-0000-7000-8000-000000000020",
  kind: 0 as const,
  state: 0 as const,
  title: "Build finished",
  text: "The build completed.",
  link: null,
  readAt: null,
  changedAt: "2026-11-08T01:00:02Z",
  version: 1,
};

test("mergeNotificationItem_NewVersionReplacesExistingAndSortsByChangedAt", () => {
  const older = {
    ...item,
    id: "019a4c17-0000-7000-8000-000000000010",
    changedAt: "2026-11-08T00:59:00Z",
  };
  const updated = { ...item, version: 2, changedAt: "2026-11-08T01:00:03Z" };

  assert.deepEqual(mergeNotificationItem([older, item], updated), [updated, older]);
});

test("mergeNotificationItem_DuplicateOrOlderVersion_KeepsCurrentItems", () => {
  const current = { ...item, version: 2 };

  assert.deepEqual(mergeNotificationItem([current], item), [current]);
  assert.deepEqual(mergeNotificationItem([current], current), [current]);
});

test("mergeNotificationItem_LaterReadChangeWithSameVersion_ReplacesExisting", () => {
  const read = {
    ...item,
    readAt: item.changedAt,
  };

  assert.deepEqual(mergeNotificationItem([item], read), [read]);
  assert.deepEqual(mergeNotificationItem([read], item), [read]);
});

test("isNotificationRevisionNewer_VersionThenChangedAtThenReadAt", () => {
  const current = {
    ...item,
    version: 2,
    changedAt: "2026-11-08T01:00:03Z",
    readAt: item.changedAt,
  };
  assert.equal(
    isNotificationRevisionNewer(
      { ...item, version: 3, changedAt: "2026-11-08T01:00:01Z" },
      current,
    ),
    true,
  );
  assert.equal(
    isNotificationRevisionNewer(
      { ...current, changedAt: "2026-11-08T01:00:04Z", readAt: null },
      current,
    ),
    true,
  );
  assert.equal(
    isNotificationRevisionNewer(
      { ...current, readAt: item.changedAt },
      { ...current, readAt: null },
    ),
    true,
  );
  assert.equal(isNotificationRevisionNewer({ ...current, readAt: null }, current), false);
});

test("parseNotificationStreamItem_CompleteFrame_ReturnsValidatedItem", () => {
  const streamItem = parseNotificationStreamItem({
    ...item,
    createdAt: "2026-11-08T01:00:02Z",
    kind: "Notice",
  });

  assert.equal(streamItem.id, item.id);
  assert.equal(streamItem.version, 1);
  assert.equal(streamItem.kind, 0);
});
