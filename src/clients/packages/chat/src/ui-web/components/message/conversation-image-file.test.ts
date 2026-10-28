import assert from "node:assert/strict";
import test from "node:test";

import { MessageContentType, type AiMessage } from "@agw/api";
import { buildConversationRenderModel } from "@agw/chat-core";
import {
  findPreviousConversationImageSnapshot,
  resolveConversationImageFileRequest,
} from "./conversation-image-file.tsx";

type ImageScope = {
  threadId: string;
  turnId: string;
  providerTurnId: string;
  producerScopeId: string;
};

function messageScopeProperties(scope: ImageScope) {
  return {
    threadId: scope.threadId,
    turnId: scope.turnId,
    producerScopeId: scope.producerScopeId,
  };
}

function imageMessage(messageId: string, path: string, uri: string, scope: ImageScope): AiMessage {
  return {
    messageId,
    role: "assistant",
    author: "codex",
    additionalProperties: { ...messageScopeProperties(scope), itemId: messageId },
    contents: [
      {
        type: MessageContentType.DataContent,
        uri,
        name: messageId,
        additionalProperties: {
          threadId: scope.threadId,
          turnId: scope.providerTurnId,
          itemId: messageId,
          imageSource: "imageView",
          imagePath: path,
        },
      },
    ],
  };
}

function resultMessage(
  messageId: string,
  path: string,
  scope: ImageScope,
  sourceMessageId?: string,
): AiMessage {
  return {
    messageId,
    role: "assistant",
    author: "codex",
    additionalProperties: {
      ...messageScopeProperties(scope),
      itemId: messageId,
      type: "result",
      ...(sourceMessageId ? { sourceMessageId } : {}),
    },
    contents: [
      {
        type: MessageContentType.TextContent,
        content: `![image](${path})`,
        additionalProperties: {
          threadId: scope.threadId,
          turnId: scope.providerTurnId,
          itemId: messageId,
        },
      },
    ],
  };
}

function transcriptMessage(
  messageId: string,
  sourceMessageId: string,
  scope: ImageScope,
): AiMessage {
  return {
    messageId,
    role: "assistant",
    author: "codex",
    additionalProperties: {
      ...messageScopeProperties(scope),
      itemId: messageId,
      sourceMessageId,
    },
    contents: [
      {
        type: MessageContentType.TextContent,
        content: "Transcript message",
        additionalProperties: {
          threadId: scope.threadId,
          turnId: scope.providerTurnId,
          itemId: messageId,
        },
      },
    ],
  };
}

test("relative image paths stay on the primary project root", () => {
  const request = resolveConversationImageFileRequest("assets/pelican.png", [
    { id: null, name: "project", path: "~/project", isPrimary: true },
    { id: "assets", name: "assets", path: "/workspace/assets", isPrimary: false },
  ]);

  assert.deepEqual(request, { path: "assets/pelican.png" });
});

test("absolute paths use the longest matching additional directory", () => {
  const request = resolveConversationImageFileRequest(
    "/workspace/project/assets/images/pelican.png",
    [
      { id: null, name: "project", path: "/workspace/project", isPrimary: true },
      { id: "project-assets", name: "assets", path: "/workspace/project/assets", isPrimary: false },
      {
        id: "project-images",
        name: "images",
        path: "/workspace/project/assets/images",
        isPrimary: false,
      },
    ],
  );

  assert.deepEqual(request, {
    path: "/workspace/project/assets/images/pelican.png",
    directoryId: "project-images",
  });
});

test("absolute path matching respects directory boundaries", () => {
  const request = resolveConversationImageFileRequest("/workspace/assets-old/pelican.png", [
    { id: "assets", name: "assets", path: "/workspace/assets", isPrimary: false },
  ]);

  assert.deepEqual(request, { path: "/workspace/assets-old/pelican.png" });
});

test("file URLs decode spaces and Unicode paths", () => {
  const request = resolveConversationImageFileRequest(
    "file:///Users/ben/project%20files/%E7%8C%AB%20%E5%9B%BE.png",
    [],
  );

  assert.deepEqual(request, { path: "/Users/ben/project files/猫 图.png" });
});

test("Markdown paths decode URL path escapes and omit query and fragment", () => {
  assert.deepEqual(
    resolveConversationImageFileRequest("/workspace/image%20one.png?size=large#preview", []),
    { path: "/workspace/image one.png" },
  );
  assert.deepEqual(
    resolveConversationImageFileRequest("assets/%E5%9B%BE.png?download=1#preview", []),
    { path: "assets/图.png" },
  );
});

test("relative traversal reaches the server with its query removed", () => {
  const request = resolveConversationImageFileRequest(
    "assets/../outside%20project/image.png?download=1#preview",
    [],
  );

  assert.deepEqual(request, { path: "assets/../outside project/image.png" });
});

test("Windows absolute paths match additional directories without case sensitivity", () => {
  const request = resolveConversationImageFileRequest("C:\\Work\\Project\\Assets\\THING.png", [
    { id: "assets", name: "assets", path: "c:/work/project/assets/", isPrimary: false },
  ]);

  assert.deepEqual(request, {
    path: "C:\\Work\\Project\\Assets\\THING.png",
    directoryId: "assets",
  });
});

