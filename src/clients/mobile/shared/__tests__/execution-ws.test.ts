import {
  executionReconnectDelaysMs,
  mergeStreamingMessages,
  scopeMessagesByUserTurn,
} from "@agw/execution-core";
import type { AgwMessage } from "../src/rn/api/agw-api-types";
import {
  buildExecCommandPayload,
  buildInterruptCommandPayload,
  buildSettingCommandPayload,
  executeWithWebSocket,
  type ExecutionWsHandle,
  type ExecutionWsRequest,
} from "../src/rn/pages/home/lib/execution-ws";

const RECORD_SEPARATOR = "\x1e";

type MockCloseEvent = { code: number; reason: string; wasClean: boolean };
type MockOptions = { headers?: Record<string, string> };

class MockWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;
  static instances: MockWebSocket[] = [];

  public static reset(): void {
    this.instances = [];
  }

  public binaryType = "blob";
  public readonly options?: MockOptions;
  public readonly url: string;
  public readyState = MockWebSocket.CONNECTING;
  public sentData: string[] = [];
  public onclose: ((event: MockCloseEvent) => void) | null = null;
  public onerror: ((event: unknown) => void) | null = null;
  public onmessage: ((event: { data: string }) => void) | null = null;
  public onopen: ((event: { target: MockWebSocket }) => void) | null = null;

  public constructor(_url: string, _protocols?: unknown, options?: MockOptions) {
    this.url = _url;
    this.options = options;
    MockWebSocket.instances.push(this);
  }

  public open(): void {
    this.readyState = MockWebSocket.OPEN;
    this.onopen?.({ target: this });
  }

  public send(data: string): void {
    this.sentData.push(data);
  }

  public emit(frame: unknown): void {
    this.onmessage?.({ data: `${JSON.stringify(frame)}${RECORD_SEPARATOR}` });
  }

  public close(code = 1000, reason = ""): void {
    this.readyState = MockWebSocket.CLOSING;
    this.readyState = MockWebSocket.CLOSED;
    this.onclose?.({ code, reason, wasClean: code === 1000 });
  }
}

type SignalRFrame = {
  type?: number;
  invocationId?: string;
  target?: string;
  arguments?: unknown[];
  [key: string]: unknown;
};

function sentFrames(ws: MockWebSocket): SignalRFrame[] {
  return ws.sentData.flatMap((payload) =>
    payload
      .split(RECORD_SEPARATOR)
      .filter(Boolean)
      .map((part) => JSON.parse(part) as SignalRFrame),
  );
}

function findInvocation(
  ws: MockWebSocket,
  predicate: (frame: SignalRFrame) => boolean,
): SignalRFrame {
  const frame = sentFrames(ws).find(
    (candidate) => candidate.type === 1 && candidate.invocationId && predicate(candidate),
  );
  if (!frame) throw new Error(`Invocation not found in ${JSON.stringify(sentFrames(ws))}`);
  return frame;
}

function findDispatch(ws: MockWebSocket, commandType: string): SignalRFrame {
  return findInvocation(
    ws,
    (frame) =>
      frame.target === "DispatchCommand" &&
      (frame.arguments?.[0] as { type?: string } | undefined)?.type === commandType,
  );
}

function complete(ws: MockWebSocket, invocation: SignalRFrame, result?: unknown): void {
  ws.emit({ type: 3, invocationId: invocation.invocationId, result });
}

async function flushMicrotasks(count = 12): Promise<void> {
  for (let index = 0; index < count; index += 1) await Promise.resolve();
}

async function flushReconnectTimer(): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, 0));
  await flushMicrotasks();
}

async function latestWebSocket(expectedCount = 1): Promise<MockWebSocket> {
  await flushMicrotasks();
  const ws = MockWebSocket.instances[expectedCount - 1];
  if (!ws) throw new Error(`Expected ${expectedCount} WebSocket instance(s)`);
  return ws;
}

async function completeHandshake(ws: MockWebSocket): Promise<void> {
  ws.open();
  await flushMicrotasks();
  expect(sentFrames(ws)[0]).toEqual({ protocol: "json", version: 1 });
  // ASP.NET Core SignalR 的标准成功握手只有空对象和 record separator。
  ws.emit({});
  await flushMicrotasks();
}

async function completeInitialization(
  handle: ExecutionWsHandle,
  provider = "InProcess",
): Promise<MockWebSocket> {
  const ws = await latestWebSocket();
  await completeHandshake(ws);

  const providerInvocation = findInvocation(ws, (frame) => frame.target === "GetExecutionProvider");
  complete(ws, providerInvocation, provider);
  await flushMicrotasks();
  complete(ws, findDispatch(ws, "SettingCommand"));
  await flushMicrotasks();
  complete(ws, findDispatch(ws, "ExecCommand"));
  await flushMicrotasks();

  // 保留引用以防调用方漏接 promise 导致测试中出现未处理 rejection。
  void handle.promise.catch(() => undefined);
  return ws;
}

