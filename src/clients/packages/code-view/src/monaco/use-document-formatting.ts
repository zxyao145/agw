import type { SupportLanguage } from "prettier";
import * as React from "react";

import { monaco } from "./setup";

type Prettier = {
  formatting: typeof import("./formatting");
  languages: readonly SupportLanguage[];
};

// JSON 使用 JSON 语言服务的格式化。JSON uses the JSON language service's formatting.
const JSON_LANGUAGE = "json";

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** 编辑器当前模型的语言；没有模型时为 null。The language of the editor's current model; null without a model. */
function useLanguageId(editor: monaco.editor.IStandaloneCodeEditor | null): string | null {
  const [languageId, setLanguageId] = React.useState<string | null>(null);
  React.useEffect(() => {
    if (editor === null) return;
    const update = () => setLanguageId(editor.getModel()?.getLanguageId() ?? null);
    update();
    const subscriptions = [
      editor.onDidChangeModel(update),
      editor.onDidChangeModelLanguage(update),
    ];
    return () => {
      for (const subscription of subscriptions) subscription.dispose();
    };
  }, [editor]);
  return languageId;
}

/**
 * 可编辑编辑器的 Format（设计文档 5.4 节）：启动后加载 Prettier 与它支持的语言；文件名受 Prettier 支持时为模型的语言
 * 注册格式化（按模型的缩进设置），失败时报告错误、内容不变；JSON 使用 JSON 语言服务。能格式化时把执行格式化的函数交给
 * `onFormatChange`，否则交出 null。
 * Format for the editable editor (design section 5.4): Prettier and its languages load
 * after start; when Prettier supports the file name, formatting is registered for the model's language (with the
 * model's indentation) and failures report an error while leaving the content unchanged; JSON uses the JSON language
 * service. When formatting is available, `onFormatChange` gets a function that formats, otherwise null.
 */
export function useDocumentFormatting(
  editor: monaco.editor.IStandaloneCodeEditor | null,
  options: {
    enabled: boolean;
    fileName: string | null;
    onFormatChange(format: (() => void) | null): void;
    onError(message: string): void;
  },
): void {
  const { enabled, fileName } = options;
  const [prettier, setPrettier] = React.useState<Prettier | null>(null);
  const languageId = useLanguageId(editor);
  const reportError = React.useEffectEvent((message: string) => options.onError(message));
  const reportFormat = React.useEffectEvent((format: (() => void) | null) =>
    options.onFormatChange(format),
  );

  React.useEffect(() => {
    if (!enabled) return;
    let active = true;
    import("./formatting")
      .then(async (formatting) => {
        const languages = await formatting.loadPrettierLanguages();
        if (active) setPrettier({ formatting, languages });
      })
      .catch((error: unknown) => reportError(errorMessage(error)));
    return () => {
      active = false;
    };
  }, [enabled]);

  const prettierFormats =
    prettier !== null &&
    fileName !== null &&
    languageId !== JSON_LANGUAGE &&
    prettier.formatting.prettierSupports(fileName, prettier.languages);

  React.useEffect(() => {
    if (prettier === null || !prettierFormats || languageId === null || fileName === null) return;
    const { formatting } = prettier;
    const registration = monaco.languages.registerDocumentFormattingEditProvider(languageId, {
      displayName: "Prettier",
      provideDocumentFormattingEdits: async (target) => {
        const modelOptions = target.getOptions();
        try {
          const text = await formatting.formatWithPrettier(target.getValue(), {
            fileName,
            tabWidth: modelOptions.indentSize,
            useTabs: !modelOptions.insertSpaces,
          });
          return [{ range: target.getFullModelRange(), text }];
        } catch (error) {
          reportError(`Could not format ${fileName}: ${errorMessage(error)}`);
          return [];
        }
      },
    });
    return () => registration.dispose();
  }, [prettier, prettierFormats, languageId, fileName]);

  const canFormat = enabled && editor !== null && (languageId === JSON_LANGUAGE || prettierFormats);
  React.useEffect(() => {
    if (!canFormat || editor === null) {
      reportFormat(null);
      return;
    }
    reportFormat(() => {
      const action = editor.getAction("editor.action.formatDocument");
      if (!action) throw new Error("The Format Document action is not available.");
      void action.run();
    });
    return () => reportFormat(null);
  }, [canFormat, editor]);
}
