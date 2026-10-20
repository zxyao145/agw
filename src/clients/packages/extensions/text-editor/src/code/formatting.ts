// Prettier 格式化（6.5 节）。这个模块不导入 Monaco，Node 测试可以直接调用；Code 页面启动后动态加载它，Prettier 不在
// 打开文件的路径上。
// Prettier formatting (section 6.5). This module does not import Monaco, so Node tests call it directly; the Code page
// loads it dynamically after start, keeping Prettier off the file-opening path.
import type { SupportInfo, SupportInfoOptions, SupportLanguage } from "prettier";
import * as babel from "prettier/plugins/babel";
import * as estree from "prettier/plugins/estree";
import * as graphql from "prettier/plugins/graphql";
import * as html from "prettier/plugins/html";
import * as markdown from "prettier/plugins/markdown";
import * as postcss from "prettier/plugins/postcss";
import * as typescript from "prettier/plugins/typescript";
import * as yaml from "prettier/plugins/yaml";
import * as prettier from "prettier/standalone";

const plugins = [babel, estree, typescript, postcss, html, markdown, yaml, graphql];

// prettier/standalone 的类型声明没有写 getSupportInfo 的参数，运行时与 prettier 主入口一样接受 `{ plugins }`。
// prettier/standalone's declaration omits getSupportInfo's parameter; at runtime it takes `{ plugins }` like the main entry.
const getSupportInfo = prettier.getSupportInfo as (
  options: SupportInfoOptions,
) => Promise<SupportInfo>;

/** Prettier 与上面的插件支持的语言。The languages Prettier supports with the plugins above. */
export async function loadPrettierLanguages(): Promise<readonly SupportLanguage[]> {
  return (await getSupportInfo({ plugins })).languages;
}

/**
 * Prettier 能否按文件名推断出解析器：文件名与某个语言的 `filenames` 相同（不区分大小写），或以它的某个扩展名结尾。
 * 规则与 Prettier 推断解析器的规则相同。
 * Whether Prettier infers a parser from the file name: the name equals one of a language's `filenames` (ignoring
 * case) or ends with one of its extensions, the same rule Prettier uses to infer the parser.
 */
export function prettierSupports(fileName: string, languages: readonly SupportLanguage[]): boolean {
  const name = (fileName.split(/[\\/]/u).pop() ?? fileName).toLowerCase();
  return languages.some(
    (language) =>
      language.filenames?.some((candidate) => candidate.toLowerCase() === name) ||
      language.extensions?.some((extension) => name.endsWith(extension)),
  );
}

/**
 * 按文件名推断的解析器格式化全文；语法错误等失败时被拒绝。
 * Formats the whole text with the parser inferred from the file name; rejects on failures such as syntax errors.
 */
export function formatWithPrettier(
  text: string,
  options: { fileName: string; tabWidth: number; useTabs: boolean },
): Promise<string> {
  return prettier.format(text, {
    filepath: options.fileName,
    plugins,
    tabWidth: options.tabWidth,
    useTabs: options.useTabs,
  });
}
