import assert from "node:assert/strict";
import test from "node:test";

import {
  getAgentSuggestionQueryParams,
  getSuggestionTrigger,
  replaceSuggestion,
  resolveInputSuggestions,
  searchCommand,
  toCommandSource,
  toFileSuggestions,
} from "./suggestions.ts";

test("detects slash and file triggers at the active caret", () => {
  const commandInput = "Please run /dep later";
  const commandCaret = commandInput.indexOf(" later");
  assert.deepEqual(getSuggestionTrigger(commandInput, commandCaret), {
    type: "command",
    query: "dep",
    start: 11,
    end: commandCaret,
  });

  const fileInput = "Open @src/app now";
  const fileCaret = fileInput.indexOf(" now");
  assert.deepEqual(getSuggestionTrigger(fileInput, fileCaret), {
    type: "file",
    query: "src/app",
    start: 5,
    end: fileCaret,
  });

  assert.equal(getSuggestionTrigger("/deploy later", "/deploy later".length), null);
  assert.equal(getSuggestionTrigger("user@example.com", "user@example.com".length), null);
  assert.equal(getSuggestionTrigger("Please run /dep", -1), null);
});

test("replaces only the trigger and returns the restored caret", () => {
  const input = "为什么 @for要同时执行";
  const caretIndex = input.indexOf("要");
  const value = "为什么 @format.sh 要同时执行";

  assert.deepEqual(replaceSuggestion(input, "@format.sh", caretIndex), {
    value,
    caretIndex: value.indexOf("要"),
  });

  assert.deepEqual(replaceSuggestion("Open @src/a later", "@src/app.ts", 11), {
    value: "Open @src/app.ts later",
    caretIndex: "Open @src/app.ts ".length,
  });

  const spacedPath = '@"post/agw/Agw 介绍.md"';
  assert.deepEqual(replaceSuggestion("Open @Agw", spacedPath, "Open @Agw".length), {
    value: `Open ${spacedPath} `,
    caretIndex: `Open ${spacedPath} `.length,
  });
});

test("searches system and Claude commands while preserving the five item limit", () => {
  const systemSuggestions = searchCommand("deploy", {
    mode: "system",
    suggestions: [
      { text: "/deploy", description: "Skill · Deploy", kind: "skill" },
      { text: "/deploy", description: "Tool · Operations", kind: "tool" },
      { text: "/status", description: "Skill · Custom status", kind: "skill" },
    ],
  });
  assert.deepEqual(
    systemSuggestions.map((suggestion) => suggestion.kind),
    ["skill", "tool"],
  );

  assert.deepEqual(
    searchCommand("", {
      mode: "claudeCode",
      slashCommands: ["custom", "/help", "/compact"],
    }).map((suggestion) => suggestion.text),
    ["/compact", "/clear", "/status", "/custom"],
  );
  assert.deepEqual(searchCommand("", { mode: "unsupported" }), []);

  const many = Array.from({ length: 8 }, (_, index) => ({
    text: `/command-${index}`,
    description: "Skill",
    kind: "skill" as const,
  }));
  assert.equal(searchCommand("", { mode: "system", suggestions: many }).length, 5);
});

test("converts API modes and enables queries only for single agents", () => {
  assert.deepEqual(getAgentSuggestionQueryParams("project-1", { id: "agent-1", type: "agent" }), {
    projectId: "project-1",
    agentId: "agent-1",
  });
  assert.equal(
    getAgentSuggestionQueryParams("project-1", { id: "flow-1", type: "agentflow" }),
    null,
  );

  assert.deepEqual(toCommandSource({ mode: "claudeCode", suggestions: [] }, ["custom"]), {
    mode: "claudeCode",
    slashCommands: ["custom"],
  });
  assert.deepEqual(toCommandSource({ mode: "unsupported", suggestions: [] }, []), {
    mode: "unsupported",
  });
});

test("maps file results and routes file suggestions through the shared resolver", async () => {
  const candidates = Array.from({ length: 7 }, (_, index) => ({
    relativePath: `src/file-${index}.ts`,
    fullPath: `/workspace/src/file-${index}.ts`,
  }));
  assert.deepEqual(toFileSuggestions(candidates).at(0), {
    text: "@src/file-0.ts",
    description: "/workspace/src/file-0.ts",
  });
  assert.deepEqual(
    toFileSuggestions([
      {
        relativePath: "post/agw/Agw 介绍.md",
        fullPath: "/workspace/post/agw/Agw 介绍.md",
      },
    ]).at(0),
    {
      text: '@"post/agw/Agw 介绍.md"',
      description: "/workspace/post/agw/Agw 介绍.md",
    },
  );
  assert.equal(toFileSuggestions(candidates).length, 5);

  let query = "";
  const resolved = await resolveInputSuggestions(
    "Open @src/fi",
    "Open @src/fi".length,
    { mode: "unsupported" },
    async (keyword) => {
      query = keyword;
      return toFileSuggestions(candidates);
    },
  );
  assert.equal(query, "src/fi");
  assert.equal(resolved.length, 5);
  const email = "email@example.com";
  assert.deepEqual(resolveInputSuggestions(email, email.length, { mode: "unsupported" }), []);
});

test("routes an explicitly entered hidden path to file search", async () => {
  let query = "";

  await resolveInputSuggestions(
    "Open @./.github/w",
    "Open @./.github/w".length,
    { mode: "unsupported" },
    async (keyword) => {
      query = keyword;
      return [];
    },
  );

  assert.equal(query, "./.github/w");
});
