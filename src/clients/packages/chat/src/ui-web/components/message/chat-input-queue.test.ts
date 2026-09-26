import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";
import { createUserMessage } from "@agw/chat-core";
import {
  ExecutionSessionManager,
  type ExecutionHubHandlers,
  type ExecutionRequest,
  type ExecutionSubmission,
  type ManagedExecutionHandle,
} from "@agw/chat-runtime";

const { React, act, fireEvent, render, screen } = await setupDomEnvironment();
const { ChatInputQueue } = await import("./chat-input-queue.tsx");

const key = { serverId: "local", projectId: "project-1", conversationId: "conversation-1" };

function submission(text: string, fileCommentCount = 0, imageCount = 0): ExecutionSubmission {
  const attachments = Array.from({ length: imageCount }, (_, index) => ({
    id: `image-${index}`,
    name: `image-${index}.png`,
    mediaType: "image/png" as const,
    size: 1,
    dataUrl: "data:image/png;base64,AA==",
  }));
  return {
    target: { agentId: "agent-1", agentType: 0 },
    text,
    attachments,
    fileCommentCount,
    createMessage: (value, messageId) => ({ ...createUserMessage(value, attachments), messageId }),
  };
}

/**
 * 用真实的会话管理器队列渲染列表：组件回调直接操作队列，队列变化后重新渲染。
 * Renders the list over a real session manager queue: component callbacks act on the queue, and each change re-renders.
 */
async function renderQueue() {
  let handlers!: ExecutionHubHandlers;
  let active = false;
  const executed: ExecutionRequest[] = [];
  const manager = new ExecutionSessionManager((value) => {
    handlers = value;
    return {
      configure: async () => ({ restoredDurableExecution: false }),
      hasActiveExecution: () => active,
      execute: async (request: ExecutionRequest) => {
        executed.push(request);
        active = true;
      },
      listAgentflowCheckpoints: async () => [],
      resumeCheckpoint: async () => "execution-resumed",
      setMode: async () => undefined,
      setPermissionMode: async () => undefined,
      interrupt: async () => undefined,
      interruptAndWait: async () => undefined,
      submitHumanResponse: async () => undefined,
      retryConnection: async () => undefined,
      dispose: async () => undefined,
    };
  });
  const handle: ManagedExecutionHandle = manager.attach(key, { onMessage: () => undefined });
  await handle.configure({ projectId: key.projectId, conversationId: key.conversationId });

  function Harness() {
    React.useSyncExternalStore(manager.subscribeQueue, manager.getQueueVersion);
    return React.createElement(ChatInputQueue, {
      queue: handle.getQueue(),
      onEditStart: (itemId: string) => {
        handle.setEditingQueuedItem(itemId);
        return true;
      },
      onEditCancel: () => handle.setEditingQueuedItem(null),
      onEditSave: (itemId: string, text: string) => {
        try {
          handle.updateQueuedItem(itemId, text);
          return true;
        } catch {
          return false;
        }
      },
      onRemove: (itemId: string) => handle.removeQueuedItem(itemId),
      onResume: () => handle.resumeQueue(),
    });
  }
  render(React.createElement(Harness));
  const finish = (status: "completed" | "failed") =>
    act(() => {
      const request = executed.at(-1)!;
      active = false;
      handlers.onMessage({
        messageId: `finished-${request.executionId}`,
        role: "system",
        contents: [],
        additionalProperties: {
          type: "agw-turn-finished",
          status,
          conversationId: key.conversationId,
          turnId: request.executionId,
        },
      });
    });
  const text = (request: ExecutionRequest | undefined) =>
    request?.input.contents.find((content) => content.type === "TextContent")?.content;
  return { handle, executed, finish, text };
}

test("the queue lists entries in order with their attachments", async () => {
  const { handle } = await renderQueue();

  await act(async () => {
    handle.submit(submission("running"));
    handle.submit(submission("first queued", 2, 1));
    handle.submit(submission("", 1));
  });

  const rows = screen.getAllByRole("listitem");
  assert.equal(rows.length, 2);
  assert.match(rows[0]!.textContent ?? "", /first queued/);
  assert.match(rows[0]!.textContent ?? "", /1 image/);
  assert.match(rows[0]!.textContent ?? "", /2 code comments/);
  assert.match(rows[1]!.textContent ?? "", /Code comments only/);
});

test("an inline edit keeps order and is sent after the running turn completes", async () => {
  const { handle, executed, finish, text } = await renderQueue();
  await act(async () => {
    handle.submit(submission("running"));
    handle.submit(submission("draft"));
    handle.submit(submission("after"));
  });

  fireEvent.click(screen.getByRole("button", { name: "Edit queued message 1" }));
  const editor = screen.getByRole("textbox", { name: "Edit queued message" });
  fireEvent.change(editor, { target: { value: "edited" } });
  // 队首正在编辑时，当前 Turn 完成也不发送。While the head is edited, a completed turn sends nothing.
  await finish("completed");
  assert.equal(executed.length, 1);
  fireEvent.keyDown(editor, { key: "Enter", ctrlKey: true });

  assert.deepEqual(executed.map(text), ["running", "edited"]);
  assert.equal(screen.getAllByRole("listitem").length, 1);
  assert.match(screen.getByRole("listitem").textContent ?? "", /after/);
});

test("saving blank text keeps the editor open and cancel restores the entry", async () => {
  const { handle } = await renderQueue();
  await act(async () => {
    handle.submit(submission("running"));
    handle.submit(submission("keep"));
  });

  fireEvent.click(screen.getByRole("button", { name: "Edit queued message 1" }));
  const editor = screen.getByRole("textbox", { name: "Edit queued message" });
  fireEvent.change(editor, { target: { value: "   " } });
  fireEvent.click(screen.getByRole("button", { name: "Save" }));
  assert.ok(screen.getByRole("textbox", { name: "Edit queued message" }));

  fireEvent.keyDown(editor, { key: "Escape" });
  assert.equal(screen.queryByRole("textbox", { name: "Edit queued message" }), null);
  assert.match(screen.getByRole("listitem").textContent ?? "", /keep/);
});

test("removing an entry drops it from the list", async () => {
  const { handle } = await renderQueue();
  await act(async () => {
    handle.submit(submission("running"));
    handle.submit(submission("remove me"));
    handle.submit(submission("keep me"));
  });

  fireEvent.click(screen.getByRole("button", { name: "Remove queued message 1" }));

  const rows = screen.getAllByRole("listitem");
  assert.equal(rows.length, 1);
  assert.match(rows[0]!.textContent ?? "", /keep me/);
});

test("a failed turn pauses the queue until Continue queue is pressed", async () => {
  const { handle, executed, finish, text } = await renderQueue();
  await act(async () => {
    handle.submit(submission("running"));
    handle.submit(submission("next"));
  });

  await finish("failed");
  assert.match(screen.getByRole("status").textContent ?? "", /failed/);
  assert.equal(executed.length, 1);
  fireEvent.click(screen.getByRole("button", { name: /Continue queue/ }));

  assert.deepEqual(executed.map(text), ["running", "next"]);
  assert.equal(screen.queryByRole("status"), null);
  assert.equal(screen.queryAllByRole("listitem").length, 0);
});
