import { openDB, type DBSchema, type IDBPDatabase } from "idb";

import {
  getScopePrefix,
  getStorageKey,
  type ConversationComposerScope,
} from "./conversation-composer-storage";
import {
  createImageAttachments,
  validateImageFiles,
  type ChatImageAttachment,
} from "./image-attachments";

interface ImageDraftDatabase extends DBSchema {
  images: { key: string; value: readonly ChatImageAttachment[] };
}

let database: Promise<IDBPDatabase<ImageDraftDatabase>> | undefined;

function getDatabase() {
  database ??= openDB<ImageDraftDatabase>("agw-chat-image-drafts", 1, {
    upgrade(db) {
      db.createObjectStore("images");
    },
    blocking() {
      void database?.then((db) => db.close());
      database = undefined;
    },
    terminated() {
      database = undefined;
    },
  });
  return database;
}

export interface ImageDraftSnapshot {
  attachments: readonly ChatImageAttachment[];
  isBusy: boolean;
  error: unknown;
}

/**
 * 对话拥有图片读取任务与顺序写入；迁移 ID 时保留同一个实例。
 * The conversation owns image reads and ordered writes; an ID migration keeps the same instance.
 */
export class ConversationImageDraft {
  private attachments: readonly ChatImageAttachment[] = [];
  private loaded: boolean;
  private loading: Promise<void> | undefined;
  private pending = Promise.resolve();
  private pendingWrites = 0;
  private reading = false;
  private generation = 0;
  private discarded = false;
  private error: unknown = null;
  private listeners = new Set<() => void>();
  private snapshot: ImageDraftSnapshot;

  constructor(
    private readonly scope: ConversationComposerScope | null,
    private conversationId: string | null,
  ) {
    this.loaded = scope === null;
    this.snapshot = { attachments: [], isBusy: !this.loaded, error: null };
  }

  getSnapshot = () => this.snapshot;

  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  load(): Promise<void> {
    if (this.loaded) return Promise.resolve();
    const key = getStorageKey(this.scope!, this.conversationId);
    this.loading ??= this.enqueue(async () => {
      this.attachments = (await (await getDatabase()).get("images", key)) ?? [];
      this.loaded = true;
    });
    return this.loading;
  }

  async add(files: readonly File[]): Promise<void> {
    if (this.discarded) return;
    if (this.error) throw this.error;
    if (this.snapshot.isBusy) throw new Error("Please wait for the images to finish loading.");
    const validationError = validateImageFiles(files, this.attachments);
    if (validationError) throw new Error(validationError);
    const generation = this.generation;
    this.reading = true;
    this.publish();
    try {
      const attachments = await createImageAttachments(files);
      if (generation !== this.generation) return;
      await this.update((current) => [...current, ...attachments]);
    } finally {
      if (generation === this.generation) {
        this.reading = false;
        this.publish();
      }
    }
  }

  removeImages(ids: readonly string[]): Promise<void> {
    if (this.discarded) return Promise.resolve();
    const removed = new Set(ids);
    return this.update((current) => current.filter((image) => !removed.has(image.id)));
  }

  clear(): Promise<void> {
    this.generation += 1;
    this.reading = false;
    return this.update(() => []);
  }

  discard(): Promise<void> {
    const cleared = this.clear();
    this.discarded = true;
    return cleared;
  }

  moveTo(conversationId: string): Promise<void> {
    const sourceKey = getStorageKey(this.scope!, this.conversationId);
    const destinationKey = getStorageKey(this.scope!, conversationId);
    this.conversationId = conversationId;
    return this.enqueue(async () => {
      const db = await getDatabase();
      const tx = db.transaction("images", "readwrite");
      const images = (await tx.store.get(sourceKey)) ?? [];
      await Promise.all([
        images.length > 0 ? tx.store.put(images, destinationKey) : tx.store.delete(destinationKey),
        tx.store.delete(sourceKey),
        tx.done,
      ]);
      this.attachments = images;
      this.loaded = true;
    });
  }

  private update(
    change: (current: readonly ChatImageAttachment[]) => readonly ChatImageAttachment[],
  ): Promise<void> {
    const key = this.scope ? getStorageKey(this.scope, this.conversationId) : null;
    return this.enqueue(async () => {
      const images = change(this.attachments);
      if (key) {
        const db = await getDatabase();
        if (images.length > 0) await db.put("images", images, key);
        else await db.delete("images", key);
      }
      this.attachments = images;
      this.loaded = true;
    });
  }

  private enqueue(operation: () => Promise<void>): Promise<void> {
    this.pendingWrites += 1;
    this.publish();
    const result = this.pending.then(operation);
    this.pending = result.then(
      () => this.finish(null),
      (error: unknown) => this.finish(error),
    );
    return result;
  }

  private finish(error: unknown) {
    this.pendingWrites -= 1;
    this.error = error;
    this.publish();
  }

  private publish() {
    this.snapshot = {
      attachments: this.attachments,
      isBusy: !this.loaded || this.reading || this.pendingWrites > 0,
      error: this.error,
    };
    for (const listener of this.listeners) listener();
  }
}

const drafts = new Map<string, ConversationImageDraft>();

export const conversationImageDrafts = {
  get(scope: ConversationComposerScope, conversationId: string | null): ConversationImageDraft {
    const key = getStorageKey(scope, conversationId);
    let draft = drafts.get(key);
    if (!draft) {
      draft = new ConversationImageDraft(scope, conversationId);
      drafts.set(key, draft);
    }
    return draft;
  },
  accept(scope: ConversationComposerScope, conversationId: string): Promise<void> {
    const draft = conversationImageDrafts.get(scope, null);
    drafts.delete(getStorageKey(scope, null));
    drafts.set(getStorageKey(scope, conversationId), draft);
    return draft.moveTo(conversationId);
  },
  remove(scope: ConversationComposerScope, conversationId: string): Promise<void> {
    const draft = conversationImageDrafts.get(scope, conversationId);
    drafts.delete(getStorageKey(scope, conversationId));
    return draft.discard();
  },
  async removeConversations(scope: ConversationComposerScope): Promise<void> {
    const prefix = `${getScopePrefix(scope)}conversation:`;
    const removals: Promise<void>[] = [];
    for (const [key, draft] of drafts) {
      if (key.startsWith(prefix)) {
        drafts.delete(key);
        removals.push(draft.discard());
      }
    }
    await Promise.all(removals);
    const db = await getDatabase();
    const tx = db.transaction("images", "readwrite");
    const keys = await tx.store.getAllKeys();
    await Promise.all([
      ...keys.filter((key) => key.startsWith(prefix)).map((key) => tx.store.delete(key)),
      tx.done,
    ]);
  },
};
