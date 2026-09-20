import assert from "node:assert/strict";
import test from "node:test";
import { normalizeStructuredResult } from "./structured-result";

test("structured results accept only one JSON container and preserve its tokens", () => {
  const cases = [
    ["  {} \n", "{}"],
    ["[]", "[]"],
    ['```json\n{"approved":false}\n```\n\n小结：请修改。', '{"approved":false}'],
    ['结果：\n~~~json\n[{"score":38},null]\n~~~\n完成。', '[{"score":38},null]'],
    ['{"id":9007199254740993,"score":1.00}\n小结：完成。', '{"id":9007199254740993,"score":1.00}'],
    [
      JSON.stringify({ text: 'braces {} [] ``` and "quotes" \\ 中文' }),
      JSON.stringify({ text: 'braces {} [] ``` and "quotes" \\ 中文' }),
    ],
  ];
  for (const [input, expected] of cases) {
    assert.equal(normalizeStructuredResult(input!), expected);
  }
});

test("structured results reject primitives, malformed documents and multiple containers", () => {
  for (const input of [
    "",
    "Only a summary.",
    "42",
    "true",
    "null",
    '"{}"',
    "```json\ntrue\n```",
    '```json\n"{}"\n```',
    "```json\n{}",
    '{"x":1}}',
    "[1]]",
    '```json\n{"value":}\n```',
    '{"nested":{}, invalid}',
    '[{"x":1}',
    '{"x":1,}',
    "{}\n[]",
    "```json\n{}\n```\n```json\n[]\n```",
  ]) {
    assert.equal(normalizeStructuredResult(input), null, input);
  }
});
