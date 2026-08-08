import assert from "node:assert/strict";
import test from "node:test";
import { normalizeMathDelimiters } from "./math-markdown";

test("normalizes inline LaTeX delimiters", () => {
  const markdown = String.raw`验证：\( S = \frac{n(n+1)}{2} \)。`;

  assert.equal(normalizeMathDelimiters(markdown), String.raw`验证：$ S = \frac{n(n+1)}{2} $。`);
});

test("normalizes display LaTeX delimiters", () => {
  const markdown = String.raw`结果：
\[
S = \sum_{i=1}^{6} i
\]`;

  assert.equal(
    normalizeMathDelimiters(markdown),
    String.raw`结果：


$$
S = \sum_{i=1}^{6} i
$$

`,
  );
});

test("keeps math delimiters inside inline and fenced code unchanged", () => {
  const markdown = "Use \\(\\alpha\\), but keep `\\(beta\\)`.\n\n```text\n\\[gamma\\]\n```";

  assert.equal(
    normalizeMathDelimiters(markdown),
    "Use $\\alpha$, but keep `\\(beta\\)`.\n\n```text\n\\[gamma\\]\n```",
  );
});

test("keeps unmatched delimiters as plain text", () => {
  assert.equal(
    normalizeMathDelimiters(String.raw`unfinished \(x + 1`),
    String.raw`unfinished \(x + 1`,
  );
});
