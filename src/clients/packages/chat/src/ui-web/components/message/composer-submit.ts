import type { SubmissionMode } from "@agw/chat-runtime";

/**
 * 目标是否支持把输入送入进行中的 Turn；不支持时附带原因。
 * Whether the target accepts input into a running turn; the reason comes with "not supported".
 */
export type SteerAvailability = { supported: true } | { supported: false; reason: string };

export type ComposerSubmitInput = {
  isExecuting: boolean;
  /** 输入框中有可提交的内容。The composer holds submittable content. */
  hasContent: boolean;
  /** 用户偏好的默认提交方式。The submission mode the user prefers by default. */
  preferredMode: SubmissionMode;
  steer: SteerAvailability;
};

export type ComposerSubmitState = {
  /** send：普通发送；split：主按钮加菜单；stop：终止执行。send: plain send; split: primary button plus menu; stop: stop the execution. */
  action: "send" | "split" | "stop";
  /** 主按钮与回车使用的方式。The mode of the primary button and Enter. */
  primaryMode: SubmissionMode;
  /** 菜单与备用快捷键使用的另一种方式；没有另一种方式时为 null。The other mode, for the menu and the alternate shortcut; null when there is none. */
  alternateMode: SubmissionMode | null;
  /** 另一种方式不可用时的原因。Why the other mode is unavailable. */
  alternateBlockedReason: string | null;
  /** 偏好是 Steer 但目标不支持而回到 Queue 时的说明。Shown when the preference is Steer but the target does not support it and Queue is used. */
  fallbackNote: string | null;
};

/**
 * 备用快捷键。Ctrl+Enter 在折叠的输入框中已经等同于 Enter（发送），Ctrl/Meta/Alt+Enter 在展开的输入框中插入换行；
 * Alt+Enter 在折叠的输入框中没有占用，Mac 的 Option 与其他平台的 Alt 行为一致。
 * The alternate shortcut. Ctrl+Enter already equals Enter (send) in the collapsed composer and Ctrl/Meta/Alt+Enter inserts a newline in the expanded one;
 * Alt+Enter is unused in the collapsed composer, and Mac Option behaves like Alt elsewhere.
 */
export const ALTERNATE_SUBMIT_SHORTCUT = "Alt+Enter";
export const PRIMARY_SUBMIT_SHORTCUT = "Enter";

export const SUBMIT_MODE_LABELS: Record<SubmissionMode, string> = {
  queue: "Queue",
  steer: "Steer",
};

/**
 * 发送按钮的状态表：空闲时只有普通发送；执行中有内容时是分体按钮；执行中没有内容时是停止按钮。
 * 偏好是 Steer 但目标不支持时，主按钮使用 Queue，Steer 留在菜单中置灰。
 * The send button's state table: plain send when idle; a split button while executing with content; a stop button while executing without content.
 * With a Steer preference on a target that does not support it, the primary button uses Queue and Steer stays in the menu, disabled.
 */
export function resolveComposerSubmit(input: ComposerSubmitInput): ComposerSubmitState {
  const steerSupported = input.steer.supported;
  const primaryMode: SubmissionMode =
    input.preferredMode === "steer" && steerSupported ? "steer" : "queue";
  if (!input.isExecuting) {
    return {
      action: "send",
      primaryMode,
      alternateMode: null,
      alternateBlockedReason: null,
      fallbackNote: null,
    };
  }
  const alternateMode: SubmissionMode = primaryMode === "steer" ? "queue" : "steer";
  const unsupportedReason = input.steer.supported ? null : input.steer.reason;
  return {
    action: input.hasContent ? "split" : "stop",
    primaryMode,
    alternateMode,
    alternateBlockedReason: alternateMode === "steer" ? unsupportedReason : null,
    fallbackNote:
      input.preferredMode === "steer" && unsupportedReason !== null
        ? `Steering is unavailable, so messages go to the queue. ${unsupportedReason}`
        : null,
  };
}

export type ComposerKeyAction =
  | { type: "submit"; mode: SubmissionMode }
  | { type: "blocked"; reason: string }
  | { type: "ignore" };

/**
 * 快捷键对应的动作。Enter 使用主方式；Alt+Enter 使用另一种方式，该方式不可用时只给出原因，不发送。没有另一种方式（空闲或没有内容）时 Alt+Enter 不做任何事。
 * What a shortcut does. Enter uses the primary mode; Alt+Enter uses the other mode and, when it is unavailable, only gives the reason without sending. Alt+Enter does nothing when there is no other mode (idle, or no content).
 */
export function resolveComposerKey(
  state: ComposerSubmitState,
  key: "primary" | "alternate",
): ComposerKeyAction {
  if (key === "primary") return { type: "submit", mode: state.primaryMode };
  if (state.action !== "split" || state.alternateMode === null) return { type: "ignore" };
  if (state.alternateBlockedReason !== null) {
    return { type: "blocked", reason: state.alternateBlockedReason };
  }
  return { type: "submit", mode: state.alternateMode };
}

/** 执行中输入框的占位文字；两种提交方式各不相同。The placeholder while executing, different for each mode. */
export function getComposerPlaceholder(mode: SubmissionMode): string {
  return mode === "steer"
    ? "Type a message to steer the running task..."
    : "Type a message to queue after the running task...";
}

export function getSubmitButtonLabel(mode: SubmissionMode): string {
  return mode === "steer" ? "Steer running task" : "Queue message";
}

/** 菜单底部一次性改用另一种方式的说明。The menu footer that sends only this message with the other mode. */
export function getSendOptionLabel(mode: SubmissionMode): string {
  return mode === "steer" ? "Steer this message instead" : "Queue this message instead";
}

/**
 * 被 Hub 拒绝的 Steer 在输入框下方显示的错误：去掉 SignalR 附加的调用前缀与七位错误码，只保留服务端的说明。
 * The error shown under the input for a Steer the Hub rejected: the SignalR invocation prefix and the seven-digit code are dropped, leaving the server's explanation.
 */
export function getSteerRejectionMessage(error: Error): string {
  const detail =
    /HubException: \d{7}: ([\s\S]+)$/u.exec(error.message)?.[1]?.trim() || error.message;
  return `The message was not sent to the running task. ${detail}`;
}

/** 输入框下方提示行：当前主方式和另一种方式各自的快捷键。The hint row under the input: the shortcut of the primary and of the other mode. */
export function getSubmitShortcutHint(state: ComposerSubmitState): string | null {
  if (state.alternateMode === null) return null;
  return `${PRIMARY_SUBMIT_SHORTCUT} to ${SUBMIT_MODE_LABELS[state.primaryMode].toLowerCase()}, ${ALTERNATE_SUBMIT_SHORTCUT} to ${SUBMIT_MODE_LABELS[state.alternateMode].toLowerCase()}`;
}
