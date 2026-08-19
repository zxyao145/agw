import assert from "node:assert/strict";
import test from "node:test";

import { getSuggestionTrigger, replaceSuggestion } from "./suggestion-trigger.ts";

test("detects slash and file trigger fragments at the caret", () => {
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

test("selection replaces the trigger at the caret and preserves surrounding text", () => {
  const commandInput = "Please run /dep";
  assert.deepEqual(replaceSuggestion(commandInput, "/deploy", commandInput.length), {
    value: "Please run /deploy ",
    caretIndex: "Please run /deploy ".length,
  });

  const inlineInput = "为什么 @for要同时执行";
  const inlineCaret = inlineInput.indexOf("要");
  const inlineValue = "为什么 @format.sh 要同时执行";
  assert.deepEqual(replaceSuggestion(inlineInput, "@format.sh", inlineCaret), {
    value: inlineValue,
    caretIndex: inlineValue.indexOf("要"),
  });

  const spacedInput = "Open @src/a later";
  const spacedCaret = spacedInput.indexOf(" later");
  const spacedValue = "Open @src/app.ts later";
  assert.deepEqual(replaceSuggestion(spacedInput, "@src/app.ts", spacedCaret), {
    value: spacedValue,
    caretIndex: spacedValue.indexOf("later"),
  });
});
