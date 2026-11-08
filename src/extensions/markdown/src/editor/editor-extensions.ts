import type { AnyExtension } from "@tiptap/core";

import { editMarkdownExtensions } from "../markdown/markdown-extensions";
import {
  DefinitionBlock,
  EditorDocument,
  FootnoteDefinitionBlock,
  FootnoteReference,
  FrontMatter,
  RawBlock,
  RawInline,
} from "./editor-nodes";
import { MarkdownDefinitionsExtension } from "./markdown-definitions";

/**
 * Mixed 编辑器的扩展：schema、Markdown 解析与输出、定义插件。界面任务在此之后加入只用于编辑的扩展。
 * The Mixed editor's extensions: schema, Markdown parsing and output, and the definitions plugin. The UI task appends
 * its editing-only extensions after these.
 */
export const editorExtensions: AnyExtension[] = [
  EditorDocument,
  FrontMatter,
  ...editMarkdownExtensions,
  RawBlock,
  DefinitionBlock,
  FootnoteDefinitionBlock,
  RawInline,
  FootnoteReference,
  MarkdownDefinitionsExtension,
];
