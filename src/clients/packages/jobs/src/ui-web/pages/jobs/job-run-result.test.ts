import assert from "node:assert/strict";
import test from "node:test";

import { getJobRunResultPresentation } from "./job-run-result.tsx";

test("getJobRunResultPresentation maps run results to labels and badge variants", () => {
  assert.deepEqual([0, 1, 2, 3].map(getJobRunResultPresentation), [
    { label: "Succeeded", variant: "default" },
    { label: "Retry", variant: "secondary" },
    { label: "Failed", variant: "destructive" },
    { label: "Unknown (3)", variant: "secondary" },
  ]);
});
