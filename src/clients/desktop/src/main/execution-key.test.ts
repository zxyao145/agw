import assert from "node:assert/strict";
import test from "node:test";

import { getExecutionKey } from "./execution-key";

test("execution key isolates server, project, and conversation", () => {
  assert.equal(
    getExecutionKey({ serverId: "local", projectId: "project-1", contextId: "context-1" }),
    "local:project-1:context-1",
  );
});
