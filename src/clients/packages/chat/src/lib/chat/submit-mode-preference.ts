"use client";

import * as React from "react";

import type { SubmissionMode } from "@agw/chat-runtime";

const STORAGE_KEY = "agw:chat-submit-mode";
const DEFAULT_SUBMIT_MODE: SubmissionMode = "queue";

const listeners = new Set<() => void>();

/**
 * 执行期间输入的默认提交方式；没有保存过偏好时为 queue。
 * The default submission mode for input sent while a turn runs; queue when no preference was saved.
 */
export const submitModePreference = {
  get(): SubmissionMode {
    return window.localStorage.getItem(STORAGE_KEY) === "steer" ? "steer" : DEFAULT_SUBMIT_MODE;
  },
  set(mode: SubmissionMode): void {
    window.localStorage.setItem(STORAGE_KEY, mode);
    for (const listener of listeners) listener();
  },
  subscribe(listener: () => void): () => void {
    listeners.add(listener);
    window.addEventListener("storage", listener);
    return () => {
      listeners.delete(listener);
      window.removeEventListener("storage", listener);
    };
  },
};

/** 读取并修改默认提交方式；服务端渲染时使用 queue。Reads and changes the default submission mode; server rendering uses queue. */
export function useSubmitModePreference(): readonly [
  SubmissionMode,
  (mode: SubmissionMode) => void,
] {
  const mode = React.useSyncExternalStore(
    submitModePreference.subscribe,
    submitModePreference.get,
    () => DEFAULT_SUBMIT_MODE,
  );
  return [mode, submitModePreference.set] as const;
}
