import assert from "node:assert/strict";
import test from "node:test";
import { getAcpSettingsError, readAcpRegistryAgentId, withAcpRegistryAgent } from "./acp-settings";

test("ACP settings require a registry entry or a command", () => {
  assert.equal(getAcpSettingsError("{}"), "Select an agent from the ACP registry or set command.");
  assert.ok(getAcpSettingsError('{"registry":{"agentId":"a"},"command":"b"}'));
  assert.equal(getAcpSettingsError('{"registry":{"agentId":"goose"}}'), null);
  assert.equal(getAcpSettingsError('{"command":"node","args":["agent.js"]}'), null);
});

test("ACP settings validate registry identifiers and optional command and configuration fields", () => {
  for (const settings of [
    { registry: { agentId: "Goose" } },
    { registry: { agentId: "goose", version: "1.2" } },
    { registry: { agentId: "goose" }, args: [] },
    { command: " " },
    { command: "node\n" },
    { command: "node", args: [null] },
    { command: "node", modeId: "" },
    { command: "node", configOptions: { model: 1 } },
    { command: "node", unknown: true },
  ]) {
    assert.ok(getAcpSettingsError(JSON.stringify(settings)), JSON.stringify(settings));
  }
});

test("ACP settings accept the maximum sizes and reject the first excess", () => {
  const args = (length: number) => Array.from({ length }, () => "");
  const entries = (length: number) =>
    Object.fromEntries(Array.from({ length }, (_, index) => ["option-" + index, true]));

  for (const [atLimit, overLimit] of [
    [{ registry: { agentId: "a".repeat(100) } }, { registry: { agentId: "a".repeat(101) } }],
    [{ command: "a".repeat(1024) }, { command: "a".repeat(1025) }],
    [{ args: args(64) }, { args: args(65) }],
    [{ args: ["a".repeat(4096)] }, { args: ["a".repeat(4097)] }],
    [{ authMethodId: "a".repeat(200) }, { authMethodId: "a".repeat(201) }],
    [{ modeId: "a".repeat(200) }, { modeId: "a".repeat(201) }],
    [{ configOptions: entries(32) }, { configOptions: entries(33) }],
    [
      { configOptions: { ["a".repeat(200)]: true } },
      { configOptions: { ["a".repeat(201)]: true } },
    ],
    [{ configOptions: { mode: "a".repeat(1000) } }, { configOptions: { mode: "a".repeat(1001) } }],
  ]) {
    const serialize = (settings: object) =>
      JSON.stringify("registry" in settings ? settings : { command: "node", ...settings });
    assert.equal(getAcpSettingsError(serialize(atLimit)), null);
    assert.ok(getAcpSettingsError(serialize(overLimit)));
  }
});

test("choosing a registry entry replaces the command and version while preserving other settings", () => {
  const extra = withAcpRegistryAgent(
    '{"command":"x","args":["y"],"registry":{"agentId":"a","version":"1.0.0"},"modeId":"m"}',
    "goose",
  );
  assert.equal(extra, '{"registry":{"agentId":"goose"},"modeId":"m"}');
  assert.equal(readAcpRegistryAgentId(extra), "goose");
  assert.equal(readAcpRegistryAgentId("{"), null);
});
