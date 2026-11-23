import type { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";

import { isOutlineLevel, outlineLabel, sameOutline, type OutlineHeading } from "../shared/outline";

export class EditOutline {
  private document: ProseMirrorNode | undefined;
  private currentHeadings: readonly OutlineHeading[] = [];
  private positions: readonly number[] = [];
  private readonly listeners = new Set<() => void>();

  public constructor(private readonly editor: Editor) {
    this.update();
    editor.on("transaction", this.update);
  }

  /** 大纲中的标题不变时为同一个数组。The same array while the outline headings are unchanged. */
  public get headings(): readonly OutlineHeading[] {
    return this.currentHeadings;
  }

  /** 返回第 index 项的标题节点位置，越界时抛出。Returns the heading's position and throws if out of range. */
  public position(index: number): number {
    const position = this.positions[index];
    if (position === undefined) throw new RangeError(`No outline heading at index ${index}.`);
    return position;
  }

  /** 供 useSyncExternalStore 订阅大纲变化。Subscribes to outline changes for useSyncExternalStore. */
  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  public dispose(): void {
    this.editor.off("transaction", this.update);
    this.listeners.clear();
  }

  private readonly update = (): void => {
    const { doc } = this.editor.state;
    if (this.document === doc) return;
    this.document = doc;

    const headings: OutlineHeading[] = [];
    const positions: number[] = [];
    doc.descendants((node, pos) => {
      if (node.type.name !== "heading") return !node.isTextblock && !node.isAtom;
      const level = node.attrs.level;
      if (!isOutlineLevel(level)) return false;
      const label = outlineLabel(
        node.textBetween(0, node.content.size, "", (leaf) =>
          leaf.type.name === "rawInline" ? String(leaf.attrs.raw) : " ",
        ),
      );
      if (label) {
        headings.push({ level, label });
        positions.push(pos);
      }
      return false;
    });

    this.positions = positions;
    if (sameOutline(headings, this.currentHeadings)) return;
    this.currentHeadings = headings;
    for (const listener of this.listeners) listener();
  };
}
