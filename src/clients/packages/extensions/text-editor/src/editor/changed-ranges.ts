import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import type { Transaction } from "@tiptap/pm/state";
import {
  AddMarkStep,
  AddNodeMarkStep,
  AttrStep,
  RemoveMarkStep,
  RemoveNodeMarkStep,
  type Step,
} from "@tiptap/pm/transform";

// 一组事务改动过的范围，换算到最后的文档中，并扩大到所在的顶层块。插件只重新处理这些块。标记与属性的步骤不移动位置，
// 它们的 step map 为空，范围取自步骤本身。
// The ranges a group of transactions changed, mapped into the final document and widened to their top-level blocks.
// Plugins revisit only these blocks. Mark and attribute steps move no positions and have empty step maps, so their
// ranges come from the steps themselves.

export type Range = { readonly from: number; readonly to: number };

/** 步骤在它执行之后的文档中改动的范围。The ranges a step changed, in the document after the step. */
function stepRanges(step: Step, index: number, tr: Transaction): Range[] {
  if (step instanceof AddMarkStep || step instanceof RemoveMarkStep) {
    return [{ from: step.from, to: step.to }];
  }
  if (
    step instanceof AttrStep ||
    step instanceof AddNodeMarkStep ||
    step instanceof RemoveNodeMarkStep
  ) {
    return [{ from: step.pos, to: step.pos + 1 }];
  }
  const ranges: Range[] = [];
  tr.mapping.maps[index].forEach((_oldStart, _oldEnd, newStart, newEnd) =>
    ranges.push({ from: newStart, to: newEnd }),
  );
  return ranges;
}

/** 事务改动过的顶层块范围（按位置排列，已合并重叠）。The top-level block ranges the transactions changed (sorted,
 *  overlaps merged). */
export function changedBlockRanges(
  transactions: readonly Transaction[],
  doc: ProseMirrorNode,
): Range[] {
  const ranges: Range[] = [];
  transactions.forEach((tr, index) => {
    tr.steps.forEach((step, stepIndex) => {
      const rest = tr.mapping.slice(stepIndex + 1);
      for (const range of stepRanges(step, stepIndex, tr)) {
        let from = rest.map(range.from, -1);
        let to = rest.map(range.to, 1);
        for (const later of transactions.slice(index + 1)) {
          from = later.mapping.map(from, -1);
          to = later.mapping.map(to, 1);
        }
        ranges.push(topLevelRange(doc, from, Math.max(from, to)));
      }
    });
  });
  ranges.sort((left, right) => left.from - right.from);
  const merged: Range[] = [];
  for (const range of ranges) {
    const last = merged.at(-1);
    if (last && range.from <= last.to)
      merged[merged.length - 1] = { from: last.from, to: Math.max(last.to, range.to) };
    else merged.push(range);
  }
  return merged;
}

/** 把范围扩大到所在的顶层块。Widens a range to the top-level blocks around it. */
function topLevelRange(doc: ProseMirrorNode, from: number, to: number): Range {
  const size = doc.content.size;
  const $from = doc.resolve(Math.min(from, size));
  const $to = doc.resolve(Math.min(to, size));
  return {
    from: $from.depth > 0 ? $from.before(1) : $from.pos,
    to: $to.depth > 0 ? $to.after(1) : $to.pos,
  };
}
