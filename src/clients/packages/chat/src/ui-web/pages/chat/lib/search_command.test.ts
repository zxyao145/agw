import assert from "node:assert/strict";
import test from "node:test";

import { searchCommand } from "../../../../lib/chat/search-command.ts";

test("system mode searches only API suggestions and keeps skill/tool name collisions", () => {
  const suggestions = searchCommand("deploy", {
    mode: "system",
    suggestions: [
      { text: "/deploy", description: "Skill · Deploy", kind: "skill" },
      { text: "/deploy", description: "Tool · Operations", kind: "tool" },
      { text: "/status", description: "Skill · Custom status", kind: "skill" },
    ],
  });

  assert.deepEqual(suggestions, [
    { text: "/deploy", description: "Skill · Deploy", kind: "skill" },
    { text: "/deploy", description: "Tool · Operations", kind: "tool" },
  ]);
  assert.equal(searchCommand("compact", { mode: "system", suggestions: [] }).length, 0);
});

test("system mode searches concrete tools expanded from tool blocks", () => {
  const suggestions = searchCommand("todos", {
    mode: "system",
    suggestions: [
      { text: "/todos_add", description: "Tool · Todo", kind: "tool" },
      { text: "/todos_complete", description: "Tool · Todo", kind: "tool" },
      { text: "/git_clone", description: "Tool · Git", kind: "tool" },
    ],
  });

  assert.deepEqual(
    new Set(suggestions.map((suggestion) => suggestion.text)),
    new Set(["/todos_add", "/todos_complete"]),
  );
});

test("claudeCode mode merges defaults and normalized init commands while filtering ignored commands", () => {
  const suggestions = searchCommand("", {
    mode: "claudeCode",
    slashCommands: ["custom", "/help", "/compact"],
  });

  assert.deepEqual(
    suggestions.map((suggestion) => suggestion.text),
    ["/compact", "/clear", "/status", "/custom"],
  );
});

test("unsupported mode returns no commands", () => {
  assert.deepEqual(searchCommand("", { mode: "unsupported" }), []);
});

test("all modes return at most five suggestions", () => {
  const systemSuggestions = Array.from({ length: 8 }, (_, index) => ({
    text: `/command-${index}`,
    description: "Skill",
    kind: "skill" as const,
  }));
  const claudeCommands = Array.from({ length: 8 }, (_, index) => `/custom-${index}`);

  assert.equal(searchCommand("", { mode: "system", suggestions: systemSuggestions }).length, 5);
  assert.equal(searchCommand("", { mode: "claudeCode", slashCommands: claudeCommands }).length, 5);
});