function textMessage(
  messageId: string,
  role: string,
  content: string,
  author = "agent",
): AgwMessage {
  return {
    messageId,
    role,
    author,
    contents: [{ type: "TextContent", content }],
  };
}

function baseRequest(): ExecutionWsRequest {
  return {
    projectId: "project-1",
    contextId: "context-1",
    agentId: "agent-1",
    agentType: 0,
    executionId: "execution-1",
    input: {
      messageId: "message-1",
      author: "$agw",
      contents: [{ type: "TextContent", content: "run" }],
    },
  };
}

beforeEach(() => {
  MockWebSocket.reset();
  globalThis.WebSocket = MockWebSocket as unknown as typeof WebSocket;
});

afterEach(() => {
  jest.useRealTimers();
});

describe("execution-ws streaming identity", () => {
  it("keeps reused message ids independent across user turns", () => {
    const history = scopeMessagesByUserTurn([
      textMessage("user-1", "user", "one", "$agw"),
      textMessage("item_0", "assistant", "1"),
      textMessage("user-2", "user", "two", "$agw"),
      textMessage("item_0", "assistant", "2"),
    ]);
    const merged = mergeStreamingMessages(history, [
      {
        messageId: "item_0",
        role: "assistant",
        author: "agent",
        streamingScopeId: "user-2",
        contents: [{ type: "TextContent", content: "3" }],
      },
    ]);

    const itemZero = merged.filter((message) => message.messageId === "item_0");
    expect(itemZero.map((message) => message.streamingScopeId)).toEqual(["user-1", "user-2"]);
    expect(itemZero.map((message) => message.contents[0].content)).toEqual(["1", "23"]);
  });
});

describe("execution-ws shared command payloads", () => {
  it("matches Setting, Exec, and Interrupt server contracts", () => {
    expect(
      buildSettingCommandPayload({
        projectId: "project-1",
        contextId: "context-1",
        environmentVariables: { KEY: "value" },
        permissionMode: "alwaysAsk",
      }),
    ).toEqual({
      type: "SettingCommand",
      projectId: "project-1",
      contextId: "context-1",
      environmentVariables: { KEY: "value" },
      permissionMode: "alwaysAsk",
    });
    expect(buildExecCommandPayload(baseRequest())).toMatchObject({
      type: "ExecCommand",
      agentId: "agent-1",
      agentType: 0,
      executionId: "execution-1",
      stream: true,
    });
    expect(buildInterruptCommandPayload("execution-1", "stop")).toEqual({
      type: "InterruptCommand",
      executionId: "execution-1",
      reason: "stop",
    });
  });
});

