import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const messageSource = readFileSync(new URL("./presented-message.tsx", import.meta.url), "utf8");
const dataContentSource = readFileSync(
  new URL("./renders/data-content.tsx", import.meta.url),
  "utf8",
);
const tokensCss = readFileSync(
  new URL("../../../../../components/src/ui-tokens/tokens.css", import.meta.url),
  "utf8",
);

test("light surfaces use a white background", () => {
  assert.match(tokensCss, /\/\* Colors \*\/\s*--background: #ffffff;/);
});

test("user messages use the neutral chat card", () => {
  assert.match(messageSource, /bg-\[#f3f3f4\][^"\n]*text-\[#17191d\]/);
});

test("historical user messages keep full-width right alignment when metadata is present", () => {
  assert.match(messageSource, /const isUser = message\.alignment === "right"/);
  assert.match(messageSource, /message\.width === "full" \|\| isUser/);
});

test("user message content is capped at eighty percent width", () => {
  assert.match(messageSource, /msg-pos-right max-w-\[80%\]/);
});

test("user image attachments align to the right edge of the message", () => {
  assert.match(messageSource, /cn\("msg-content-container", isUser \? "items-end" : ""\)/);
});

test("image attachments fit within a three-hundred-pixel height", () => {
  assert.match(dataContentSource, /max-h-\[300px\][^"\n]*object-contain/);
});
