import * as React from "react";

import type { ChatTargetOption, components } from "@agw/api";
import type { ChatImageAttachment, CommandSource } from "@agw/chat-core";
import type { AgentMode, PermissionMode } from "@agw/execution-core";
import type { ContextSummary, ProjectFilesService } from "@agw/projects-core";

import { pickChatImages } from "./image-picker";

export type NativeAgentSuggestion = components["schemas"]["AgentSuggestionResponse"];
export type NativeChatBindings = {
  projects: Array<{ id: string; name: string }>;
  contexts: ContextSummary[];
  targets: ChatTargetOption[];
  selectedProjectId: string | null;
  selectedContextId: string | null;
  selectedTargetValue: string | null;
  selectedTarget: ChatTargetOption | null;
  selectedProject: { id: string; name: string } | null;
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  commandSource: CommandSource;
  agentSuggestions: NativeAgentSuggestion[];
  supportsAgentMode: boolean;
  isSuggestionsLoading: boolean;
  suggestionsError: string | null;
  isExecuting: boolean;
  filesService: ProjectFilesService | null;
  selectTarget(value: string): void;
  selectProject(projectId: string): void;
  selectContext(contextId: string): void;
  setPermissionMode(mode: PermissionMode): void;
  setAgentMode(mode: AgentMode): void;
  sendMessage(text: string, attachments: readonly ChatImageAttachment[]): Promise<void>;
  stopExecution(): void;
  renameContext(contextId: string, title: string): Promise<void>;
  deleteContext(contextId: string): Promise<void>;
  refreshContexts(): Promise<void>;
};

export type NativeComposerState = {
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

const NativeChatContext = React.createContext<NativeChatBindings | null>(null);
const NativeComposerContext = React.createContext<NativeComposerState | null>(null);

export function NativeChatProvider({
  bindings,
  composer: composerOverride,
  children,
}: React.PropsWithChildren<{
  bindings: NativeChatBindings;
  composer?: NativeComposerState;
}>) {
  const [text, setText] = React.useState("");
  const [attachments, setAttachments] = React.useState<ChatImageAttachment[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [quickTextOpen, setQuickTextOpen] = React.useState(false);

  const internalComposer = React.useMemo<NativeComposerState>(
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
          setError(caught instanceof Error ? caught.message : String(caught));
        }
      },
      removeAttachment: (id) =>
        setAttachments((current) => current.filter((attachment) => attachment.id !== id)),
      submit: async () => {
        if ((!text.trim() && attachments.length === 0) || bindings.isExecuting) return;
        const submittedText = text;
        const submittedAttachments = attachments;
        setText("");
        setAttachments([]);
        setError(null);
        await bindings.sendMessage(submittedText, submittedAttachments);
      },
      openQuickText: () => setQuickTextOpen(true),
      closeQuickText: () => setQuickTextOpen(false),
      selectQuickText: (value) => {
        setText(`${value} `);
        setQuickTextOpen(false);
      },
    }),
    [attachments, bindings, error, quickTextOpen, text],
  );

  return (
    <NativeChatContext.Provider value={bindings}>
      <NativeComposerContext.Provider value={composerOverride ?? internalComposer}>
        {children}
      </NativeComposerContext.Provider>
    </NativeChatContext.Provider>
  );
}

export function useNativeChat(): NativeChatBindings {
  const value = React.useContext(NativeChatContext);
  if (!value) throw new Error("useNativeChat must be used inside NativeChatProvider.");
  return value;
}

export function useNativeComposer(): NativeComposerState {
  const value = React.useContext(NativeComposerContext);
  if (!value) throw new Error("useNativeComposer must be used inside NativeChatProvider.");
  return value;
}
