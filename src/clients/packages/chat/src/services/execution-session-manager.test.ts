import assert from "node:assert/strict";
import test from "node:test";

import type { ExecutionHubHandlers, ExecutionReconnectState } from "./execution-hub";
import { ExecutionSessionManager } from "./execution-session-manager";

const sessionKey = {
  serverId: "server-a",
  projectId: "project-1",
  contextId: "context-1",
};

/** 创建满足会话管理器测试所需的最小执行客户端。 */
function createExecutionClient() {
  return {
    configure: async () => undefined,
    execute: async () => undefined,
    setMode: async () => undefined,
    setPermissionMode: async () => undefined,
    interrupt: async () => undefined,
    interruptAndWait: async () => undefined,
    submitHumanResponse: async () => undefined,
    retryConnection: async () => undefined,
    dispose: async () => undefined,
  };
}

test("manager preserves reconnect state while a conversation is detached", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  const receivedStates: ExecutionReconnectState[] = [];
  const firstHandle = manager.attach(sessionKey, {
    onMessage: () => undefined,
    onReconnecting: (state) => receivedStates.push(state),
  });
  const reconnectState: ExecutionReconnectState = {
    status: "reconnecting",
    retryAttempt: 3,
    retryDelayMs: 5_000,
  };

  clientHandlers?.onReconnecting?.(reconnectState);

  assert.equal(firstHandle.getReconnectState(), reconnectState);
  assert.deepEqual(receivedStates, [reconnectState]);

  firstHandle.detach();
  const secondHandle = manager.attach(sessionKey, { onMessage: () => undefined });
  assert.equal(secondHandle.getReconnectState(), reconnectState);

  clientHandlers?.onReconnected?.();
  assert.equal(secondHandle.getReconnectState(), null);
});

test("manager keeps reconnect state until the manual retry succeeds", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  let retryCount = 0;
  const retryingState: ExecutionReconnectState = {
    status: "reconnecting",
    retryAttempt: 1,
    retryDelayMs: 0,
  };
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return {
      ...createExecutionClient(),
      retryConnection: async () => {
        retryCount += 1;
        clientHandlers?.onReconnecting?.(retryingState);
      },
    };
  });
  const failedState: ExecutionReconnectState = {
    status: "failed",
    retryAttempt: 7,
    retryDelayMs: 0,
  };
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });

  clientHandlers?.onReconnectFailed?.(failedState);

  assert.equal(handle.getReconnectState(), failedState);
  await manager.retryConnection(sessionKey);
  assert.equal(retryCount, 1);
  assert.equal(handle.getReconnectState(), retryingState);

  clientHandlers?.onReconnectFailed?.(failedState);
  assert.equal(handle.getReconnectState(), failedState);

  clientHandlers?.onReconnected?.();
  assert.equal(handle.getReconnectState(), null);
});
