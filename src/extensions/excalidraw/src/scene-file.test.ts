import assert from "node:assert/strict";
import test from "node:test";

import { parseSceneFile } from "./scene-file";

test("parseSceneFile returns an Excalidraw scene", () => {
  const scene = {
    type: "excalidraw",
    version: 2,
    source: "https://excalidraw.com",
    elements: [{ id: "rect", type: "rectangle", version: 1 }],
    appState: { viewBackgroundColor: "#ffffff" },
    files: {},
  };
  assert.deepEqual(parseSceneFile(JSON.stringify(scene)), scene);
  assert.deepEqual(parseSceneFile('{"type":"excalidraw"}'), { type: "excalidraw" });
});

test("parseSceneFile rejects invalid JSON", () => {
  assert.throws(() => parseSceneFile(""), /^Error: The file is not valid JSON: /);
  assert.throws(
    () => parseSceneFile('{"type": "excalidraw",'),
    /^Error: The file is not valid JSON: /,
  );
});

test("parseSceneFile rejects JSON that is not an Excalidraw scene", () => {
  for (const text of [
    "[]",
    "null",
    '"excalidraw"',
    '{"elements":[]}',
    '{"type":"excalidrawlib"}',
  ]) {
    assert.throws(() => parseSceneFile(text), /is not an Excalidraw scene/, text);
  }
  assert.throws(
    () => parseSceneFile('{"type":"excalidraw","elements":{}}'),
    /"elements" field is not an array/,
  );
  assert.throws(
    () => parseSceneFile('{"type":"excalidraw","appState":[]}'),
    /"appState" field is not an object/,
  );
  assert.throws(
    () => parseSceneFile('{"type":"excalidraw","files":"x"}'),
    /"files" field is not an object/,
  );
});
