import assert from "node:assert/strict";
import test from "node:test";

import { isTaskMissingFromHistory } from "./task-history-refresh";

test("isTaskMissingFromHistory detects the current task is absent from loaded history", () => {
  assert.equal(
    isTaskMissingFromHistory(
      [
        {
          taskId: "task-1",
        },
      ],
      "task-2",
    ),
    true,
  );
});

test("isTaskMissingFromHistory returns false when the current task is loaded", () => {
  assert.equal(
    isTaskMissingFromHistory(
      [
        {
          taskId: "task-1",
        },
      ],
      "task-1",
    ),
    false,
  );
});

test("isTaskMissingFromHistory returns false when there is no current task", () => {
  assert.equal(isTaskMissingFromHistory([], null), false);
});
