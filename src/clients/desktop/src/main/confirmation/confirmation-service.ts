import { randomUUID } from "node:crypto";

import type { ConfirmationAnswer, ConfirmationView } from "../../shared/contracts";

/** 一个确认窗口：`contents` 是核对 IPC 发送方用的 WebContents。One confirmation window; `contents` is the WebContents IPC senders are checked against. */
export type ConfirmationWindow = {
  readonly contents: object;
  /** 页面完成绘制后按内容高度显示。Shows the window at the content height once the page rendered. */
  show(height: number): void;
  close(): void;
};

export type ConfirmationServiceOptions = {
  /** 创建并加载确认窗口；窗口关闭时调用 `onClosed`。Creates and loads a confirmation window; `onClosed` runs when it closes. */
  openWindow(view: ConfirmationView, onClosed: () => void): ConfirmationWindow;
};

export type ConfirmationRequestOptions = {
  /** 发起请求的 WebContents，与 `clientId` 一起用于撤回请求。The requesting WebContents, used with `clientId` to withdraw the request. */
  owner?: object;
  clientId?: string;
  /** 批准之前复核请求绑定的数据（例如包摘要），失败时按取消处理。Re-checks the data bound to the request (such as a package digest) before approval; failure counts as canceled. */
  verify?: () => Promise<boolean>;
};

type PendingConfirmation = {
  requestId: string;
  view: ConfirmationView;
  owner: object | null;
  clientId: string | null;
  verify: (() => Promise<boolean>) | null;
  resolve(answer: ConfirmationAnswer | null): void;
  window: ConfirmationWindow | null;
  answering: boolean;
  settled: boolean;
};

/** 回答是否适合确认的类型：`message` 为按钮下标，其他为布尔值。Whether an answer suits the view: a button index for `message`, a boolean otherwise. */
function isValidAnswer(view: ConfirmationView, answer: unknown): answer is ConfirmationAnswer {
  if (view.kind === "message") {
    return (
      typeof answer === "number" &&
      Number.isInteger(answer) &&
      answer >= 0 &&
      answer < view.buttons.length
    );
  }
  return typeof answer === "boolean";
}

/**
 * 主进程的确认服务：每个请求有一次性标识，依次在独立的确认窗口中显示。回答只接受当前请求的确认窗口发出的 IPC，并核对发送方、
 * 请求标识与回答类型；需要批准的请求在批准前复核绑定的数据。关闭窗口、撤回或复核失败都按取消处理（结果为 null）。
 * The main process's confirmation service: every request has a one-time id and shows in a separate confirmation
 * window, one at a time. Answers are accepted only from the IPC of the current request's window, after checking the
 * sender, the request id and the answer type; requests that need approval re-check their bound data first. Closing
 * the window, withdrawing the request or a failed re-check all count as canceled (a null result).
 */
export class ConfirmationService {
  private readonly queue: PendingConfirmation[] = [];
  private active: PendingConfirmation | null = null;

  public constructor(private readonly options: ConfirmationServiceOptions) {}

  public request(
    view: ConfirmationView,
    options: ConfirmationRequestOptions = {},
  ): Promise<ConfirmationAnswer | null> {
    return new Promise((resolve) => {
      this.queue.push({
        requestId: randomUUID(),
        view,
        owner: options.owner ?? null,
        clientId: options.clientId ?? null,
        verify: options.verify ?? null,
        resolve,
        window: null,
        answering: false,
        settled: false,
      });
      this.showNext();
    });
  }

  /** 撤回 `owner` 以 `clientId` 发起的请求。Withdraws the request `owner` made with `clientId`. */
  public dismiss(owner: object, clientId: string): void {
    const matches = (item: PendingConfirmation) =>
      item.owner === owner && item.clientId === clientId;
    const queued = this.queue.findIndex(matches);
    if (queued !== -1) {
      const [item] = this.queue.splice(queued, 1);
      this.settle(item, null);
      return;
    }
    if (this.active && matches(this.active)) this.settle(this.active, null);
  }

  /** 确认窗口读取自己的请求。The confirmation window reads its request. */
  public requestFor(sender: object): { requestId: string; view: ConfirmationView } {
    const pending = this.current(sender, null);
    return { requestId: pending.requestId, view: pending.view };
  }

  public ready(sender: object, requestId: string, height: number): void {
    const pending = this.current(sender, requestId);
    if (typeof height !== "number" || !Number.isFinite(height)) {
      throw new Error("The confirmation height is invalid.");
    }
    pending.window?.show(height);
  }

  public async answer(sender: object, requestId: string, answer: unknown): Promise<void> {
    const pending = this.current(sender, requestId);
    if (!isValidAnswer(pending.view, answer)) {
      throw new Error(`${String(answer)} does not answer a ${pending.view.kind} confirmation.`);
    }
    if (pending.answering) return;
    pending.answering = true;
    if (answer === true && pending.verify && !(await pending.verify())) {
      this.settle(pending, null);
      return;
    }
    this.settle(pending, answer);
  }

  /** 当前请求，发送方必须是它的确认窗口。The current request, whose confirmation window must be the sender. */
  private current(sender: object, requestId: string | null): PendingConfirmation {
    const pending = this.active;
    if (
      !pending ||
      pending.settled ||
      pending.window?.contents !== sender ||
      (requestId !== null && requestId !== pending.requestId)
    ) {
      throw new Error("This confirmation is no longer valid.");
    }
    return pending;
  }

  private showNext(): void {
    if (this.active || this.queue.length === 0) return;
    const pending = this.queue.shift()!;
    this.active = pending;
    pending.window = this.options.openWindow(pending.view, () => this.settle(pending, null));
  }

  private settle(pending: PendingConfirmation, answer: ConfirmationAnswer | null): void {
    if (pending.settled) return;
    pending.settled = true;
    pending.resolve(answer);
    pending.window?.close();
    if (this.active === pending) {
      this.active = null;
      this.showNext();
    }
  }
}
