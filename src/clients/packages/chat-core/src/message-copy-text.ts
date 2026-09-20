import type { PresentedContent } from "./conversation-render-model";

/** Text represented by the message body, without renderer controls or attachments. */
export function getMessageCopyText(contents: readonly PresentedContent[]): string {
  return contents
    .flatMap((content) => {
      switch (content.type) {
        case "markdown":
        case "reasoning":
          return [content.markdown];
        case "plain":
        case "error":
        case "json":
          return [content.text];
        case "plan":
          return [content.leadingMarkdown, content.markdown, content.trailingMarkdown];
        case "image":
        case "uri":
          return [];
      }
    })
    .filter((text) => text.trim().length > 0)
    .join("\n\n");
}
