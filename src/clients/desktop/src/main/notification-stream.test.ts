import assert from "node:assert/strict";
import test from "node:test";

import { createNotificationStreamParser, NotificationStreamReducer } from "./notification-stream";

const notification = {
  id: "019a4c17-0000-7000-8000-000000000020",
  createdAt: "2026-11-08T01:00:02Z",
  kind: "Notice",
  state: "Open",
  title: "Build finished",
  text: "The build completed.",
  link: null,
  readAt: null,
  changedAt: "2026-11-08T01:00:02Z",
  version: 1,
};

test("NotificationStreamParser_ReadyBoundaryAndNotification_EmitsTypedEvents", () => {
  const events: Parameters<Parameters<typeof createNotificationStreamParser>[0]>[0][] = [];
  const parser = createNotificationStreamParser((event) => events.push(event));
  parser.feed(
    `event: ready\ndata: {"connectedAt":"2026-11-08T01:00:00Z"}\n\n` +
      `id: 2026-11-08T01:00:02Z|${notification.id}\nevent: notification\ndata: ${JSON.stringify(notification)}\n\n`,
  );

  assert.deepEqual(events, [
    { type: "ready", connectedAt: "2026-11-08T01:00:00Z" },
    {
      type: "notification",
      id: `2026-11-08T01:00:02Z|${notification.id}`,
      notification,
    },
  ]);
});

test("NotificationStreamReducer_BacklogReplayAndVersionUpdate_SuppressesToastAndDuplicates", () => {
  const reducer = new NotificationStreamReducer();
  reducer.apply({ type: "ready", connectedAt: "2026-11-08T01:00:00Z" });
  const backlog = {
    type: "notification" as const,
    id: "cursor-1",
    notification: { ...notification, createdAt: "2026-11-08T00:59:00Z" },
  };

  assert.equal(reducer.apply(backlog)?.showToast, false);
  assert.equal(reducer.apply(backlog), null);
  assert.equal(
    reducer.apply({
      ...backlog,
      id: "cursor-2",
      notification: {
        ...notification,
        version: 2,
        createdAt: "2026-11-08T00:59:00Z",
        changedAt: "2026-11-08T01:00:03Z",
      },
    })?.showToast,
    false,
  );
});

test("NotificationStreamReducer_NewNotificationAfterReadyBoundary_AllowsToast", () => {
  const reducer = new NotificationStreamReducer();
  reducer.apply({ type: "ready", connectedAt: "2026-11-08T01:00:00Z" });

  assert.equal(
    reducer.apply({
      type: "notification",
      id: "cursor-1",
      notification,
    })?.showToast,
    true,
  );
});

test("NotificationStreamReducer_ReadChangeWithSameVersionUpdatesOnceWithoutAnotherToast", () => {
  const reducer = new NotificationStreamReducer();
  reducer.apply({ type: "ready", connectedAt: "2026-11-08T01:00:00Z" });
  const first = {
    type: "notification" as const,
    id: `2026-11-08T01:00:02Z|${notification.id}`,
    notification,
  };
  const read = {
    type: "notification" as const,
    id: `2026-11-08T01:00:02Z|${notification.id}`,
    notification: {
      ...notification,
      readAt: notification.changedAt,
    },
  };

  assert.equal(reducer.apply(first)?.showToast, true);
  assert.equal(reducer.apply(read)?.notification.readAt, notification.changedAt);
  assert.equal(reducer.apply(read), null);
  assert.equal(
    reducer.apply({
      type: "notification",
      id: `2026-11-08T01:00:05Z|${notification.id}`,
      notification: {
        ...notification,
        title: "Updated after read",
        changedAt: "2026-11-08T01:00:05Z",
        version: 2,
      },
    })?.showToast,
    false,
  );

  reducer.beginConnection();
  reducer.apply({ type: "ready", connectedAt: "2026-11-08T01:01:00Z" });
  const replayAfterReconnect = {
    type: "notification" as const,
    id: `2026-11-08T01:00:06Z|${notification.id}`,
    notification: {
      ...notification,
      title: "Updated while disconnected",
      changedAt: "2026-11-08T01:00:06Z",
      version: 3,
    },
  };

  assert.equal(reducer.apply(replayAfterReconnect)?.notification.version, 3);
  assert.equal(reducer.apply(replayAfterReconnect), null);
});
