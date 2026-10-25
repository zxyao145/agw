import * as React from "react";
import { createPortal } from "react-dom";

import type { CodeViewSideProps } from "../types";
import { LineActionController } from "./line-zone-action";
import { LineZoneLayer } from "./line-zones";
import type { monaco } from "./setup";

/**
 * 一个编辑器当前模型上的行区域与行操作。编辑器换模型后 view zone 与装饰都会清除，所以每个模型各建一个。
 * The line zones and line action on an editor's current model. Changing the model clears view zones and decorations,
 * so each model gets its own.
 */
export interface EditorSide {
  editor: monaco.editor.IStandaloneCodeEditor;
  zones: LineZoneLayer;
  actions: LineActionController;
}

export function createEditorSide(editor: monaco.editor.IStandaloneCodeEditor): EditorSide {
  return {
    editor,
    zones: new LineZoneLayer(editor),
    actions: new LineActionController(editor),
  };
}

/** 在编辑器释放或换模型之前调用。Call before the editor is disposed or changes its model. */
export function disposeEditorSide(side: EditorSide): void {
  side.actions.dispose();
  side.zones.dispose();
}

/**
 * 只读模型收到同一个文件的新文字时保留滚动位置与折叠状态。
 * New text of the same file in a read-only model keeps the scroll position and folding.
 */
export function useTextSync(
  editor: monaco.editor.IStandaloneCodeEditor | null,
  model: monaco.editor.ITextModel | null,
  text: string,
): void {
  React.useLayoutEffect(() => {
    if (editor === null || model === null || model.getValue() === text) return;
    const state = editor.saveViewState();
    model.setValue(text);
    editor.restoreViewState(state);
  }, [editor, model, text]);
}

const NO_CONTAINERS: ReadonlyMap<string, HTMLElement> = new Map();

function sameContainers(
  left: ReadonlyMap<string, HTMLElement>,
  right: ReadonlyMap<string, HTMLElement>,
): boolean {
  if (left.size !== right.size) return false;
  for (const [key, container] of left) {
    if (right.get(key) !== container) return false;
  }
  return true;
}

/**
 * 让一个编辑器显示 `props` 的行区域、行操作与整行标记；返回渲染行区域内容的 portal。
 * Makes one editor show the line zones, line action and marks of `props`; returns the portals of the zone content.
 */
export function useCodeViewSide(
  side: EditorSide | null,
  props: CodeViewSideProps,
): React.ReactNode {
  const [containers, setContainers] = React.useState(NO_CONTAINERS);

  React.useLayoutEffect(() => {
    if (side === null) return;
    const next = side.zones.sync(props.lineZones);
    setContainers((current) => (sameContainers(current, next) ? current : next));
  }, [side, props.lineZones]);

  React.useLayoutEffect(() => {
    side?.actions.setAction(props.lineZoneAction);
  }, [side, props.lineZoneAction]);

  React.useLayoutEffect(() => {
    side?.actions.setMarkedLines(props.markedLines);
  }, [side, props.markedLines]);

  // 只为当前仍存在的区域渲染 portal：区域移除时，React 先卸载内容，再由上面的 effect 移除容器。Portals render only
  // for zones still present, so a removed zone's content unmounts before the effect above removes its container.
  return props.lineZones.flatMap((zone) => {
    const container = containers.get(zone.key);
    return container === undefined
      ? []
      : [createPortal(props.renderLineZone(zone), container, zone.key)];
  });
}
