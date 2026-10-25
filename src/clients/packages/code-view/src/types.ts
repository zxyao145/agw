import type * as React from "react";

import type { LineChange } from "./line-remap";
import type { CodeTextModel } from "./text-model";

/** 某一行下方的区域。A zone below a line. */
export interface LineZone {
  /** 稳定的 key，区域内容在它不变时保留状态。A stable key; the zone content keeps its state while it stays. */
  key: string;
  afterLineNumber: number;
}

/**
 * 作用于一行的操作：行号旁的按钮（鼠标所在行）与右键菜单（光标所在行）。
 * An action on a line: the button beside the line number (the line under the mouse) and the context menu (the cursor
 * line).
 */
export interface LineZoneAction {
  label: string;
  canRun(lineNumber: number): boolean;
  run(lineNumber: number): void;
}

/** 一个编辑器的行区域、行操作与整行标记。One editor's line zones, line action and whole-line marks. */
export interface CodeViewSideProps {
  lineZones: readonly LineZone[];
  renderLineZone(zone: LineZone): React.ReactNode;
  /** 整行标出的行号。Line numbers marked across the whole line. */
  markedLines: readonly number[];
  lineZoneAction: LineZoneAction | null;
}

/**
 * 可编辑的内容（设计文档 5.4 节）：宿主的文本模型与状态栏中的编码菜单，以及编辑器报告的格式化、错误与行号变化。
 * Editable content (design section 5.4): the host's text model and the encoding menu of the status bar, plus the
 * formatting, errors and line changes the editor reports.
 */
export interface EditableCodeContent {
  kind: "editable";
  model: CodeTextModel;
  /** 文档已读入且没有失败；为 false 时编辑器与状态栏隐藏。The document is loaded and not failed; false hides the editor and the status bar. */
  shown: boolean;
  /** 状态栏中缩进与换行之间的编码菜单。The encoding menu between indentation and line breaks in the status bar. */
  encoding: React.ReactNode;
  /**
   * 能格式化时为执行格式化的函数，否则为 null；格式化程序加载或语言变化后调用。
   * A function that formats when formatting is available, otherwise null; called once formatters load or the language
   * changes.
   */
  onFormatChange(format: (() => void) | null): void;
  /** 格式化失败等需要告诉用户的错误。Errors to tell the user, such as a failed format. */
  onError(message: string): void;
  /** 读入以外的每次内容变化中的改动。The changes of every content change other than a load. */
  onDidChangeLines(changes: readonly LineChange[]): void;
}

/** 只读的内容。Read-only content. */
export interface TextCodeContent {
  kind: "text";
  text: string;
}

export interface CodeViewerProps extends CodeViewSideProps {
  /** 项目内路径；只读时决定语言。The project-relative path; it decides the language when read-only. */
  filePath: string;
  ariaLabel: string;
  wordWrap: boolean;
  content: TextCodeContent | EditableCodeContent;
}

export interface CodeDiffSide extends CodeViewSideProps {
  text: string;
  label: string;
  /** 这一侧有这个文件。The file exists on this side. */
  present: boolean;
}

export interface CodeDiffViewerProps {
  filePath: string;
  original: CodeDiffSide;
  modified: CodeDiffSide;
}
