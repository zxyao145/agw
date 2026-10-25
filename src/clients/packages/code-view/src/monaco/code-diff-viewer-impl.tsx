"use client";

import * as React from "react";

import type { CodeDiffSide, CodeDiffViewerProps } from "../types";
import { createCodeModel, DIFF_OPTIONS, editorOptions } from "./editor-options";
import { applyHostAppearance, readHostAppearance } from "./host-appearance";
import { monaco } from "./setup";
import {
  createEditorSide,
  disposeEditorSide,
  type EditorSide,
  useCodeViewSide,
  useTextSync,
} from "./use-code-view-side";
import { useHostAppearanceUpdates } from "./use-host-appearance";

interface DiffParts {
  editor: monaco.editor.IStandaloneDiffEditor;
  original: EditorSide;
  originalModel: monaco.editor.ITextModel;
  modified: EditorSide;
  modifiedModel: monaco.editor.ITextModel;
}

function sideLabel(side: CodeDiffSide): string {
  return side.present ? side.label : `${side.label} (no file)`;
}

/**
 * 只读的左右两栏 diff。DiffEditor 与两个 model 在挂载时创建一次，两侧的 aria 标签只能在创建时设置；文件路径变化时由
 * 外层用 key 重新挂载，scope 变化时由调用方用 key 重新挂载。
 * A read-only side-by-side diff. The DiffEditor and both models are created once on mount, and the side aria labels can
 * only be set then; the outer component remounts it by key when the file path changes, and callers remount it by key
 * when the scope changes.
 */
export default function CodeDiffViewerImpl({ filePath, original, modified }: CodeDiffViewerProps) {
  const hostRef = React.useRef<HTMLDivElement>(null);
  const [parts, setParts] = React.useState<DiffParts | null>(null);
  const [originalWidth, setOriginalWidth] = React.useState<number | null>(null);

  React.useLayoutEffect(() => {
    const host = hostRef.current!;
    const font = applyHostAppearance(readHostAppearance(host));
    const originalModel = createCodeModel(original.text, filePath);
    const modifiedModel = createCodeModel(modified.text, filePath);
    const editor = monaco.editor.createDiffEditor(host, {
      ...editorOptions(font, { readOnly: true, wordWrap: false }),
      ...DIFF_OPTIONS,
      originalAriaLabel: original.label,
      modifiedAriaLabel: modified.label,
    });
    editor.setModel({ original: originalModel, modified: modifiedModel });
    const originalEditor = editor.getOriginalEditor();
    const created: DiffParts = {
      editor,
      original: createEditorSide(originalEditor),
      originalModel,
      modified: createEditorSide(editor.getModifiedEditor()),
      modifiedModel,
    };
    // 标题的左栏与原始侧编辑器同宽，拖动分隔条时跟随。The left title matches the original editor and follows drags.
    const layout = originalEditor.onDidLayoutChange((info) => setOriginalWidth(info.width));
    setOriginalWidth(originalEditor.getLayoutInfo().width);
    setParts(created);
    return () => {
      layout.dispose();
      disposeEditorSide(created.original);
      disposeEditorSide(created.modified);
      editor.dispose();
      originalModel.dispose();
      modifiedModel.dispose();
    };
    // 只在挂载时创建；之后的文字与标签由下面的 effect 同步。Created on mount only; later text and labels sync below.
  }, []);

  useHostAppearanceUpdates(hostRef, (font) => parts?.editor.updateOptions(font));
  useTextSync(parts?.original.editor ?? null, parts?.originalModel ?? null, original.text);
  useTextSync(parts?.modified.editor ?? null, parts?.modifiedModel ?? null, modified.text);
  const originalPortals = useCodeViewSide(parts?.original ?? null, original);
  const modifiedPortals = useCodeViewSide(parts?.modified ?? null, modified);

  return (
    <>
      <div className="flex shrink-0 border-b border-border font-sans text-xs font-medium">
        <div
          className="truncate bg-red-50 px-3 py-1.5 text-red-900 dark:bg-red-950 dark:text-red-100"
          style={{ width: originalWidth ?? "50%" }}
          data-diff-side="original"
        >
          {sideLabel(original)}
        </div>
        <div
          className="min-w-0 flex-1 truncate bg-green-50 px-3 py-1.5 text-green-900 dark:bg-green-950 dark:text-green-100"
          data-diff-side="modified"
        >
          {sideLabel(modified)}
        </div>
      </div>
      <div
        ref={hostRef}
        className="agw-code-view-editor min-h-0 flex-1 font-mono"
        data-code-view="diff"
      />
      {originalPortals}
      {modifiedPortals}
    </>
  );
}
