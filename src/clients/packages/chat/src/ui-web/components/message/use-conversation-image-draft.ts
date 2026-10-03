import * as React from "react";
import { toast } from "sonner";

import type { ConversationComposerScope } from "../../../lib/chat/conversation-composer-storage";
import {
  ConversationImageDraft,
  conversationImageDrafts,
} from "../../../lib/chat/conversation-image-drafts";
import type { ChatImageAttachment } from "../../../lib/chat/image-attachments";

export function notifyImageDraftError(error: unknown) {
  console.error("Unable to update the conversation's image draft.", error);
  if (error instanceof DOMException && error.name === "QuotaExceededError") {
    toast.error("There is not enough local storage to save these images.");
    return;
  }
  toast.error(error instanceof Error ? error.message : "Unable to update the image draft.");
}

export function useConversationImageDraft(
  scope: ConversationComposerScope | null,
  conversationId: string | null,
  sessionRevision: string | number,
) {
  const transientRevision = scope ? null : sessionRevision;
  const draft = React.useMemo(
    () =>
      scope
        ? conversationImageDrafts.get(scope, conversationId)
        : new ConversationImageDraft(null, conversationId),
    [scope, conversationId, transientRevision],
  );
  const snapshot = React.useSyncExternalStore(
    draft.subscribe,
    draft.getSnapshot,
    draft.getSnapshot,
  );

  React.useEffect(() => {
    void draft.load().catch(notifyImageDraftError);
  }, [draft]);

  const submit = React.useCallback(
    (value: string, execute: (text: string, images: readonly ChatImageAttachment[]) => boolean) => {
      const images = draft.getSnapshot().attachments;
      const accepted = execute(value, images);
      if (accepted) {
        void draft.removeImages(images.map((image) => image.id)).catch(notifyImageDraftError);
      }
      return accepted;
    },
    [draft],
  );

  return {
    draft,
    submit,
    attachments: snapshot.attachments,
    isBusy: snapshot.isBusy || Boolean(snapshot.error),
  };
}
