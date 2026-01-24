'use client';

import { Ulid } from 'id128';
import type { AiMessage } from '@/types';
import {
  getChatHistoryDatabase,
  calculateSessionSize,
  cleanupOldSessions,
  type ChatSessionDocument,
  upsert,
} from './chat-history-db';

/**
 * Generate a title from the first user message
 */
function generateTitle(messages: AiMessage[]): string {
  const firstUserMessage = messages.find((msg) => msg.role === 'user');
  if (!firstUserMessage || !firstUserMessage.contents?.length) {
    return 'New Chat';
  }

  const firstContent = firstUserMessage.contents[0];
  const text = firstContent.content || 'New Chat';

  // Truncate to 50 characters
  return text.length > 20 ? text.substring(0, 20) + '...' : text;
}

/**
 * Create or update a chat session
 */
export async function saveSession(
  threadId: string,
  messages: AiMessage[],
  title?: string
): Promise<ChatSessionDocument | null> {
  // Don't save sessions without messages
  if (!messages || messages.length === 0) {
    return null;
  }

  try {
    const db = await getChatHistoryDatabase();

    // Find existing session by threadId
    const result = await db.find({
      selector: { threadId },
      limit: 1
    });

    const existing = result.docs[0];

    const now = Date.now();
    const sessionTitle = title || (existing?.title) || generateTitle(messages);

    const docId = existing?._id || Ulid.generate().toCanonical();

    const sessionData: ChatSessionDocument = {
      _id: docId,
      threadId,
      title: sessionTitle,
      messages,
      createdAt: existing?.createdAt || now,
      updatedAt: now,
      size: 0, // Will be calculated below
    };

    console.debug('Saving session:', sessionData);
    sessionData.size = calculateSessionSize(sessionData);

    // Put will insert or update based on _id
    // const response = await db.put(sessionData);
   const response = await upsert(db, docId, (doc: ChatSessionDocument) => {
      // doc.updatedAt = Date.now();
      // doc.messages = messages;

      console.debug('Upsert session data:', doc);
      return sessionData;
    });
    // Cleanup after save
    await cleanupOldSessions(db);

    // Get the updated document
    const savedDoc = await db.get(response.id);
    console.debug('savedDoc:', savedDoc);

    return savedDoc;
  } catch (error) {
    console.error('Failed to save session:', error);
    return null;
  }
}

/**
 * Get a session by threadId
 */
export async function getSessionByThreadId(
  threadId: string
): Promise<ChatSessionDocument | null> {
  try {
    const db = await getChatHistoryDatabase();
    const result = await db.find({
      selector: { threadId },
      limit: 1
    });

    if (result.docs.length === 0) {
      return null;
    }

    return result.docs[0];
  } catch (error) {
    console.error('Failed to get session by threadId:', error);
    return null;
  }
}

/**
 * Get all sessions, sorted by most recently updated
 */
export async function getAllSessions(): Promise<ChatSessionDocument[]> {
  try {
    const db = await getChatHistoryDatabase();
    const result = await db.find({
      selector: {},
      sort: [{ updatedAt: 'desc' }]
    });

    return result.docs;
  } catch (error) {
    console.error('Failed to get all sessions:', error);
    return [];
  }
}

/**
 * Delete a session by ID
 */
export async function deleteSession(sessionId: string): Promise<boolean> {
  try {
    const db = await getChatHistoryDatabase();
    const doc = await db.get(sessionId);
    await db.remove(doc);
    return true;
  } catch (error) {
    if ((error as any).status === 404) {
      console.warn('Session not found:', sessionId);
      return false;
    }
    console.error('Failed to delete session:', error);
    return false;
  }
}

/**
 * Delete a session by threadId
 */
export async function deleteSessionByThreadId(threadId: string): Promise<boolean> {
  try {
    const db = await getChatHistoryDatabase();
    const result = await db.find({
      selector: { threadId },
      limit: 1
    });

    if (result.docs.length === 0) {
      return false;
    }

    await db.remove(result.docs[0]);
    return true;
  } catch (error) {
    console.error('Failed to delete session by threadId:', error);
    return false;
  }
}

/**
 * Update session title
 */
export async function updateSessionTitle(
  sessionId: string,
  newTitle: string
): Promise<boolean> {
  try {
    const db = await getChatHistoryDatabase();
    const doc = await db.get(sessionId);
    doc.title = newTitle;
    doc.updatedAt = Date.now();
    // Recalculate size in case title changed
    doc.size = calculateSessionSize(doc);

    await db.put(doc);
    return true;
  } catch (error) {
    console.error('Failed to update session title:', error);
    return false;
  }
}

/**
 * Clear all sessions
 */
export async function clearAllSessions(): Promise<void> {
  try {
    const db = await getChatHistoryDatabase();
    const result = await db.allDocs({ include_docs: true });

    // Delete all documents
    await Promise.all(
      result.rows.map(async (row) => {
        if (row.doc) {
          await db.remove(row.doc);
        }
      })
    );
  } catch (error) {
    console.error('Failed to clear all sessions:', error);
  }
}

/**
 * Subscribe to session changes using PouchDB changes() feed
 */
export function subscribeToSessions(
  callback: (sessions: ChatSessionDocument[]) => void
): () => void {
  let isSubscribed = true;
  let changes: PouchDB.Core.Changes<ChatSessionDocument> | null = null;

  // Initialize database and set up subscription
  getChatHistoryDatabase()
    .then((db) => {
      if (!isSubscribed) return;

      // Initial load
      getAllSessions().then((sessions) => {
        if (isSubscribed) {
          callback(sessions);
        }
      });

      // Listen for changes
      changes = db.changes({
        since: 'now',
        live: true,
        include_docs: true
      }).on('change', async () => {
        if (!isSubscribed) return;

        // Reload all sessions when any change occurs
        const sessions = await getAllSessions();
        if (isSubscribed) {
          callback(sessions);
        }
      });
    })
    .catch((error) => {
      console.error('Failed to initialize chat history database subscription:', error);
    });

  // Return cleanup function
  return () => {
    isSubscribed = false;
    if (changes) {
      changes.cancel();
      changes = null;
    }
  };
}
