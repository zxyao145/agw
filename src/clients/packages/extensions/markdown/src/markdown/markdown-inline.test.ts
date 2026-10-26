import assert from "node:assert/strict";
import test from "node:test";

import { createSpellingBudget, renderParagraphMarkdown } from "./markdown-inline";

// 这个文件只导入 markdown-inline.ts，Mixed 的解析函数没有登记（node:test 在单独的进程中运行每个测试文件）。
// This file imports only markdown-inline.ts, so Mixed's parser is not registered (node:test runs each test file in
// its own process).
test("writing inline content before Mixed's parser is registered throws", () => {
  assert.throws(
    () => renderParagraphMarkdown([{ type: "text", text: "a" }], createSpellingBudget()),
    /registers Mixed's parser/,
  );
});
