import type { ChatImageAttachment } from "@agw/chat-core";
import React from "react";

import { getErrorMessage } from "@/lib/errors";
import { useWorkspace } from "@/features/workspace/workspace-provider";
import { pickChatImages } from "./image-picker";

type ComposerContextValue = {
  text: string;
  attachments: ChatImageAttachment[];
  error: string | null;
  quickTextOpen: boolean;
  setText(value: string): void;
  pickImages(): Promise<void>;
  removeAttachment(id: string): void;
  submit(): Promise<void>;
  openQuickText(): void;
  closeQuickText(): void;
  selectQuickText(value: string): void;
};

const ComposerContext = React.createContext<ComposerContextValue | null>(null);

export function ComposerProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const workspace = useWorkspace();
  const [text, setText] = React.useState("");
  const [attachments, setAttachments] = React.useState<ChatImageAttachment[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [quickTextOpen, setQuickTextOpen] = React.useState(false);

  const value = React.useMemo<ComposerContextValue>(
    () => ({
      text,
      attachments,
      error,
      quickTextOpen,
      setText,
      pickImages: async () => {
        try {
          const selected = await pickChatImages(attachments);
          setAttachments((current) => [...current, ...selected]);
          setError(null);
        } catch (caught) {
          setError(getErrorMessage(caught));
        }
      },
      removeAttachment: (id) =>
        setAttachments((current) => current.filter((attachment) => attachment.id !== id)),
      submit: async () => {
        if ((!text.trim() && attachments.length === 0) || workspace.isExecuting) return;
        const submittedText = text;
        const submittedAttachments = attachments;
        setText("");
        setAttachments([]);
        setError(null);
        await workspace.sendMessage(submittedText, submittedAttachments);
      },
      openQuickText: () => setQuickTextOpen(true),
      closeQuickText: () => setQuickTextOpen(false),
      selectQuickText: (value) => {
        setText(`${value} `);
        setQuickTextOpen(false);
      },
    }),
    [attachments, error, quickTextOpen, text, workspace],
  );
  return <ComposerContext.Provider value={value}>{children}</ComposerContext.Provider>;
}

export function useComposer(): ComposerContextValue {
  const value = React.useContext(ComposerContext);
  if (!value) throw new Error("useComposer must be used inside ComposerProvider.");
  return value;
}
