import assert from "node:assert/strict";
import test from "node:test";
import type { EventSourceOptionResponse } from "@agw/api";

import {
  getEventTypeOptions,
  isAllowedEventType,
  matchesEventTypePattern,
} from "./event-type-options";

test("selected source declarations are combined without duplicates", () => {
  const sources: EventSourceOptionResponse[] = [
    { id: "one", name: "First", kind: 1, managedBy: "user", eventTypes: ["demo.*", "shared.type"] },
    {
      id: "two",
      name: "Second",
      kind: 2,
      managedBy: "user",
      eventTypes: ["shared.type", "other.type"],
    },
  ];
  assert.deepEqual(getEventTypeOptions(sources, ["one", "two"]), [
    "demo.*",
    "shared.type",
    "other.type",
  ]);
  assert.deepEqual(getEventTypeOptions(sources, ["two"]), ["shared.type", "other.type"]);
});

test("complete types and declared patterns can be selected", () => {
  assert.equal(matchesEventTypePattern("demo.*", "demo.ticket.created"), true);
  assert.equal(matchesEventTypePattern("demo.*", "demox.created"), false);
  assert.equal(isAllowedEventType("demo.ticket.created", ["demo.*"]), true);
  assert.equal(isAllowedEventType("demo.*", ["demo.*"]), true);
  assert.equal(isAllowedEventType("other", ["demo.*"]), false);
});
