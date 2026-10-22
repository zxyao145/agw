import type { ExtensionApi } from "@agw/extension-sdk";
import type { SupportLanguage } from "prettier";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";

import { DocumentError } from "../shared/document-error";
import {
  EncodingMenu,
  PopupButton,
  SaveButton,
  SaveStatus,
  useDocumentSession,
} from "../shared/editor-chrome";
import { errorMessage, notifyFailure } from "../shared/errors";
import type { CodeDocument } from "./code-document";
import {
  resolveTextEditorFontSizes,
  type TextEditorSettings,
} from "../shared/text-editor-settings";
import { monaco } from "./monaco";

type Prettier = {
  formatting: typeof import("./formatting");
  languages: readonly SupportLanguage[];
};

type EditorStatus = {
  cursor: string;
  indentation: string;
  eol: "LF" | "CRLF";
  languageId: string;
};

const { EndOfLineSequence } = monaco.editor;
// JSON 使用 JSON 语言服务的格式化。JSON uses the JSON language service's formatting.
const JSON_LANGUAGE = "json";

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
 * Code 查看器（第 6 节）：工具栏（保存状态、Format、换行开关、Save）、Monaco 编辑器与状态栏；文档会话为 Failed 时显示
 * 说明面板。
 * The Code viewer (section 6): the toolbar (save status, Format, word wrap, Save), the Monaco editor and the status
 * bar; a Failed document session shows the explanation panel.
 */
