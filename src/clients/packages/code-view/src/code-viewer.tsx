"use client";

import * as React from "react";

import { useClientMounted } from "./client-mounted";
import type { CodeTextModel } from "./text-model";
import type { CodeViewerProps } from "./types";

const CodeViewerImpl = React.lazy(() => import("./monaco/code-viewer-impl"));

const modelKeys = new WeakMap<CodeTextModel, number>();
let nextModelKey = 0;

/** 每个文本模型对象的编号，模型换了时查看器重新挂载。A number per text model object; a new model remounts the viewer. */
function modelKey(model: CodeTextModel): number {
  let key = modelKeys.get(model);
  if (key === undefined) {
    key = nextModelKey++;
    modelKeys.set(model, key);
  }
  return key;
}

/**
 * Source 的编辑器与状态栏：只读时显示 `text`，可编辑时编辑宿主的文本模型。Monaco 在第一次显示时按需加载；编辑器的字体为
 * 宿主等宽字体，字号为 `--agw-font-size-code`（缺省为 `0.875rem`）。
 * Source's editor and status bar: read-only shows `text`, editable edits the host's text model. Monaco loads on first
 * display; the editor uses the host monospace font at `--agw-font-size-code` (`0.875rem` by default).
 */
export function CodeViewer(props: CodeViewerProps) {
  const mounted = useClientMounted();
  const { content } = props;
  const key =
    content.kind === "text"
      ? `text:${props.filePath}`
      : `editable:${modelKey(content.model)}:${props.filePath}`;
  return (
    <div className="agw-code-view flex h-full min-h-0 flex-col">
      {mounted ? (
        <React.Suspense fallback={null}>
          <CodeViewerImpl key={key} {...props} />
        </React.Suspense>
      ) : null}
    </div>
  );
}