test("Windows drive image paths decode escapes and omit query and fragment", () => {
  assert.deepEqual(
    resolveConversationImageFileRequest("C:/Work/image%20one.png?download=1#preview", []),
    { path: "C:/Work/image one.png" },
  );
  assert.deepEqual(
    resolveConversationImageFileRequest(
      "C:\\Work\\Project\\Assets\\%E5%9B%BE.png?download=1#preview",
      [{ id: "assets", name: "assets", path: "c:/work/project/assets", isPrimary: false }],
    ),
    { path: "C:\\Work\\Project\\Assets\\图.png", directoryId: "assets" },
  );
});

test("Windows drive traversal remains available to server validation", () => {
  const request = resolveConversationImageFileRequest(
    "C:\\Work\\Project\\Assets\\..\\outside%20image.png?download=1#preview",
    [{ id: "assets", name: "assets", path: "C:/work/project/assets", isPrimary: false }],
  );

  assert.deepEqual(request, {
    path: "C:\\Work\\Project\\Assets\\..\\outside image.png",
    directoryId: "assets",
  });
});

test("UNC image paths decode escapes, omit query and fragment, and match additional roots", () => {
  const request = resolveConversationImageFileRequest(
    "\\\\fileserver\\share\\project\\%E5%9B%BE%20one.png?download=1#preview",
    [
      {
        id: "project",
        name: "project",
        path: "\\\\fileserver\\share\\project",
        isPrimary: false,
      },
    ],
  );

  assert.deepEqual(request, {
    path: "\\\\fileserver\\share\\project\\图 one.png",
    directoryId: "project",
  });
});

test("UNC paths preserve localhost as the share host", () => {
  const request = resolveConversationImageFileRequest(
    "\\\\localhost\\share\\project\\image%20one.png?download=1#preview",
    [
      {
        id: "project",
        name: "project",
        path: "\\\\localhost\\share\\project",
        isPrimary: false,
      },
    ],
  );

  assert.deepEqual(request, {
    path: "\\\\localhost\\share\\project\\image one.png",
    directoryId: "project",
  });
});

test("UNC traversal remains available to server validation", () => {
  const request = resolveConversationImageFileRequest(
    "\\\\fileserver\\share\\project\\..\\outside.png?download=1#preview",
    [
      {
        id: "project",
        name: "project",
        path: "\\\\fileserver\\share\\project",
        isPrimary: false,
      },
    ],
  );

  assert.deepEqual(request, {
    path: "\\\\fileserver\\share\\project\\..\\outside.png",
    directoryId: "project",
  });
});

test("external image URLs remain outside the local file request flow", () => {
  for (const src of [
    "https://example.com/image.png",
    "https:%",
    "http://example.com/image.png",
    "//cdn.example/image.png",
    "data:image/png;base64,abc",
    "blob:https://example.com/image-id",
  ]) {
    assert.equal(resolveConversationImageFileRequest(src, []), null);
  }
});

test("invalid local file URLs resolve to a visible per-image error", () => {
  const resolution = resolveConversationImageFileRequest("file:///Users/ben/%ZZ.png", []);

  assert.ok(resolution && "error" in resolution);
});

test("uses the latest earlier image view from the current Codex thread, turn, and producer", () => {
  const path = "/outside-project/pelican.png";
  const scope = {
    threadId: "thread-1",
    turnId: "agw-turn-1",
    providerTurnId: "codex-turn-1",
    producerScopeId: "producer-1",
  };
  const sourceMessageId = "shared-codex-source";
  const rawMessages: AiMessage[] = [
    transcriptMessage("transcript", sourceMessageId, scope),
    imageMessage("view-first", path, "data:image/png;base64,first", scope),
    imageMessage("view-other-turn", path, "data:image/png;base64,other-turn", {
      ...scope,
      turnId: "agw-turn-2",
      providerTurnId: "codex-turn-2",
    }),
    imageMessage("view-other-thread", path, "data:image/png;base64,other-thread", {
      ...scope,
      threadId: "thread-2",
    }),
    imageMessage("view-other-producer", path, "data:image/png;base64,other-producer", {
      ...scope,
      producerScopeId: "producer-2",
    }),
    imageMessage("view-latest", path, "data:image/png;base64,latest", scope),
    resultMessage("result", path, scope, sourceMessageId),
    imageMessage("view-future", path, "data:image/png;base64,future", scope),
  ];
  const result = buildConversationRenderModel(rawMessages).find(
    (item) => item.type === "result" && item.message.source.messageId === "result",
  );
  const request = resolveConversationImageFileRequest(path, []);

  assert.ok(result?.type === "result");
  assert.ok(request && "path" in request);
  assert.equal(
    findPreviousConversationImageSnapshot(
      rawMessages,
      structuredClone(result.message.source),
      request.path,
    ),
    "data:image/png;base64,latest",
  );
});

test("does not use an image view from another Codex turn", () => {
  const path = "/outside-project/pelican.png";
  const rawMessages: AiMessage[] = [
    imageMessage("view", path, "data:image/png;base64,other-turn", {
      threadId: "thread-1",
      turnId: "agw-turn-1",
      providerTurnId: "codex-turn-1",
      producerScopeId: "producer-1",
    }),
    resultMessage("result", path, {
      threadId: "thread-1",
      turnId: "agw-turn-2",
      providerTurnId: "codex-turn-2",
      producerScopeId: "producer-1",
    }),
  ];
  const result = buildConversationRenderModel(rawMessages).find(
    (item) => item.type === "result" && item.message.source.messageId === "result",
  );
  const request = resolveConversationImageFileRequest(path, []);

  assert.ok(result?.type === "result");
  assert.ok(request && "path" in request);
  assert.equal(
    findPreviousConversationImageSnapshot(rawMessages, result.message.source, request.path),
    null,
  );
});
