import assert from "node:assert/strict";
import test from "node:test";

import {
  areChatSettingsParamsEquivalent,
  decodeChatUrlSettings,
  getChatSettingsHash,
  getChatSettingsHashValue,
  encodeChatUrlSettings,
  getTargetValueFromChatUrlSettings,
} from "./url-settings";

function encodeBase64UrlForTest(value: string): string {
  return Buffer.from(value, "utf8")
    .toString("base64")
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/, "");
}

test("chat URL settings round trip as base64 url-safe JSON", () => {
  const settings = {
    agentType: 0,
    agentId: "agent-1",
    chatSettings: {
      envVars: [{ key: "OPENAI_API_KEY", value: "sk test value" }],
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
  assert.equal(
    decodeChatUrlSettings(
      encodeChatUrlSettings({
        agentType: 0,
        agentId: "agent-1",
        extraSettingText: '{"foo":"bar"}',
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

test("chat URL settings compare decoded settings instead of encoded string identity", () => {
  const left = encodeChatUrlSettings({
    agentType: 1,
    agentId: "flow-1",
    chatSettings: {
      envVars: [],
    },
  });
  const right = encodeBase64UrlForTest(
    JSON.stringify(
      {
        chatSettings: {
          envVars: [],
        },
        agentId: "flow-1",
        agentType: 1,
      },
      null,
      2,
    ),
  );

  assert.notEqual(left, right);
  assert.equal(areChatSettingsParamsEquivalent(left, right), true);
  assert.equal(areChatSettingsParamsEquivalent(left, null), false);
});
