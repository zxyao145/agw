import assert from "node:assert/strict";
import test from "node:test";

import { getManagedLabel } from "./managed-record.ts";

test("getManagedLabel names the manager of job and extension records", () => {
  assert.equal(getManagedLabel("job", "job-123"), "Managed by Job job-123");
  assert.equal(
    getManagedLabel("ext:019a4c17-0000-7000-8000-000000000001", "rule:1"),
    "Managed by extension 019a4c17-0000-7000-8000-000000000001",
  );
});

test("getManagedLabel returns null for records created by the user", () => {
  assert.equal(getManagedLabel("user", null), null);
});
