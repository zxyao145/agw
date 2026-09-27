import assert from "node:assert/strict";
import test from "node:test";

import { getExecutionKey } from "./execution-key";

test("execution key isolates server, project, and conversation", () => {
  assert.equal(
    getExecutionKey({
      serverId: "local",
      projectId: "project-1",
      conversationId: "conversation-1",
    }),
    "local:project-1:conversation-1",
  );
});
