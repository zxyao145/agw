import assert from "node:assert/strict";
import test from "node:test";

import { createToolBlockValue, createToolValue, parseToolValues } from "./tool-values.ts";

test("null and empty both mean that the owner adds no Tools", () => {
  assert.deepEqual(parseToolValues(null), []);
  assert.deepEqual(parseToolValues([]), []);
});

test("createToolValue creates the nested Tool definition with empty options", () => {
  assert.deepEqual(createToolValue("web_search"), {
    kind: "tool",
    definition: {
      name: "web_search",
      options: {},
    },
  });
});

test("createToolBlockValue creates strong default options", () => {
  assert.deepEqual(createToolBlockValue("project-memory"), {
    kind: "toolBlock",
    definition: {
      name: "project-memory",
      options: {
        storage: "database",
      },
    },
  });
  assert.deepEqual(createToolBlockValue("background-agents"), {
    kind: "toolBlock",
    definition: {
      name: "background-agents",
      options: {
        allowedAgentIds: [],
      },
    },
  });
});
