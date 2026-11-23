import assert from "node:assert/strict";
import test from "node:test";

import { matchesNodeLibrarySearch } from "./agentflow-node-library-search";

test("node library search matches an Agent kind when the name and description do not", () => {
  assert.equal(matchesNodeLibrarySearch("agent", ["Writer", "Test configuration", "Agent"]), true);
});

test("node library search matches a Workflow kind when the name and description do not", () => {
  assert.equal(
    matchesNodeLibrarySearch("workflow as agent", [
      "Reusable flow",
      "Quality review",
      "Workflow as Agent",
    ]),
    true,
  );
});

test("node library search trims queries and ignores case", () => {
  assert.equal(matchesNodeLibrarySearch("  REVIEW ", ["Reusable flow", "Quality review"]), true);
  assert.equal(matchesNodeLibrarySearch("  missing ", ["Reusable flow", "Quality review"]), false);
});
