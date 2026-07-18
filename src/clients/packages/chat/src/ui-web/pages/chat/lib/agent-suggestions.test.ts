import assert from "node:assert/strict";
import test from "node:test";

import {
  getAgentSuggestionQueryParams,
  toCommandSource,
} from "../../../../lib/chat/agent-suggestions.ts";

test("suggestions query is enabled for a single agent with or without a project", () => {
  assert.deepEqual(
    getAgentSuggestionQueryParams("project-1", {
      id: "agent-1",
      label: "Agent",
      type: "agent",
    }),
    { projectId: "project-1", agentId: "agent-1" },
  );
  assert.deepEqual(
    getAgentSuggestionQueryParams(null, {
      id: "agent-1",
      label: "Agent",
      type: "agent",
    }),
    { agentId: "agent-1" },
  );
  assert.equal(
    getAgentSuggestionQueryParams("project-1", {
      id: "flow-1",
      label: "Flow",
      type: "agentflow",
    }),
    null,
  );
  assert.equal(getAgentSuggestionQueryParams(null, null), null);
});

test("API mode is converted to the discriminated command source", () => {
  assert.deepEqual(
    toCommandSource(
      {
        mode: "system",
        suggestions: [{ text: "/deploy", description: "Skill", kind: "skill" }],
      },
      ["custom"],
    ),
    {
      mode: "system",
      suggestions: [{ text: "/deploy", description: "Skill", kind: "skill" }],
    },
  );
  assert.deepEqual(toCommandSource({ mode: "claudeCode", suggestions: [] }, ["custom"]), {
    mode: "claudeCode",
    slashCommands: ["custom"],
  });
  assert.deepEqual(toCommandSource({ mode: "unsupported", suggestions: [] }, ["custom"]), {
    mode: "unsupported",
  });
  assert.deepEqual(toCommandSource(undefined, ["custom"]), { mode: "unsupported" });
});
