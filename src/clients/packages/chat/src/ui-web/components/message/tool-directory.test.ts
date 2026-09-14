import assert from "node:assert/strict";
import test from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { MessageContentType, type AiMessageContent } from "@agw/api";
import { ToolDirectoriesContext, ToolDirectoryInfo } from "./tool-directory";

const directories = [
  { id: null, name: "main", path: "/workspace/main", isPrimary: true },
  { id: "abc-123", name: "documents", path: "/data/documents", isPrimary: false },
];

function render(content: unknown, overrides: Partial<AiMessageContent> = {}) {
  return renderToStaticMarkup(
    React.createElement(
      ToolDirectoriesContext.Provider,
      { value: directories },
      React.createElement(ToolDirectoryInfo, {
        content: {
          type: MessageContentType.FunctionCallContent,
          additionalProperties: { toolName: "file_access_ls" },
          content,
          ...overrides,
        },
      }),
    ),
  );
}

test("file tool calls show the matching directory name and full path", () => {
  const args = { directoryId: "ABC-123" };
  for (const content of [args, JSON.stringify(args)]) {
    const html = render(content);
    assert.match(html, /documents/);
    assert.match(html, /\/data\/documents/);
    assert.doesNotMatch(html, /\/workspace\/main/);
  }
  assert.deepEqual(args, { directoryId: "ABC-123" });
});

test("omitted or null directory ids identify the primary directory", () => {
  for (const content of [{}, { directoryId: null }]) {
    const html = render(content);
    assert.match(html, /Primary directory/);
    assert.match(html, /\/workspace\/main/);
  }
});

test("unknown ids and incomplete arguments never fall back to another directory", () => {
  for (const content of [
    { directoryId: "removed-directory" },
    { directoryId: "" },
    { directoryId: 42 },
    '{"directoryId":',
    null,
    [],
  ]) {
    assert.equal(render(content), "");
  }
});

test("directory information is limited to file tool calls", () => {
  assert.equal(render({}, { additionalProperties: { toolName: "other_tool" } }), "");
  assert.equal(render({}, { type: MessageContentType.FunctionResultContent }), "");
});
