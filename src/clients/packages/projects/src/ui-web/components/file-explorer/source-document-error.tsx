import * as React from "react";

import type { SourceDocumentState, SourceEncoding } from "./source-document";

type PickerState =
  | { status: "closed" }
  | { status: "loading" }
  | { status: "open"; encodings: readonly SourceEncoding[] }
  | { status: "error"; message: string };

const PANEL_BUTTON_CLASS =
  "cursor-pointer rounded-md border border-border bg-background px-3 py-1 text-foreground hover:bg-accent hover:text-accent-foreground disabled:cursor-default disabled:opacity-60";

/**
 * 读取失败时的说明：`ERR_NOT_TEXT` 与 `ERR_UNKNOWN_ENCODING` 使用固定的文字，其他错误显示错误信息。
 * The explanation for a failed read: fixed texts for `ERR_NOT_TEXT` and `ERR_UNKNOWN_ENCODING`, the error message
 * otherwise.
 */
export function sourceErrorMessage(error: { code: string | null; message: string }): string {
  switch (error.code) {
    case "ERR_NOT_TEXT":
      return "This file is binary, so it can't be edited as text.";
    case "ERR_UNKNOWN_ENCODING":
      return "The encoding of this file couldn't be detected.";
    default:
      return error.message;
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** "Reopen with Encoding…" 按钮与编码列表。The "Reopen with Encoding…" button and the encoding list. */
function EncodingPicker({ document }: { document: SourceDocumentState }) {
  const [state, setState] = React.useState<PickerState>({ status: "closed" });
  const open = () => {
    setState({ status: "loading" });
    document.listEncodings().then(
      (encodings) => setState({ status: "open", encodings }),
      (error: unknown) => setState({ status: "error", message: errorMessage(error) }),
    );
  };

  if (state.status === "error") {
    return (
      <p className="mt-3 whitespace-pre-wrap" role="alert">
        {state.message}
      </p>
    );
  }
  if (state.status !== "open") {
    return (
      <button
        type="button"
        className={`mt-3 ${PANEL_BUTTON_CLASS}`}
        disabled={state.status === "loading"}
        onClick={open}
      >
        Reopen with Encoding…
      </button>
    );
  }
  return (
    <div className="mt-3">
      <p className="whitespace-pre-wrap">Choose the encoding to read this file with.</p>
      <ul
        className="my-2 max-h-80 max-w-90 list-none overflow-y-auto rounded-md border border-border p-1"
        aria-label="Encodings"
      >
        {state.encodings.map((encoding) => (
          <li key={encoding.id}>
            <button
              type="button"
              className="block w-full cursor-pointer rounded-md bg-background px-3 py-1 text-left text-foreground hover:bg-accent hover:text-accent-foreground"
              data-encoding={encoding.id}
              onClick={() => document.reopenWithEncoding(encoding.id)}
            >
              {encoding.label}
            </button>
          </li>
        ))}
      </ul>
      <button
        type="button"
        className={PANEL_BUTTON_CLASS}
        onClick={() => setState({ status: "closed" })}
      >
        Cancel
      </button>
    </div>
  );
}

/**
 * 读取失败时的说明面板（设计文档 5.4 节）；编码不能识别时提供 "Reopen with Encoding…"。
 * The panel for a failed read (design section 5.4); an undetected encoding offers
 * "Reopen with Encoding…".
 */
export function SourceDocumentError({
  document,
  error,
}: {
  document: SourceDocumentState;
  error: { code: string | null; message: string };
}) {
  return (
    <div className="px-8 py-6 text-[0.875em] text-muted-foreground" data-source-error>
      <div role="alert">
        <p className="mb-1 whitespace-pre-wrap font-semibold text-destructive">
          Could not read {document.fileName}
        </p>
        <p className="whitespace-pre-wrap">{sourceErrorMessage(error)}</p>
      </div>
      {error.code === "ERR_UNKNOWN_ENCODING" ? <EncodingPicker document={document} /> : null}
    </div>
  );
}
