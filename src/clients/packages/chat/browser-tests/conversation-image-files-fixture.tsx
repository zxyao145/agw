import * as React from "react";
import { createRoot } from "react-dom/client";
import {
  buildConversationRenderModel,
  type ConversationRenderItem,
  type PresentedMessage,
} from "@agw/chat-core";
import { configureApiRuntime, type AiMessage } from "@agw/api";
import type { ProjectDirectoryOption } from "@agw/projects";

import { Conversation } from "../src/ui-web/components/message/conversation";
import { ConversationImageFileProvider } from "../src/ui-web/components/message/conversation-image-file";
import { MarkdownRendererProvider } from "../src/ui-web/components/message/renders/markdown-renderer";
import MdCard from "../src/ui-web/components/message/renders/md-card";
import { TiptapMarkdown } from "../src/ui-web/markdown-tiptap";

const DATA_IMAGE =
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";

type FixtureOptions = {
  apiBaseUrl: string;
  apiToken: string;
  projectId: string;
  serverId: string;
  directories: ProjectDirectoryOption[];
  markdown: string;
  renderer: "md-card" | "tiptap";
  includeDataImage?: boolean;
  conversationId?: string;
  messages?: AiMessage[];
};

function createMessageItem(markdown: string, includeDataImage: boolean): ConversationRenderItem[] {
  if (!markdown) return [];

  const contents: PresentedMessage["contents"] = [
    { type: "markdown", markdown, sourceType: "TextContent" },
    ...(includeDataImage ? [{ type: "image", uri: DATA_IMAGE, name: "Data image" } as const] : []),
  ];
  const message: PresentedMessage = {
    source: { messageId: "markdown-images", role: "assistant", contents: [] },
    identity: "markdown-images",
    alignment: "left",
    width: "normal",
    meta: null,
    contents,
  };

  return [
    {
      key: "markdown-images",
      type: "message",
      alignment: "left",
      width: "normal",
      message,
    } satisfies ConversationRenderItem,
  ];
}

function ConversationFixture({ options }: { options: FixtureOptions }) {
  const scrollElementRef = React.useRef<HTMLDivElement>(null);
  const [ready, setReady] = React.useState(false);
  const MarkdownRenderer = options.renderer === "tiptap" ? TiptapMarkdown : MdCard;

  React.useLayoutEffect(() => setReady(true), []);

  return (
    <div ref={scrollElementRef} style={{ height: 900, width: 1100, overflow: "auto" }}>
      {ready ? (
        <ConversationImageFileProvider
          projectId={options.projectId}
          serverId={options.serverId}
          directories={options.directories}
          conversationId={options.conversationId}
          messages={options.messages}
        >
          <MarkdownRendererProvider renderer={MarkdownRenderer}>
            <Conversation
              items={
                options.messages
                  ? buildConversationRenderModel(options.messages)
                  : createMessageItem(options.markdown, options.includeDataImage ?? false)
              }
              conversationKey="markdown-images"
              scrollElementRef={scrollElementRef}
            />
          </MarkdownRendererProvider>
        </ConversationImageFileProvider>
      ) : null}
    </div>
  );
}

const root = createRoot(document.getElementById("root")!);
let currentOptions: FixtureOptions | null = null;

function render(options: FixtureOptions) {
  currentOptions = options;
  configureApiRuntime({ baseUrl: options.apiBaseUrl, token: options.apiToken, explicitAuth: true });
  root.render(<ConversationFixture options={options} />);
}

const fixture = {
  mount: render,
  updateMarkdown(markdown: string) {
    if (!currentOptions) throw new Error("The Markdown image fixture is not mounted.");
    render({ ...currentOptions, markdown });
  },
};

Object.assign(window, { conversationImageFilesFixture: fixture });

declare global {
  interface Window {
    conversationImageFilesFixture: typeof fixture;
  }
}
