import assert from "node:assert/strict";
import test from "node:test";

import { extensionInstanceId } from "./extension-instance";

const EXTENSION_ID = "01a173c6-4933-766f-8182-77f77ba5b946";

test("extensionInstanceId uses the ExtensionId for a local extension", () => {
  assert.equal(extensionInstanceId(null, EXTENSION_ID), EXTENSION_ID);
});

test("extensionInstanceId isolates Server and user origins", () => {
  const origin = { serverId: "server-a", userId: "user-a" };
  const first = extensionInstanceId(origin, EXTENSION_ID);

  assert.equal(extensionInstanceId(origin, EXTENSION_ID), first);
  assert.notEqual(extensionInstanceId({ ...origin, serverId: "server-b" }, EXTENSION_ID), first);
  assert.notEqual(extensionInstanceId({ ...origin, userId: "user-b" }, EXTENSION_ID), first);
  assert.notEqual(extensionInstanceId(origin, "01a173c6-4933-766f-8182-77f77ba5b947"), first);
});
