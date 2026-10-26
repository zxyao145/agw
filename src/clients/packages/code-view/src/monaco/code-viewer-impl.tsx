"use client";

import * as React from "react";

import type { LineChange } from "../line-remap";
import type { CodeTextModel } from "../text-model";
import type { CodeViewerProps } from "../types";
import { createCodeModel, editorOptions } from "./editor-options";
import { applyHostAppearance, readHostAppearance } from "./host-appearance";
import { monaco } from "./setup";
import { StatusBar } from "./status-bar";
import { CodeTextModelImpl } from "./text-model-impl";
import {
  createEditorSide,
  disposeEditorSide,
  type EditorSide,
  useCodeViewSide,
  useTextSync,
} from "./use-code-view-side";
import { useDocumentFormatting } from "./use-document-formatting";
import { useHostAppearanceUpdates } from "./use-host-appearance";

function requireImpl(model: CodeTextModel): CodeTextModelImpl {
  if (!(model instanceof CodeTextModelImpl)) {
    throw new Error("The text model was not created by loadCodeTextModel.");
  }
  return model;
}

/** 插入文字中的换行数。The number of line breaks in inserted text. */
function lineBreaks(text: string): number {
  let count = 0;
  for (let index = text.indexOf("\n"); index >= 0; index = text.indexOf("\n", index + 1)) count++;
  return count;
}

/**
 * Source 的编辑器与状态栏（设计文档第 1、3、5 节）。只读时编辑器使用自己的模型显示 `text`；可编辑时跟随宿主文本模型的
 * 当前模型，换模型后重新建立行区域与行操作。内容的种类与文本模型在挂载后不变，文件路径变化时由外层用 key 重新挂载。
 * Source's editor and status bar (design sections 1, 3 and 5). Read-only, the editor shows `text` in its own model;
 * editable, it follows the host text model's current model and rebuilds the line zones and action after each model
 * change. The content kind and text model stay fixed once mounted; the outer component remounts it by key when the file
 * path changes.
 */
export default function CodeViewerImpl(props: CodeViewerProps) {
  const { content } = props;
  const editable = content.kind === "editable";
  const hostRef = React.useRef<HTMLDivElement>(null);
  const [editor, setEditor] = React.useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  const [ownModel, setOwnModel] = React.useState<monaco.editor.ITextModel | null>(null);
  const [side, setSide] = React.useState<EditorSide | null>(null);
  const [fileName, setFileName] = React.useState<string | null>(null);
  const textModel = editable ? requireImpl(content.model) : null;

  React.useLayoutEffect(() => {
    const font = applyHostAppearance(readHostAppearance(hostRef.current!));
    const created = monaco.editor.create(hostRef.current!, {
      ...editorOptions(font, { readOnly: !editable, wordWrap: props.wordWrap }),
      ariaLabel: props.ariaLabel,
      model: null,
    });
    let currentSide: EditorSide | null = null;
    let subscription: monaco.IDisposable | null = null;
    let model: monaco.editor.ITextModel | null = null;
    // 换模型之前移除旧模型上的行区域与行操作。The old model's zones and action go before the model changes.
    const attach = (model: monaco.editor.ITextModel | null) => {
      if (currentSide !== null) disposeEditorSide(currentSide);
      created.setModel(model);
      currentSide = createEditorSide(created);
      setSide(currentSide);
    };
    if (textModel === null) {
      model = createCodeModel(content.kind === "text" ? content.text : "", props.filePath);
      setOwnModel(model);
      attach(model);
    } else {
      attach(textModel.current);
      setFileName(textModel.file?.name ?? null);
      subscription = textModel.onDidChangeModel(() => {
        attach(textModel.current);
        setFileName(textModel.file?.name ?? null);
      });
    }
    setEditor(created);
    return () => {
      subscription?.dispose();
      if (currentSide !== null) disposeEditorSide(currentSide);
      created.dispose();
      model?.dispose();
    };
    // 只在挂载时创建；之后的文字、标签与选项由下面的 effect 同步。Created on mount only; later text, labels and options
    // sync below.
  }, []);

  React.useLayoutEffect(() => {
    editor?.updateOptions({ ariaLabel: props.ariaLabel });
  }, [editor, props.ariaLabel]);

  React.useEffect(() => {
    editor?.updateOptions({ wordWrap: props.wordWrap ? "on" : "off" });
  }, [editor, props.wordWrap]);

  useTextSync(editor, ownModel, content.kind === "text" ? content.text : "");
  useHostAppearanceUpdates(hostRef, (font) => editor?.updateOptions(font));
  const portals = useCodeViewSide(side, props);

  const shown = content.kind === "text" || content.shown;
  // 新读入的文件显示后编辑器获得焦点，键盘输入直接进入编辑器。A newly shown file focuses the editor for typing.
  React.useEffect(() => {
    if (editable && shown && side !== null) side.editor.focus();
  }, [editable, shown, side]);

  const reportLines = React.useEffectEvent((changes: readonly LineChange[]) => {
    if (content.kind === "editable") content.onDidChangeLines(changes);
  });
  React.useEffect(() => {
    if (editor === null || !editable) return;
    const subscription = editor.onDidChangeModelContent((event) => {
      // 读入与放弃修改不移动行。Loads and discarded changes move no lines.
      if (event.isFlush || event.changes.length === 0) return;
      reportLines(
        event.changes.map((change) => ({
          startLineNumber: change.range.startLineNumber,
          endLineNumber: change.range.endLineNumber,
          insertedLineCount: lineBreaks(change.text),
        })),
      );
    });
    return () => subscription.dispose();
  }, [editor, editable]);

  useDocumentFormatting(editor, {
    enabled: editable,
    fileName,
    onFormatChange: (format) => {
      if (content.kind === "editable") content.onFormatChange(format);
    },
    onError: (message) => {
      if (content.kind === "editable") content.onError(message);
    },
  });

  return (
    <>
      <div
        ref={hostRef}
        className="agw-code-view-editor min-h-0 flex-1 font-mono"
        data-code-view="source"
        hidden={!shown}
      />
      {editor !== null && shown ? (
        <StatusBar
          editor={editor}
          editable={editable}
          encoding={content.kind === "editable" ? content.encoding : null}
        />
      ) : null}
      {portals}
    </>
  );
}
