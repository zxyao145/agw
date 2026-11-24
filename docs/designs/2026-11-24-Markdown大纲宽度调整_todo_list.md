# Markdown 大纲宽度调整：实施清单

设计：[2026-11-24-Markdown大纲宽度调整.md](2026-11-24-Markdown大纲宽度调整.md)

1. [x] `shared/outline-width.ts` 与 `outline-width.test.ts`。
2. [x] `shared/outline-panel.tsx`：`OutlineResizer`、`.outline-scroll`、`keepActiveHeadingVisible` 改为滚动 `.outline-scroll`。
3. [x] `shared/outline.css`：用户宽度的网格规则、960px 以下的覆盖、`.outline-scroll`、`.outline-resizer`。
4. [x] `editor/main.tsx`、`preview/main.tsx` 调用 `applyOutlineWidth()`。
5. [x] markdown extension 的 typecheck、lint、format、test、build。
6. [x] Desktop 集成测试 `markdownOutlineResize` 步骤；缩短版运行全部通过。
