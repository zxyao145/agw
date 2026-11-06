import assert from "node:assert/strict";
import test from "node:test";

import type { LineComment } from "@agw/projects";
import { buildChatContextPrompt, createChatContextPromptBuilder } from "./chat-context-prompt";

function comment(): LineComment {
  return {
    id: "comment-1",
    side: "current",
    filePath: "notes/plan.md",
    lineNumber: 4,
    content: "Keep the relative path.",
    timestamp: new Date("2026-08-19T00:00:00Z"),
  };
}

test("context prompt appends context before file comments", () => {
  const prompt = buildChatContextPrompt("review this", { projectRelativePath: "notes/plan.md" }, [
    comment(),
  ]);

  assert.ok(
    prompt.startsWith(
      'review this\n\n<chat_context>\n{\n  "projectRelativePath": "notes/plan.md"\n}\n</chat_context>',
    ),
  );
  assert.ok(prompt.includes('"comment": "Keep the relative path."'));
  assert.ok(prompt.indexOf("<chat_context>") < prompt.indexOf("<file_comments>"));
});

test("context prompt omits empty context and blank text", () => {
  assert.equal(buildChatContextPrompt("  text  ", {}), "text");
  assert.equal(
    buildChatContextPrompt("  ", { projectRelativePath: "notes/plan.md" }).startsWith(
      "<chat_context>",
    ),
    true,
  );
});

test("queued prompt builder retains its context and comments snapshot", () => {
  const context = { projectRelativePath: "notes/plan.md" };
  const fileComment = comment();
  const createPrompt = createChatContextPromptBuilder(context, [fileComment]);
  context.projectRelativePath = "notes/other.md";
  fileComment.content = "Changed after submit";

  const prompt = createPrompt("review");
  assert.ok(prompt.includes('"projectRelativePath": "notes/plan.md"'));
  assert.ok(prompt.includes('"comment": "Keep the relative path."'));
  assert.equal(prompt.includes("Changed after submit"), false);
});
