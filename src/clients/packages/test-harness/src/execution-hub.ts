import type { TestContext } from "node:test";
import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";

export type HubInvocation = { method: string; args: unknown[] };

export type TestHubOptions = {
  /** GetExecutionProvider 的返回值。The GetExecutionProvider result. */
  provider: "InProcess" | "Distributed";
  /**
   * 每次 hub 调用时执行；返回 undefined 时使用默认结果，抛出时这次调用失败。
   * Runs on every hub call; undefined keeps the default result, and a throw fails the call.
   */
  onInvoke?: (invocation: HubInvocation, connection: TestHubConnection) => unknown;
};

/**
 * 替代 SignalR 执行连接的测试连接：记录 hub 调用，由测试推送 ReceiveMessage、模拟自动重连与断开。
 * A test connection standing in for the SignalR execution connection: it records hub calls, and the test pushes
 * ReceiveMessage, simulates automatic reconnects and disconnects.
 */
export class TestHubConnection {
  public state: HubConnectionState = HubConnectionState.Disconnected;
  public connectionId: string | null = null;
  public readonly invocations: HubInvocation[] = [];
  private receiveMessage: ((message: unknown) => void) | null = null;
  private readonly closeHandlers: ((error?: Error) => void)[] = [];
  private readonly reconnectingHandlers: ((error?: Error) => void)[] = [];
  private readonly reconnectedHandlers: ((connectionId?: string) => void)[] = [];

  public constructor(
    private readonly options: TestHubOptions,
    /** 建立连接时 withUrl 收到的地址与选项。The URL and options withUrl received. */
    public readonly url: string,
    public readonly connectionOptions: Record<string, unknown>,
  ) {}

  public on(eventName: string, handler: (message: unknown) => void): void {
    if (eventName === "ReceiveMessage") this.receiveMessage = handler;
  }

  public onclose(handler: (error?: Error) => void): void {
    this.closeHandlers.push(handler);
  }

  public onreconnecting(handler: (error?: Error) => void): void {
    this.reconnectingHandlers.push(handler);
  }

  public onreconnected(handler: (connectionId?: string) => void): void {
    this.reconnectedHandlers.push(handler);
  }

  public async start(): Promise<void> {
    this.state = HubConnectionState.Connected;
  }

  public async stop(): Promise<void> {
    this.state = HubConnectionState.Disconnected;
  }

  public async invoke(method: string, ...args: unknown[]): Promise<unknown> {
    const invocation = { method, args };
    this.invocations.push(invocation);
    const result = await this.options.onInvoke?.(invocation, this);
    return result === undefined ? defaultResult(method, this.options.provider) : result;
  }

  /**
   * 推送一条 ReceiveMessage；会话的处理函数抛出的异常从这里抛出。
   * Pushes one ReceiveMessage; an exception thrown by the session's handler propagates from here.
   */
  public receive(message: unknown): void {
    if (!this.receiveMessage) throw new Error("The session registered no ReceiveMessage handler.");
    this.receiveMessage(message);
  }

  /**
   * SignalR 自动重连成功：先进入 Reconnecting，再恢复连接并通知会话。
   * A successful SignalR automatic reconnect: Reconnecting first, then connected again with the session notified.
   */
  public reconnect(): void {
    this.state = HubConnectionState.Reconnecting;
    for (const handler of this.reconnectingHandlers) handler();
    this.state = HubConnectionState.Connected;
    for (const handler of this.reconnectedHandlers) handler();
  }

  /**
   * 连接断开且不再自动重连。
   * The connection drops without any further automatic reconnect.
   */
  public close(error?: Error): void {
    this.state = HubConnectionState.Disconnected;
    for (const handler of this.closeHandlers) handler(error);
  }

  /**
   * DispatchCommand 发出的命令，可按命令类型筛选。
   * The commands sent through DispatchCommand, optionally of one command type.
   */
  public commands(type?: string): Record<string, unknown>[] {
    return this.invocations
      .filter((invocation) => invocation.method === "DispatchCommand")
      .map((invocation) => invocation.args[0] as Record<string, unknown>)
      .filter((command) => type === undefined || command.type === type);
  }
}

/**
 * 在本测试内替换 HubConnectionBuilder.build；返回的数组按建立顺序收集每个连接。
 * Replaces HubConnectionBuilder.build for this test; the returned array collects every connection in creation order.
 */
export function replaceExecutionHub(t: TestContext, options: TestHubOptions): TestHubConnection[] {
  const connections: TestHubConnection[] = [];
  t.mock.method(HubConnectionBuilder.prototype, "build", function (this: HubConnectionBuilder) {
    // withUrl 把地址与选项保存在构建器的 url 与 httpConnectionOptions 字段中。
    // withUrl keeps the URL and options in the builder's url and httpConnectionOptions fields.
    const builder = this as unknown as {
      url: string;
      httpConnectionOptions: Record<string, unknown>;
    };
    const connection = new TestHubConnection(options, builder.url, builder.httpConnectionOptions);
    connections.push(connection);
    return connection as never;
  });
  return connections;
}

function defaultResult(method: string, provider: TestHubOptions["provider"]): unknown {
  switch (method) {
    case "GetExecutionProvider":
      return provider;
    case "FindInProcessExecution":
      return null;
    case "RecoverInProcessExecution":
      return false;
    case "GetAgentflowCheckpoints":
      return [];
    default:
      return undefined;
  }
}
