import type { Editor } from "@tiptap/core";
import HighlightJs from "highlight.js/lib/core";
import { common } from "lowlight";

// 代码块的语言选择（设计 5.7）：lowlight 已注册的语言与 "Plain text"；选择后替换 info 的第一个词，其余部分不变。
// The code block language choice (design section 5.7): the languages lowlight has registered plus "Plain text";
// choosing one replaces the first word of the info and keeps the rest.

export type CodeLanguage = { readonly name: string; readonly label: string };

/** 纯文本的语言名称与别名，它们显示为 "Plain text"。The plain text names and aliases, shown as "Plain text". */
const PLAIN_TEXT_NAMES = new Set(["plaintext", "text", "txt"]);
// 有其他附加信息时，纯文本写为 `text`，附加信息不会成为语言。With other info, plain text is written as `text`, so
// the info does not become the language.
const PLAIN_TEXT_WORD = "text";

export type CodeLanguageEntry = CodeLanguage & { readonly aliases: readonly string[] };

/**
 * lowlight 实例中已注册的语言（不含纯文本），按显示名称排序。每个代码块扩展计算一次。
 * The registered languages of a lowlight instance (plain text excluded), sorted by label. Each code block extension
 * computes them once.
 */
export function codeLanguages(registered: readonly string[]): readonly CodeLanguageEntry[] {
  return registered
    .filter((name) => !PLAIN_TEXT_NAMES.has(name))
    .map((name) => {
      const grammar = common[name];
      // 语法定义给出显示名称与别名。The grammar definition gives the display name and aliases.
      const definition = grammar ? grammar(HighlightJs) : null;
      return { name, label: definition?.name ?? name, aliases: definition?.aliases ?? [] };
    })
    .sort((left, right) => left.label.localeCompare(right.label));
}

export type LanguageChoice = {
  /** 选择框的值：注册的语言名称，纯文本为 ""，没有注册的语言为原来的词。The select value: the registered name, "" for
   *  plain text, or the word itself when it is not registered. */
  readonly value: string;
  readonly languages: readonly CodeLanguage[];
  /** 没有注册的语言（例如 `mermaid`），作为额外的一项显示。An unregistered language (such as `mermaid`), shown as an
   *  extra entry. */
  readonly unknown: string | null;
};

/** 代码块的 `language` 对应的选择。The choice for a code block's `language`. */
export function languageChoice(
  language: string | null,
  languages: readonly CodeLanguageEntry[],
): LanguageChoice {
  if (!language || PLAIN_TEXT_NAMES.has(language.toLowerCase())) {
    return { value: "", languages, unknown: null };
  }
  const word = language.toLowerCase();
  const found = languages.find((entry) => entry.name === word || entry.aliases.includes(word));
  return found
    ? { value: found.name, languages, unknown: null }
    : { value: language, languages, unknown: language };
}

/**
 * 为 `position` 处的代码块选择语言：`name` 为注册的语言名称，"" 为纯文本。info 的第一个词换成新的语言，其余部分不变。
 * Chooses the language of the code block at `position`: `name` is a registered language name, "" is plain text. The
 * first word of the info becomes the new language and the rest stays.
 */
export function setCodeBlockLanguage(editor: Editor, position: number, name: string): void {
  const node = editor.state.doc.nodeAt(position);
  if (node?.type.name !== "codeBlock") throw new Error(`No code block at ${position}.`);
  const info = String(node.attrs.info ?? "");
  const first = /^\S*/.exec(info)![0];
  const rest = info.slice(first.length);
  const word = name || (rest.trim() ? PLAIN_TEXT_WORD : "");
  const nextInfo = word ? `${word}${rest}` : "";
  editor.view.dispatch(
    editor.state.tr.setNodeMarkup(position, undefined, {
      ...node.attrs,
      info: nextInfo,
      language: word || null,
    }),
  );
}
