import { createRxDatabase, RxDatabase, RxCollection } from 'rxdb';
import { getRxStorageDexie } from 'rxdb/plugins/storage-dexie';
import type { AiMessage } from '@/types';

// Chat session document type
export interface ChatSessionDocument {
  id: string; // ULID
  threadId: string;
  title: string; // Generated from first message or custom
  messages: AiMessage[];
  createdAt: number; // Unix timestamp
  updatedAt: number; // Unix timestamp
  size: number; // Approximate size in bytes for quota management
}

// Chat session collection type
export type ChatSessionCollection = RxCollection<ChatSessionDocument>;

// Database collections type
export interface ChatHistoryDatabaseCollections {
  sessions: ChatSessionCollection;
}

// Database type
export type ChatHistoryDatabase = RxDatabase<ChatHistoryDatabaseCollections>;

// Schema for chat sessions
const chatSessionSchema = {
  version: 0,
  primaryKey: 'id',
  type: 'object',
  properties: {
    id: {
      type: 'string',
      maxLength: 26, // ULID length
    },
    threadId: {
      type: 'string',
      maxLength: 100,
    },
    title: {
      type: 'string',
      maxLength: 200,
    },
    messages: {
      type: 'array',
      items: {
        type: 'object',
      },
    },
    createdAt: {
      type: 'number',
      minimum: 0,
    },
    updatedAt: {
      type: 'number',
      minimum: 0,
    },
    size: {
      type: 'number',
      minimum: 0,
    },
  },
  required: ['id', 'threadId', 'title', 'messages', 'createdAt', 'updatedAt', 'size'],
  indexes: ['threadId', 'updatedAt', 'createdAt'],
};

let dbInstance: ChatHistoryDatabase | null = null;

/**
 * Get or create the chat history database instance
 */
export async function getChatHistoryDatabase(): Promise<ChatHistoryDatabase> {
  if (dbInstance) {
    return dbInstance;
  }

  // Create database
  const db = await createRxDatabase<ChatHistoryDatabaseCollections>({
    name: 'claudecode_chat_history',
    storage: getRxStorageDexie(),
  });

  // Add collections
  await db.addCollections({
    sessions: {
      schema: chatSessionSchema,
    },
  });

  dbInstance = db;
  return db;
}

/**
 * Calculate approximate size of a session in bytes
 */
export function calculateSessionSize(session: Omit<ChatSessionDocument, 'size'>): number {
  return JSON.stringify(session).length;
}

/**
 * Constants for size limits
 */
export const MAX_SESSIONS = 1000;
export const MAX_SIZE_BYTES = 200 * 1024 * 1024; // 200MB

/**
 * Get total size of all sessions
 */
export async function getTotalSize(db: ChatHistoryDatabase): Promise<number> {
  const sessions = await db.sessions.find().exec();
  return sessions.reduce((total, session) => total + session.size, 0);
}

/**
 * Get session count
 */
export async function getSessionCount(db: ChatHistoryDatabase): Promise<number> {
  return db.sessions.count().exec();
}

/**
 * Clean up old sessions if limits are exceeded
 * Returns number of sessions deleted
 */
export async function cleanupOldSessions(db: ChatHistoryDatabase): Promise<number> {
  let deletedCount = 0;

  // Check session count limit
  const count = await getSessionCount(db);
  if (count > MAX_SESSIONS) {
    const excess = count - MAX_SESSIONS;
    const oldestSessions = await db.sessions
      .find()
      .sort({ updatedAt: 'asc' })
      .limit(excess)
      .exec();

    for (const session of oldestSessions) {
      await session.remove();
      deletedCount++;
    }
  }

  // Check size limit
  let totalSize = await getTotalSize(db);
  while (totalSize > MAX_SIZE_BYTES) {
    const oldestSession = await db.sessions
      .findOne()
      .sort({ updatedAt: 'asc' })
      .exec();

    if (!oldestSession) break;

    totalSize -= oldestSession.size;
    await oldestSession.remove();
    deletedCount++;
  }

  return deletedCount;
}
