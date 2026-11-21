import assert from "node:assert/strict";
import test from "node:test";
import { isValidJobName } from "./job-name";

test("valid job name uses lowercase suffix characters", () => {
  assert.equal(isValidJobName("daily-report"), true);
  assert.equal(isValidJobName("a".repeat(190)), true);
});

test("invalid job name is rejected", () => {
  assert.equal(isValidJobName("Daily review"), false);
  assert.equal(isValidJobName("每日报告"), false);
  assert.equal(isValidJobName("a..b"), false);
  assert.equal(isValidJobName("a".repeat(191)), false);
});
