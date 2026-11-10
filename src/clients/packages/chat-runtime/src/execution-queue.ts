import type { AiMessage } from "@agw/api";
import type { ChatImageAttachment } from "@agw/chat-core";

import type { ExecutionTarget } from "./execution-session";

/**
 * 一次提交的完整输入；提交时选择的执行目标随它保存。
 * The complete input of one submission; the execution target chosen at submission is kept with it.
 */
export type ExecutionSubmission = {
  target: ExecutionTarget;
  text: string;
  attachments: readonly ChatImageAttachment[];
  /** 附带的代码评论数量，用于判断能否提交与展示。The number of attached code comments, for submit checks and display. */
  fileCommentCount: number;
  /**
   * 用条目当前的文字生成用户消息；代码评论等附加内容由提交方封装在这里。
   * Creates the user message from the entry's current text; the submitter wraps code comments and other extras here.
   */
  createMessage(text: string, messageId: string): AiMessage;
};

export type QueuedExecution = ExecutionSubmission & {
  /** 条目标识，也是发送时用户消息的 messageId。The entry ID, also the user message's messageId when sent. */
  id: string;
  /** 发送使用的执行标识；重发沿用，服务端据此只受理一次。The execution ID used to send; a resend reuses it so the server accepts it once. */
  turnId: string;
  /**
   * 上一次发送的结果无法确认：重发沿用同一 turnId，文字不能再编辑。
   * The previous send's outcome is unknown: a resend reuses the same turnId, and the text can no longer be edited.
   */
  uncertain: boolean;
  /**
   * 由未送达的 Steer 退回而来：它已回到队首等待发送。
   * Returned from a Steer that was not delivered: it waits at the queue head.
   */
  steerReturned?: boolean;
};

/** 提交方式：排到队尾等当前 Turn 结束，或送入进行中的 Turn。How a submission goes: join the queue until the turn ends, or enter the running turn. */
export type SubmissionMode = "queue" | "steer";

/**
 * 一条已发给服务端、等待送达进行中 Turn 的 Steer。id 是 steerId，送达后也是用户消息的 messageId。
 * A Steer sent to the server and waiting to be delivered into the running turn. Its id is the steerId, also the user message's messageId once delivered.
 */
export type SteerRecord = {
  id: string;
  /** 提交时进行中的 Turn。The turn running at submission. */
  turnId: string;
  submission: ExecutionSubmission;
  /** sending：命令已发出，尚未得到回应；accepted：服务端已受理，等待送达。sending: the command was sent and not answered; accepted: the server took it and delivery is pending. */
  status: "sending" | "accepted";
};

export type ExecutionQueueSnapshot = {
  /** 等待送达的 Steer，按提交顺序。Steers waiting for delivery, in submission order. */
  steers: readonly SteerRecord[];
  items: readonly QueuedExecution[];
  paused: boolean;
  /** 暂停原因。Why the queue paused. */
  pauseReason: string | null;
  editingItemId: string | null;
  /** 用户已请求终止，执行尚未结束。The user requested a stop and the execution has not ended yet. */
  stopping: boolean;
};

export const EMPTY_EXECUTION_QUEUE: ExecutionQueueSnapshot = Object.freeze({
  steers: [],
  items: [],
  paused: false,
  pauseReason: null,
  editingItemId: null,
  stopping: false,
});

/** 立即开始发送，排在队列中等待，或作为 Steer 发往进行中的 Turn。Sending started at once, the entry waits in the queue, or it goes to the running turn as a Steer. */
export type SubmissionResult = "started" | "queued" | "steering";

/**
 * 可提交的内容：非空白文字，或至少一条代码评论；图片只能与它们一起提交。
 * Submittable content: non-blank text or at least one code comment; images can only go with them.
 */
export function isSubmittableInput(input: { text: string; fileCommentCount: number }): boolean {
  return input.text.trim().length > 0 || input.fileCommentCount > 0;
}
