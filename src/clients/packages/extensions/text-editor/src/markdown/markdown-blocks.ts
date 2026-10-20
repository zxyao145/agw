// Edit 的块级扩展在 parseMarkdown 中记录自己返回的节点对象，位置表据此把文档的顶层节点与 marked 的顶层 token 对应起来。
// MarkdownManager 为空行生成的空 paragraph 没有记录。
// Edit's block-level extensions record the node objects their parseMarkdown returns, so the source map can pair the
// document's top-level nodes with marked's top-level tokens. The empty paragraphs MarkdownManager creates for blank
// lines are not recorded.
const recordedBlocks = new WeakSet<object>();

export function recordBlock<T extends object>(node: T): T {
  recordedBlocks.add(node);
  return node;
}

export function isRecordedBlock(node: object): boolean {
  return recordedBlocks.has(node);
}
