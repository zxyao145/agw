import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const messageSource = readFileSync(new URL("./message.tsx", import.meta.url), "utf8");
const tokensCss = readFileSync(
  new URL("../../../../../components/src/ui-tokens/tokens.css", import.meta.url),
  "utf8",
);

test("light surfaces use a white background", () => {
  assert.match(tokensCss, /\/\* Colors \*\/\s*--background: #ffffff;/);
});

test("user messages use the neutral chat card", () => {
  assert.match(messageSource, /bg-\[#f3f3f4\] text-\[#17191d\]/);
});

test("historical user messages keep full-width right alignment when metadata is present", () => {
  assert.match(messageSource, /const isUser = message\.role === "user"/);
  assert.match(messageSource, /isUser \|\| isResult \? "w-full mb-8"/);
  assert.doesNotMatch(messageSource, /isUserBlock|!message\.additionalProperties/);
});
