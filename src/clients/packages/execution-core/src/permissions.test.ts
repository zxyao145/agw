import assert from "node:assert/strict";
import test from "node:test";
import { getPermissionStatus } from "./permissions";

test("permission status separates the active turn from the next selection", () => {
  assert.deepEqual(
    getPermissionStatus({
      messageId: "status",
      contents: [],
      additionalProperties: {
        type: "permission-status",
        activePermissionMode: "alwaysAsk",
        nextPermissionMode: "fullAccess",
        permissionChangePending: true,
      },
    }),
    {
      activePermissionMode: "alwaysAsk",
      nextPermissionMode: "fullAccess",
      permissionChangePending: true,
    },
  );
  assert.equal(getPermissionStatus({ messageId: "other", contents: [] }), null);
});
