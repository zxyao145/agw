# Markdown 搜索与替换：实施清单

设计：[2026-11-23-Markdown搜索替换.md](2026-11-23-Markdown搜索替换.md)

1. [x] 依赖：catalog 与 `extensions/markdown/package.json` 增加 `prosemirror-search`，`pnpm install` 后确认 prosemirror 各包只有一份。
2. [x] `shared/find-query.ts` 与 `find-query.test.ts`。
3. [x] `shared/find-reveal.ts`、`shared/find-bar.tsx`、`shared/find-bar.css`、`shared/tooltip.css`；`shared/outline.css` 的 `grid-row: 1`；`editor/edit.css` 移出 tooltip 规则。
4. [x] `editor/mermaid-editing.ts` 的 `inDiagramOnlyBlock`、`switchesMermaidView`；`editor/edit-search.ts`；`edit-extensions.ts`、`edit-document.ts` 接入；`edit-search.test.ts`。
5. [x] `editor/edit-view.tsx`、`editor/main.tsx`、`edit.css` 匹配样式；`edit-view.test.ts` 搜索栏用例。
6. [x] `preview/preview-search.ts` 与 `preview-search.test.ts`；`markdown-document.tsx`、`preview/main.tsx`、`markdown.css` 接入。
7. [x] markdown extension 的 typecheck、lint、format、test、build；`pnpm test:boundaries`。
8. [ ] Desktop 集成测试：fixture `notes/search.md` 与 `markdownSearch` 步骤已加入，desktop typecheck 与 lint 通过；缩短版运行尚未进行。
