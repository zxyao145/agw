import * as React from "react";
import { createRoot } from "react-dom/client";
import { flushSync } from "react-dom";
import { Toaster } from "sonner";
import { EMPTY_EXECUTION_QUEUE } from "@agw/chat-runtime";

import { ChatInput } from "../src/ui-web/components/message/chat-input";
import {
  useConversationImageDraft,
  notifyImageDraftError,
} from "../src/ui-web/components/message/use-conversation-image-draft";
import {
  ConversationImageDraft,
  conversationImageDrafts,
} from "../src/lib/chat/conversation-image-drafts";
import type { ChatImageAttachment } from "../src/lib/chat/image-attachments";

type Selection = {
  serverId: string;
  projectId: string;
  conversationId: string | null;
  persist: boolean;
};
const initialSelection: Selection = {
  serverId: "server-a",
  projectId: "project-a",
  conversationId: "conversation-a",
  persist: true,
};

const fixture = {
  ConversationImageDraft,
  drafts: conversationImageDrafts,
  createImageFile,
  select: (_selection: Partial<Selection>) => {},
  current: null as ConversationImageDraft | null,
  acceptSubmission: true,
  submissions: [] as { text: string; images: readonly ChatImageAttachment[] }[],
};

function createImageFile(name: string, size?: number) {
  const bytes = Uint8Array.from(
    atob(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==",
    ),
    (value) => value.charCodeAt(0),
  );
  const padding = new Uint8Array(Math.max(0, (size ?? bytes.length) - bytes.length));
  for (let offset = 0; offset < padding.length; offset += 65536) {
    crypto.getRandomValues(padding.subarray(offset, offset + 65536));
  }
  return new File([bytes, padding], name, { type: "image/png" });
}

const inputProps = {
  isExecuting: false,
  isTransitioning: false,
  isLoadingHistory: false,
  hasMessages: false,
  onInterrupt: () => {},
  queue: EMPTY_EXECUTION_QUEUE,
  onQueueEditStart: () => false,
  onQueueEditCancel: () => {},
  onQueueEditSave: () => false,
  onQueueRemove: () => {},
  onQueueResume: () => {},
  onScrollToBottom: () => {},
  onScrollToTop: () => {},
  showResume: false,
  canResume: false,
  onResume: () => {},
  projectId: null,
  directoryIds: [],
  commandSource: { mode: "unsupported" },
  permissionMode: "fullAccess",
  agentMode: "execute",
  onPermissionModeChange: () => {},
  onAgentModeChange: () => {},
  pendingFileCommentCount: 0,
  onClearPendingFileComments: () => {},
} satisfies Partial<React.ComponentProps<typeof ChatInput>>;

function ImageDraftFixture() {
  const [selection, setSelection] = React.useState(initialSelection);
  const scope = React.useMemo(
    () => ({ serverId: selection.serverId, projectId: selection.projectId }),
    [selection.serverId, selection.projectId],
  );
  const images = useConversationImageDraft(
    selection.persist ? scope : null,
    selection.conversationId,
    0,
  );
  fixture.current = images.draft;
  fixture.select = (next) => flushSync(() => setSelection((current) => ({ ...current, ...next })));

  return (
    <>
      <output aria-label="Image draft state">{images.isBusy ? "busy" : "ready"}</output>
      <ChatInput
        {...inputProps}
        key={JSON.stringify(selection)}
        imageAttachments={images.attachments}
        isImageDraftBusy={images.isBusy}
        onAddImages={(files) => void images.draft.add(files).catch(notifyImageDraftError)}
        onRemoveImage={(id) => void images.draft.removeImages([id]).catch(notifyImageDraftError)}
        onExecute={(value) =>
          images.submit(value, (text, attachments) => {
            if (!fixture.acceptSubmission) return false;
            fixture.submissions.push({ text, images: attachments });
            return true;
          })
        }
        onClearSession={() => void images.draft.clear().catch(notifyImageDraftError)}
      />
      <Toaster />
    </>
  );
}

Object.assign(window, { imageDraftFixture: fixture });
createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ImageDraftFixture />
  </React.StrictMode>,
);

declare global {
  interface Window {
    imageDraftFixture: typeof fixture;
  }
}
