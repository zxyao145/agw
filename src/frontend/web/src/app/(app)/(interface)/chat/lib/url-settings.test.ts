import assert from "node:assert/strict";
import test from "node:test";

import {
  decodeChatUrlSettings,
  getChatSettingsHash,
  getChatSettingsHashValue,
  encodeChatUrlSettings,
  getTargetValueFromChatUrlSettings,
} from "./url-settings";

test("chat URL settings round trip as base64 url-safe JSON", () => {
  const settings = {
    agentType: 0,
    agentId: "agent-1",
    chatSettings: {
      workspace: "D:\\source\\示例",
      envVars: [{ key: "OPENAI_API_KEY", value: "sk test value" }],
      extraSettingText: '{\n  "language": "中文"\n}',
    },
  };

  const encoded = encodeChatUrlSettings(settings);

  assert.doesNotMatch(encoded, /[+/=]/);
  assert.deepEqual(decodeChatUrlSettings(encoded), settings);
});

test("chat URL settings reject malformed values", () => {
  assert.equal(decodeChatUrlSettings("not-valid-base64url"), null);
  assert.equal(decodeChatUrlSettings(encodeChatUrlSettings({ agentType: 2 })), null);
  assert.equal(decodeChatUrlSettings(encodeChatUrlSettings({ agentType: 0, agentId: "" })), null);
  assert.equal(
    decodeChatUrlSettings(
      encodeChatUrlSettings({
        agentType: 0,
        agentId: "agent-1",
        workspace: "D:\\source\\repo",
      }),
    ),
    null,
  );
});

test("chat URL settings map agent type and id to the target select value", () => {
  assert.equal(
    getTargetValueFromChatUrlSettings({ agentType: 0, agentId: "agent-1" }),
    "agent:agent-1",
  );
  assert.equal(
    getTargetValueFromChatUrlSettings({ agentType: 1, agentId: "flow-1" }),
    "agentflow:flow-1",
  );
  assert.equal(getTargetValueFromChatUrlSettings({ agentType: null, agentId: null }), null);
});

test("chat URL settings use the settings hash fragment", () => {
  assert.equal(getChatSettingsHash("abc-123_DEF"), "#settings=abc-123_DEF");
  assert.equal(getChatSettingsHash(null), "");
  assert.equal(getChatSettingsHashValue("#settings=abc-123_DEF"), "abc-123_DEF");
  assert.equal(getChatSettingsHashValue(""), null);
  assert.equal(getChatSettingsHashValue("#other=abc-123_DEF"), null);
});
