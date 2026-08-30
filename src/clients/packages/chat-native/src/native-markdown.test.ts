import assert from "node:assert/strict";
import test from "node:test";

import { padInlineCode } from "./native-markdown";

const padding = "\u202f";

test("adds narrow horizontal padding inside inline code", () => {
  assert.equal(padInlineCode("Use `foo` now."), `Use \`${padding}foo${padding}\` now.`);
});

test("supports inline code with longer backtick delimiters", () => {
  assert.equal(
    padInlineCode("Use ``foo ` bar`` now."),
    `Use \`\`${padding}foo \` bar${padding}\`\` now.`,
  );
});

test("does not modify fenced code", () => {
  const markdown = "Before `one`.\n\n```ts\nconst value = `two`;\n```";
  assert.equal(
    padInlineCode(markdown),
    `Before \`${padding}one${padding}\`.\n\n\`\`\`ts\nconst value = \`two\`;\n\`\`\``,
  );
});

test("does not duplicate existing inline code padding", () => {
  const markdown = `Use \`${padding}foo${padding}\` now.`;
  assert.equal(padInlineCode(markdown), markdown);
});
