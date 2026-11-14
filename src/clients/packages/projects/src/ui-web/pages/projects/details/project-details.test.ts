import assert from "node:assert/strict";
import test from "node:test";

import { buildCreateTaskJobRequest, createQuickTaskAt } from "./project-details";

test("buildCreateTaskJobRequest builds a one-time job", () => {
  const now = new Date("2026-11-12T08:00:00Z");
  assert.deepEqual(
    buildCreateTaskJobRequest({
      projectId: "p",
      targetValue: "agent:a",
      jobName: " task ",
      prompt: " run ",
      now,
    }),
    {
      name: "task",
      enabled: true,
      projectId: "p",
      targetKind: 0,
      agentId: "a",
      instructions: "run",
      schedule: { kind: "Once", at: "2026-11-12T08:00:10.000Z" },
    },
  );
});

test("createQuickTaskAt is ten seconds after the current time", () => {
  assert.equal(createQuickTaskAt(new Date("2026-11-12T08:00:00Z")), "2026-11-12T08:00:10.000Z");
});
