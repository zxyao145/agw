import { useState } from "react";

import { documentErrorMessage, errorCode, errorMessage } from "./errors";

/** `fs.listTextEncodings` 返回的一项。An entry returned by `fs.listTextEncodings`. */
export type TextEncodingOption = { id: string; label: string };

type PickerState =
  | { status: "closed" }
  | { status: "loading" }
  | { status: "open"; encodings: readonly TextEncodingOption[] }
  | { status: "error"; message: string };

/** "Reopen with Encoding…" 按钮与编码列表。The "Reopen with Encoding…" button and the encoding list. */
function EncodingPicker({
  listEncodings,
  onPick,
}: {
  listEncodings(): Promise<readonly TextEncodingOption[]>;
  onPick(encoding: string): void;
}) {
  const [state, setState] = useState<PickerState>({ status: "closed" });
  const open = () => {
    setState({ status: "loading" });
    listEncodings().then(
      (encodings) => setState({ status: "open", encodings }),
      (error: unknown) => setState({ status: "error", message: errorMessage(error) }),
    );
  };

  if (state.status === "error") {
    return (
      <p className="document-error-note" role="alert">
        {state.message}
      </p>
    );
  }
  if (state.status !== "open") {
    return (
      <button type="button" disabled={state.status === "loading"} onClick={open}>
        Reopen with Encoding…
      </button>
    );
  }
  return (
    <div className="encoding-picker">
      <p className="document-error-note">Choose the encoding to read this file with.</p>
      <ul className="encoding-list" aria-label="Encodings">
        {state.encodings.map((encoding) => (
          <li key={encoding.id}>
            <button type="button" data-encoding={encoding.id} onClick={() => onPick(encoding.id)}>
              {encoding.label}
            </button>
          </li>
        ))}
      </ul>
      <button type="button" onClick={() => setState({ status: "closed" })}>
        Cancel
      </button>
    </div>
  );
}

/**
 * 设置不能读取或不合法时显示的说明面板，查看器与设置页共用；`hint` 是第二行的操作提示，两处各自传入。
 * The panel shown when the Markdown extension settings cannot be read or are invalid, shared by the viewers and the
 * settings page; `hint` is the second-line instruction, passed by each of the two.
 */
export function SettingsError({ error, hint }: { error: Error; hint: string }) {
  return (
    <div className="document-error">
      <div role="alert">
        <p className="document-error-title">Markdown settings are invalid: {error.message}</p>
        <p>{hint}</p>
      </div>
    </div>
  );
}

/**
 * 文本文档读取失败时的说明面板，Preview、Code 与 Mixed 共用。编码不能识别时显示 "Reopen with Encoding…"，
 * 用户从 `listEncodings` 的结果中选择编码后调用 `onReopenWithEncoding`。
 * The panel shown when reading a text document fails, shared by Preview, Code and Mixed. When the encoding cannot be
 * detected it offers "Reopen with Encoding…"; picking one of `listEncodings`'s encodings calls
 * `onReopenWithEncoding`.
 */
export function DocumentError({
  fileName,
  error,
  listEncodings,
  onReopenWithEncoding,
}: {
  fileName: string;
  error: unknown;
  listEncodings(): Promise<readonly TextEncodingOption[]>;
  onReopenWithEncoding(encoding: string): void;
}) {
  return (
    <div className="document-error">
      <div role="alert">
        <p className="document-error-title">Could not read {fileName}</p>
        <p>{documentErrorMessage(error)}</p>
      </div>
      {errorCode(error) === "ERR_UNKNOWN_ENCODING" ? (
        <EncodingPicker listEncodings={listEncodings} onPick={onReopenWithEncoding} />
      ) : null}
    </div>
  );
}
