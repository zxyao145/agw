"use client";

import * as React from "react";

import { POPUP_ITEM_CLASS, PopupButton, STATUS_BUTTON_CLASS } from "../popup-button";
import { monaco } from "./setup";

const { EndOfLineSequence } = monaco.editor;

type EditorStatus = {
  cursor: string;
  indentation: string;
  eol: "LF" | "CRLF";
  languageId: string;
};

/** 状态栏中文字项目的样式。The style of text items in the status bar. */
const STATUS_TEXT_CLASS = "whitespace-nowrap px-2";

function languageName(id: string): string {
  return monaco.languages.getLanguages().find((language) => language.id === id)?.aliases?.[0] ?? id;
}

/** 状态栏的内容；编辑器没有模型时为 null。The status bar content; null while the editor has no model. */
function readStatus(editor: monaco.editor.IStandaloneCodeEditor): EditorStatus | null {
  const model = editor.getModel();
  const position = editor.getPosition();
  if (model === null || position === null) return null;
  const selected = (editor.getSelections() ?? []).reduce(
    (count, selection) => count + model.getCharacterCountInRange(selection),
    0,
  );
  const options = model.getOptions();
  return {
    cursor: `Ln ${position.lineNumber}, Col ${position.column}${selected > 0 ? ` (${selected} selected)` : ""}`,
    indentation: options.insertSpaces
      ? `Spaces: ${options.indentSize}`
      : `Tab Size: ${options.tabSize}`,
    eol: model.getEndOfLineSequence() === EndOfLineSequence.CRLF ? "CRLF" : "LF",
    languageId: model.getLanguageId(),
  };
}

/**
 * 编辑器的状态栏（设计文档第 3 节）：光标、缩进、编码菜单（可编辑时由宿主提供）、换行与语言。
 * 可编辑时点击换行在 LF 与 CRLF 之间切换，只读时换行为文字。
 * The editor's status bar (design section 3): cursor, indentation, the encoding menu
 * (provided by the host when editable), line breaks and language. When editable, clicking the line break switches
 * between LF and CRLF; read-only shows it as text.
 */
export function StatusBar({
  editor,
  editable,
  encoding,
}: {
  editor: monaco.editor.IStandaloneCodeEditor;
  editable: boolean;
  encoding: React.ReactNode;
}) {
  const [status, setStatus] = React.useState<EditorStatus | null>(() => readStatus(editor));
  const [languageMenu, setLanguageMenu] = React.useState(false);
  const languages = React.useMemo(
    () =>
      monaco.languages
        .getLanguages()
        .map((language) => ({ id: language.id, name: language.aliases?.[0] ?? language.id }))
        .sort((left, right) => left.name.localeCompare(right.name)),
    [],
  );

  React.useEffect(() => {
    const update = () => setStatus(readStatus(editor));
    update();
    const subscriptions = [
      editor.onDidChangeModel(update),
      editor.onDidChangeCursorSelection(update),
      editor.onDidChangeModelContent(update),
      editor.onDidChangeModelOptions(update),
      editor.onDidChangeModelLanguage(update),
    ];
    return () => {
      for (const subscription of subscriptions) subscription.dispose();
    };
  }, [editor]);

  if (status === null) return null;
  return (
    <div
      className="flex h-[26px] flex-none items-center justify-end gap-1 border-t border-border px-2 font-sans text-[0.875em] text-muted-foreground"
      data-code-view-status-bar
    >
      <span className={STATUS_TEXT_CLASS}>{status.cursor}</span>
      <span className={STATUS_TEXT_CLASS}>{status.indentation}</span>
      {encoding}
      {editable ? (
        <button
          type="button"
          className={STATUS_BUTTON_CLASS}
          onClick={() =>
            editor
              .getModel()
              ?.setEOL(status.eol === "CRLF" ? EndOfLineSequence.LF : EndOfLineSequence.CRLF)
          }
        >
          {status.eol}
        </button>
      ) : (
        <span className={STATUS_TEXT_CLASS}>{status.eol}</span>
      )}
      <PopupButton
        label={languageName(status.languageId)}
        open={languageMenu}
        onOpenChange={setLanguageMenu}
      >
        {languages.map((language) => (
          <button
            key={language.id}
            type="button"
            role="menuitem"
            className={POPUP_ITEM_CLASS}
            data-language={language.id}
            onClick={() => {
              setLanguageMenu(false);
              const model = editor.getModel();
              if (model !== null) monaco.editor.setModelLanguage(model, language.id);
            }}
          >
            {language.name}
          </button>
        ))}
      </PopupButton>
    </div>
  );
}