export function CodeView({
  api,
  codeDocument,
  settings,
}: {
  api: ExtensionApi;
  codeDocument: CodeDocument;
  settings: TextEditorSettings;
}) {
  const { session } = codeDocument;
  const view = useDocumentSession(session);
  const hostRef = useRef<HTMLDivElement>(null);
  const [editor, setEditor] = useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  const [model, setModel] = useState(codeDocument.model);
  const [status, setStatus] = useState<EditorStatus | null>(null);
  const [prettier, setPrettier] = useState<Prettier | null>(null);
  const [wordWrap, setWordWrap] = useState(false);
  const [languageMenu, setLanguageMenu] = useState(false);
  const languages = useMemo(
    () =>
      monaco.languages
        .getLanguages()
        .map((language) => ({ id: language.id, name: language.aliases?.[0] ?? language.id }))
        .sort((left, right) => left.name.localeCompare(right.name)),
    [],
  );

  useLayoutEffect(() => {
    // 宿主的等宽字体变量，由 theme.apply() 写到页面上。The host's monospace font variable, set by theme.apply().
    const fontFamily = getComputedStyle(document.documentElement)
      .getPropertyValue("--font-mono")
      .trim();
    if (!fontFamily) throw new Error("The host theme has no --font-mono.");
    const created = monaco.editor.create(hostRef.current!, {
      model: null,
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontSize: resolveTextEditorFontSizes(settings, api.theme.get().tokens).code,
      fontFamily,
      wordWrap: "off",
    });
    // 基础字号或最小字号变化时更新编辑器。Updates the editor when the base or minimum size changes.
    const themeSubscription = api.theme.onDidChange((theme) =>
      created.updateOptions({ fontSize: resolveTextEditorFontSizes(settings, theme.tokens).code }),
    );
    setEditor(created);
    return () => {
      themeSubscription.dispose();
      created.dispose();
    };
  }, []);

  // 文档换了模型（打开另一个文件）时交给编辑器，滚动位置与光标不保留。
  // When the document replaces its model (another file), the editor takes it without keeping scroll or cursor.
  useEffect(() => {
    if (!editor) return;
    const sync = () => {
      editor.setModel(codeDocument.model);
      setModel(codeDocument.model);
    };
    sync();
    const subscription = codeDocument.onDidChangeModel(sync);
    return () => subscription.dispose();
  }, [editor, codeDocument]);

  useEffect(() => {
    if (!editor) return;
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

  useEffect(() => {
    editor?.updateOptions({ wordWrap: wordWrap ? "on" : "off" });
  }, [editor, wordWrap]);

  // 启动时加载 Prettier 与它支持的语言，不阻塞文件的打开。Loads Prettier and its languages at start without blocking the file.
  useEffect(() => {
    let active = true;
    notifyFailure(
      api,
      import("./formatting").then(async (formatting) => {
        const supported = await formatting.loadPrettierLanguages();
        if (active) setPrettier({ formatting, languages: supported });
      }),
    );
    return () => {
      active = false;
    };
  }, [api]);

  const languageId = status?.languageId ?? null;
  const fileName = codeDocument.file?.name ?? null;
  const prettierFormats =
    prettier !== null &&
    fileName !== null &&
    languageId !== JSON_LANGUAGE &&
    prettier.formatting.prettierSupports(fileName, prettier.languages);

  // 文件名受 Prettier 支持时为模型的语言注册格式化；失败时显示错误，内容不变。
  // Registers Prettier formatting for the model's language when Prettier supports the file name; failures show an
  // error and leave the content unchanged.
  useEffect(() => {
    if (prettier === null || !prettierFormats || languageId === null || fileName === null) return;
    const { formatting } = prettier;
    const registration = monaco.languages.registerDocumentFormattingEditProvider(languageId, {
      displayName: "Prettier",
      provideDocumentFormattingEdits: async (target) => {
        const options = target.getOptions();
        try {
          const text = await formatting.formatWithPrettier(target.getValue(), {
            fileName,
            tabWidth: options.indentSize,
            useTabs: !options.insertSpaces,
          });
          return [{ range: target.getFullModelRange(), text }];
        } catch (error) {
          api.ui.notify({
            type: "error",
            message: `Could not format ${fileName}: ${errorMessage(error)}`,
          });
          return [];
        }
      },
    });
    return () => registration.dispose();
  }, [api, prettier, prettierFormats, languageId, fileName]);

  // 读取中与失败时不显示编辑器。The editor is hidden while loading and when failed.
  const shownModel = view.state !== "loading" && view.state !== "failed" ? model : null;
  // 新打开的文件显示后编辑器获得焦点，键盘输入直接进入编辑器。A newly shown file focuses the editor for typing.
  useEffect(() => {
    if (shownModel !== null) editor?.focus();
  }, [editor, shownModel]);

  const canFormat = languageId === JSON_LANGUAGE || prettierFormats;
  const formatTitle =
    canFormat || prettier === null || languageId === null
      ? undefined
      : `No formatter for ${languageName(languageId)}`;
  const formatDocument = () => {
    const action = editor?.getAction("editor.action.formatDocument");
    if (!action) throw new Error("The Format Document action is not available.");
    void action.run();
  };

  return (
    <div className="code-viewer">
      <div className="code-toolbar" hidden={shownModel === null}>
        <SaveStatus api={api} session={session} />
        <button
          type="button"
          className="chrome-button"
          disabled={!canFormat}
          title={formatTitle}
          onClick={formatDocument}
        >
          Format
        </button>
        <button
          type="button"
          className="chrome-button"
          aria-pressed={wordWrap}
          onClick={() => setWordWrap((current) => !current)}
        >
          Word wrap
        </button>
        <SaveButton api={api} session={session} />
      </div>
      {view.state === "loading" ? <p className="status">Loading {view.file.name}…</p> : null}
      {view.state === "failed" ? (
        <DocumentError
          fileName={view.file.name}
          error={view.error}
          listEncodings={() => api.fs.listTextEncodings()}
          onReopenWithEncoding={(encoding) =>
            notifyFailure(api, session.reopenWithEncoding(encoding))
          }
        />
      ) : null}
      <div className="code-editor" ref={hostRef} hidden={shownModel === null} />
      {shownModel !== null && status !== null ? (
        <div className="code-status-bar">
          <span>{status.cursor}</span>
          <span>{status.indentation}</span>
          <EncodingMenu api={api} session={session} placement="above" />
          <button
            type="button"
            className="chrome-button"
            onClick={() =>
              shownModel.setEOL(
                status.eol === "CRLF" ? EndOfLineSequence.LF : EndOfLineSequence.CRLF,
              )
            }
          >
            {status.eol}
          </button>
          <PopupButton
            label={languageName(status.languageId)}
            placement="above"
            open={languageMenu}
            onOpenChange={setLanguageMenu}
          >
            {languages.map((language) => (
              <button
                key={language.id}
                type="button"
                role="menuitem"
                data-language={language.id}
                onClick={() => {
                  setLanguageMenu(false);
                  monaco.editor.setModelLanguage(shownModel, language.id);
                }}
              >
                {language.name}
              </button>
            ))}
          </PopupButton>
        </div>
      ) : null}
    </div>
  );
}
