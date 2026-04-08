import assert from "node:assert/strict";
import test from "node:test";

import {
  CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME,
  CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME,
  EMPTY_EXTRA_SETTING_TEXT,
  normalizeExtraSettingTextForStorage,
} from "./chat-settings.ts";

test("chat settings dialog content uses a capped flex layout", () => {
  assert.equal(CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME, "max-h-[90vh] flex flex-col overflow-hidden");
});

test("chat settings dialog body is the dedicated scroll region", () => {
  assert.equal(CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME, "flex-1 min-h-0 overflow-y-auto pr-1");
});

test("normalizeExtraSettingTextForStorage returns undefined for blank draft text", () => {
  assert.equal(
    normalizeExtraSettingTextForStorage("", '{ "workspace": "/repo", "model": "gpt-5.4" }'),
    undefined,
  );
});

test("normalizeExtraSettingTextForStorage returns undefined when draft matches project JSON", () => {
  assert.equal(
    normalizeExtraSettingTextForStorage('{ "model": "gpt-5.4", "workspace": "/repo" }', `{
  "workspace": "/repo",
  "model": "gpt-5.4"
}`),
    undefined,
  );
});

test("normalizeExtraSettingTextForStorage keeps an explicit empty object override", () => {
  assert.equal(
    normalizeExtraSettingTextForStorage("{}", '{ "workspace": "/repo" }'),
    EMPTY_EXTRA_SETTING_TEXT,
  );
});

test("normalizeExtraSettingTextForStorage keeps project-different settings", () => {
  assert.equal(
    normalizeExtraSettingTextForStorage('{ "workspace": "/repo", "model": "gpt-5.4-mini" }', `{
  "workspace": "/repo",
  "model": "gpt-5.4"
}`),
    '{\n  "workspace": "/repo",\n  "model": "gpt-5.4-mini"\n}',
  );
});
