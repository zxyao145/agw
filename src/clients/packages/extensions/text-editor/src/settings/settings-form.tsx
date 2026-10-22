import type { ExtensionApi } from "@agw/extension-sdk";
import { useEffect, useRef, useState } from "react";

import { SettingsError } from "../shared/document-error";
import { errorMessage } from "../shared/errors";
import {
  DEFAULT_TEXT_EDITOR_SETTINGS,
  readTextEditorSettings,
  resolveTextEditorFontSizes,
  TEXT_EDITOR_FONT_SIZES,
  writeTextEditorSettings,
  type TextEditorSettings,
} from "../shared/text-editor-settings";

const FOLLOW = "follow";

type FormState =
  | { status: "loading" }
  | { status: "invalid"; error: Error }
  | { status: "ready"; settings: TextEditorSettings };

type SizeFieldProps = {
  id: string;
  label: string;
  description: string;
  /** 文件中保存的值，`null` 表示跟随。The stored value; `null` follows. */
  value: number | null;
  followText: string;
  resetLabel: string;
  disabled: boolean;
  onChange(value: number | null): void;
};

/** 一行设置：标签、下拉框、Reset 按钮与说明。One setting row: label, select, Reset button and description. */
function SizeField({
  id,
  label,
  description,
  value,
  followText,
  resetLabel,
  disabled,
  onChange,
}: SizeFieldProps) {
  return (
    <div className="setting-row">
      <label htmlFor={id}>{label}</label>
      <div className="setting-control">
        <select
          id={id}
          value={value === null ? FOLLOW : String(value)}
          disabled={disabled}
          aria-describedby={`${id}-description`}
          onChange={(event) =>
            onChange(event.target.value === FOLLOW ? null : Number(event.target.value))
          }
        >
          <option value={FOLLOW}>{followText}</option>
          {TEXT_EDITOR_FONT_SIZES.map((size) => (
            <option key={size} value={String(size)}>
              {size} px
            </option>
          ))}
        </select>
        <button
          type="button"
          aria-label={resetLabel}
          title={resetLabel}
          disabled={disabled}
          onClick={() => onChange(null)}
        >
          Reset
        </button>
      </div>
      <p id={`${id}-description`} className="setting-description">
        {description}
      </p>
    </div>
  );
}

/**
 * Text Editor 的设置页：读取设置文件，显示文档字号与代码字号两项。完整的设置对象保存在内存中，修改时先更新界面，再把整个
 * 对象按顺序排队写入文件；写入失败时用通知显示原因，界面回到最后一次写入成功的值。设置读取失败（文件不合法或其他读取错误）
 * 时显示错误，只有 "Reset all" 可用。
 * The Text Editor settings page: reads the settings file and shows the document and code font sizes. The whole
 * settings object lives in memory; a change updates the UI first and then writes the whole object through a serial
 * queue. A failed write shows the reason as a notification and returns the UI to the last successfully written
 * values. A failed settings read (invalid file or any other read error) shows the error and only "Reset all" works.
 */
export function SettingsForm({ api }: { api: ExtensionApi }) {
  const [state, setState] = useState<FormState>({ status: "loading" });
  const [tokens, setTokens] = useState(() => api.theme.get().tokens);
  // 最后一次写入成功（或读取到）的设置，以及写入队列的尾部。The last saved settings and the tail of the write queue.
  const savedRef = useRef<TextEditorSettings | null>(null);
  const invalidErrorRef = useRef<Error | null>(null);
  // 每次写入失败加一；入队时记录的 generation 与当前 generation 不同的写入被丢弃。
  // Incremented on every failed write; a queued write whose recorded generation differs from the current one is dropped.
  const generationRef = useRef(0);
  const queueRef = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    const subscription = api.theme.onDidChange((theme) => setTokens(theme.tokens));
    return () => subscription.dispose();
  }, [api]);

  useEffect(() => {
    let cancelled = false;
    readTextEditorSettings(api).then(
      (settings) => {
        if (cancelled) return;
        savedRef.current = settings;
        setState({ status: "ready", settings });
      },
      (error: unknown) => {
        if (cancelled) return;
        // 任何读取错误（文件不合法或其他）都进入不合法状态。Any read error (invalid file or other) enters the invalid state.
        const readError = error instanceof Error ? error : new Error(String(error));
        invalidErrorRef.current = readError;
        setState({ status: "invalid", error: readError });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [api]);

  const apply = (next: TextEditorSettings) => {
    setState({ status: "ready", settings: next });
    const generation = generationRef.current;
    queueRef.current = queueRef.current.then(async () => {
      // 排在失败写入之后的写入基于失败的状态，不再发送。Writes queued behind a failed write were built on its state and are dropped.
      if (generation !== generationRef.current) return;
      try {
        await writeTextEditorSettings(api, next);
        savedRef.current = next;
      } catch (error) {
        generationRef.current += 1;
        api.ui.notify({ type: "error", message: errorMessage(error) });
        const saved = savedRef.current;
        const invalidError = invalidErrorRef.current;
        if (saved !== null) setState({ status: "ready", settings: saved });
        else if (invalidError !== null) setState({ status: "invalid", error: invalidError });
      }
    });
  };

  if (state.status === "loading") return null;

  const invalid = state.status === "invalid";
  const settings = invalid ? DEFAULT_TEXT_EDITOR_SETTINGS : state.settings;
  const { document: followDocument } = resolveTextEditorFontSizes(
    { ...settings, documentFontSize: null },
    tokens,
  );
  const followCode = resolveTextEditorFontSizes({ ...settings, codeFontSize: null }, tokens).code;

  return (
    <div className="settings-page">
      {state.status === "invalid" ? (
        <SettingsError error={state.error} hint="Use Reset all to restore the defaults." />
      ) : null}
      <SizeField
        id="text-editor-document-font-size"
        label="Document font size"
        description="Used by Edit and Preview."
        value={settings.documentFontSize}
        followText={`Follow base size (${followDocument} px)`}
        resetLabel="Reset document font size"
        disabled={invalid}
        onChange={(documentFontSize) => apply({ ...settings, documentFontSize })}
      />
      <SizeField
        id="text-editor-code-font-size"
        label="Code editor font size"
        description="Used by Code."
        value={settings.codeFontSize}
        followText={`Follow document size (${followCode} px)`}
        resetLabel="Reset code editor font size"
        disabled={invalid}
        onChange={(codeFontSize) => apply({ ...settings, codeFontSize })}
      />
      <div className="settings-actions">
        <button type="button" onClick={() => apply({ ...DEFAULT_TEXT_EDITOR_SETTINGS })}>
          Reset all
        </button>
      </div>
    </div>
  );
}
