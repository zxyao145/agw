import assert from "node:assert/strict";
import { test } from "node:test";

import { floatingChatContext } from "./floating-chat-context";

test("floatingChatContext decodes a Project workspace path", () => {
  assert.deepEqual(
    floatingChatContext({
      uri: "agw-cloud://local/project-1/workspace/docs/notes%20draft.md",
      name: "notes draft.md",
      projectId: "project-1",
    }),
    {
      projectId: "project-1",
      projectRelativePath: "docs/notes draft.md",
    },
  );
});

test("floatingChatContext retains the attached directory id", () => {
  assert.deepEqual(
    floatingChatContext({
      uri: "agw-cloud://local/project-1/directory-42/notes/guide.md",
      name: "guide.md",
      projectId: "project-1",
    }),
    {
      projectId: "project-1",
      directoryId: "directory-42",
      projectRelativePath: "notes/guide.md",
    },
  );
});

test("floatingChatContext rejects a URI from another Project", () => {
  assert.throws(
    () =>
      floatingChatContext({
        uri: "agw-cloud://local/project-2/workspace/notes.md",
        name: "notes.md",
        projectId: "project-1",
      }),
    /does not match the file Project/,
  );
});