describe("executeWithWebSocket official SignalR client", () => {
  it("accepts the standard empty handshake before dispatching commands", async () => {
    const messages: AgwMessage[] = [];
    const handle = executeWithWebSocket(
      "http://localhost:5015/base/",
      "token",
      baseRequest(),
      (message) => messages.push(message),
    );
    const ws = await completeInitialization(handle);

    expect(ws.url).toBe("ws://localhost:5015/base/api/hubs/exec");
    expect(ws.options?.headers?.Authorization).toBe("Bearer token");
    expect(findDispatch(ws, "SettingCommand").arguments?.[0]).toMatchObject({
      projectId: "project-1",
      contextId: "context-1",
    });
    expect(findDispatch(ws, "ExecCommand").arguments?.[0]).toMatchObject({
      executionId: "execution-1",
    });

    ws.emit({
      type: 1,
      target: "ReceiveMessage",
      arguments: [textMessage("assistant-1", "assistant", "hello")],
    });
    ws.emit({
      type: 1,
      target: "ReceiveMessage",
      arguments: [
        {
          ...textMessage("terminal-1", "system", "done", "$agw"),
          additionalProperties: { type: "turn-finished", status: "completed" },
        },
      ],
    });

    await expect(handle.promise).resolves.toBeUndefined();
    expect(messages.map((message) => message.messageId)).toEqual(["assistant-1"]);
  });

  it("rejects a standard SignalR handshake error", async () => {
    const handle = executeWithWebSocket(
      "https://api.example.com",
      "token",
      baseRequest(),
      () => undefined,
    );
    const ws = await latestWebSocket();
    ws.open();
    await flushMicrotasks();
    ws.emit({ error: "Handshake failed" });

    await expect(handle.promise).rejects.toThrow("Handshake failed");
  });

  it("tracks concurrent interrupt invocations independently", async () => {
    const handle = executeWithWebSocket(
      "http://localhost:5015",
      "token",
      baseRequest(),
      () => undefined,
    );
    const ws = await completeInitialization(handle);

    const first = handle.interrupt("first");
    const second = handle.interrupt("second");
    await flushMicrotasks();
    const interrupts = sentFrames(ws).filter(
      (frame) =>
        frame.type === 1 &&
        (frame.arguments?.[0] as { type?: string } | undefined)?.type === "InterruptCommand",
    );
    expect(interrupts).toHaveLength(2);
    expect(interrupts[0].invocationId).not.toBe(interrupts[1].invocationId);

    complete(ws, interrupts[1]);
    complete(ws, interrupts[0]);
    await expect(Promise.all([first, second])).resolves.toEqual([undefined, undefined]);
    handle.close();
    await expect(handle.promise).resolves.toBeUndefined();
  });

  it("restores a distributed execution with Setting and Subscribe cursor", async () => {
    const handle = executeWithWebSocket(
      "http://localhost:5015",
      "token",
      baseRequest(),
      () => undefined,
    );
    const firstSocket = await completeInitialization(handle, "Distributed");
    firstSocket.emit({
      type: 1,
      target: "ReceiveMessage",
      arguments: [
        {
          ...textMessage("delta-1", "assistant", "partial"),
          additionalProperties: {
            executionId: "execution-1",
            streamCursor: "3-9",
          },
        },
      ],
    });
    firstSocket.close(1006, "network lost");
    await flushReconnectTimer();

    const secondSocket = await latestWebSocket(2);
    await completeHandshake(secondSocket);
    complete(
      secondSocket,
      findInvocation(secondSocket, (frame) => frame.target === "GetExecutionProvider"),
      "Distributed",
    );
    await flushMicrotasks();
    complete(secondSocket, findDispatch(secondSocket, "SettingCommand"));
    await flushMicrotasks();

    expect(findDispatch(secondSocket, "SubscribeExecutionCommand").arguments?.[0]).toEqual({
      type: "SubscribeExecutionCommand",
      executionId: "execution-1",
      cursor: "3-9",
    });
    expect(
      sentFrames(secondSocket).some(
        (frame) => (frame.arguments?.[0] as { type?: string } | undefined)?.type === "ExecCommand",
      ),
    ).toBe(false);

    secondSocket.emit({
      type: 1,
      target: "ReceiveMessage",
      arguments: [
        {
          ...textMessage("terminal-1", "system", "done", "$agw"),
          additionalProperties: { type: "turn-finished", status: "completed" },
        },
      ],
    });
    await expect(handle.promise).resolves.toBeUndefined();
  });

  it("ends an in-process execution explicitly after reconnect", async () => {
    const handle = executeWithWebSocket(
      "http://localhost:5015",
      "token",
      baseRequest(),
      () => undefined,
    );
    const firstSocket = await completeInitialization(handle, "InProcess");
    firstSocket.close(1006, "network lost");
    await flushReconnectTimer();

    const secondSocket = await latestWebSocket(2);
    await completeHandshake(secondSocket);
    complete(
      secondSocket,
      findInvocation(secondSocket, (frame) => frame.target === "GetExecutionProvider"),
      "InProcess",
    );
    await flushMicrotasks();
    complete(secondSocket, findDispatch(secondSocket, "SettingCommand"));

    await expect(handle.promise).rejects.toThrow(
      /in-process execution cannot resume.*still be running/i,
    );
  });

  it("uses the server timeout to enter automatic reconnect", async () => {
    jest.useFakeTimers();
    const handle = executeWithWebSocket(
      "http://localhost:5015",
      "token",
      baseRequest(),
      () => undefined,
    );
    await completeInitialization(handle);

    jest.advanceTimersByTime(30_001);
    await flushMicrotasks();
    jest.runOnlyPendingTimers();
    await flushMicrotasks();

    expect(MockWebSocket.instances).toHaveLength(2);
    handle.close();
    await expect(handle.promise).resolves.toBeUndefined();
  });

  it("rejects after all shared automatic reconnect attempts fail", async () => {
    jest.useFakeTimers();
    const handle = executeWithWebSocket(
      "http://localhost:5015",
      "token",
      baseRequest(),
      () => undefined,
    );
    const firstSocket = await completeInitialization(handle);
    firstSocket.close(1006, "network lost");

    for (let index = 0; index < executionReconnectDelaysMs.length; index += 1) {
      jest.advanceTimersByTime(executionReconnectDelaysMs[index]);
      await flushMicrotasks();
      const reconnectSocket = await latestWebSocket(index + 2);
      reconnectSocket.close(1006, `retry ${index + 1} failed`);
      await flushMicrotasks();
    }

    await expect(handle.promise).rejects.toThrow(/retries exhausted.*still be running/i);
  });
});
