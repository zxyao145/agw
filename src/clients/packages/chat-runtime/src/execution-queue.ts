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
  executionId: string;
  /**
   * 上一次发送的结果无法确认：重发沿用同一 executionId，文字不能再编辑。
   * The previous send's outcome is unknown: a resend reuses the same executionId, and the text can no longer be edited.
   */
  uncertain: boolean;
};

export type ExecutionQueueSnapshot = {
  items: readonly QueuedExecution[];
  paused: boolean;
  /** 暂停原因。Why the queue paused. */
  pauseReason: string | null;
  editingItemId: string | null;
  /** 用户已请求终止，执行尚未结束。The user requested a stop and the execution has not ended yet. */
  stopping: boolean;
};

export const EMPTY_EXECUTION_QUEUE: ExecutionQueueSnapshot = Object.freeze({
  items: [],
  paused: false,
  pauseReason: null,
  editingItemId: null,
  stopping: false,
});

/** 立即开始发送，或排在队列中等待。Sending started at once, or the entry waits in the queue. */
export type SubmissionResult = "started" | "queued";

/**
 * 可提交的内容：非空白文字，或至少一条代码评论；图片只能与它们一起提交。
 * Submittable content: non-blank text or at least one code comment; images can only go with them.
 */
export function isSubmittableInput(input: { text: string; fileCommentCount: number }): boolean {
  return input.text.trim().length > 0 || input.fileCommentCount > 0;
}
