import { Ulid } from 'id128';
import type { AiMessage } from '@/types';
import {
  getChatHistoryDatabase,
  calculateSessionSize,
  cleanupOldSessions,
  type ChatSessionDocument,
  type ChatHistoryDatabase,
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
    const existing = await db.sessions
      .findOne({
        selector: { threadId },
      })
      .exec();

    const now = Date.now();
    const sessionTitle = title || (existing?.title) || generateTitle(messages);

    const sessionData = {
      threadId,
      title: sessionTitle,
      messages,
      updatedAt: now,
    };

    const size = calculateSessionSize({
      ...sessionData,
      id: existing?.id || '',
      createdAt: existing?.createdAt || now,
    });

    if (existing) {
      // Update existing session
      await existing.patch({
        ...sessionData,
        size,
      });

      // Cleanup after update
      await cleanupOldSessions(db);

      return existing.toJSON() as ChatSessionDocument;
    } else {
      // Create new session
      const newSession = await db.sessions.insert({
        id: Ulid.generate().toCanonical(),
        ...sessionData,
        createdAt: now,
        size,
      });

      // Cleanup after insert
      await cleanupOldSessions(db);

      return newSession.toJSON() as ChatSessionDocument;
    }
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
  const db = await getChatHistoryDatabase();
  const session = await db.sessions
    .findOne({
      selector: { threadId },
    })
    .exec();

  return session ? (session.toJSON() as ChatSessionDocument) : null;
}

/**
 * Get all sessions, sorted by most recently updated
 */
export async function getAllSessions(): Promise<ChatSessionDocument[]> {
  try {
    const db = await getChatHistoryDatabase();
    const sessions = await db.sessions
      .find()
      .sort({ updatedAt: 'desc' })
      .exec();

    return sessions.map((s) => s.toJSON() as ChatSessionDocument);
  } catch (error) {
    console.error('Failed to get all sessions:', error);
    return [];
  }
}

/**
 * Delete a session by ID
 */
export async function deleteSession(sessionId: string): Promise<boolean> {
  const db = await getChatHistoryDatabase();
  const session = await db.sessions
    .findOne({
      selector: { id: sessionId },
    })
    .exec();

  if (session) {
    await session.remove();
    return true;
  }

  return false;
}

/**
 * Delete a session by threadId
 */
export async function deleteSessionByThreadId(threadId: string): Promise<boolean> {
  const db = await getChatHistoryDatabase();
  const session = await db.sessions
    .findOne({
      selector: { threadId },
    })
    .exec();

  if (session) {
    await session.remove();
    return true;
  }

  return false;
}

/**
 * Update session title
 */
export async function updateSessionTitle(
  sessionId: string,
  newTitle: string
): Promise<boolean> {
  const db = await getChatHistoryDatabase();
  const session = await db.sessions
    .findOne({
      selector: { id: sessionId },
    })
    .exec();

  if (session) {
    await session.patch({
      title: newTitle,
      updatedAt: Date.now(),
    });
    return true;
  }

  return false;
}

/**
 * Clear all sessions
 */
export async function clearAllSessions(): Promise<void> {
  const db = await getChatHistoryDatabase();
  await db.sessions.find().remove();
}

/**
 * Subscribe to session changes
 */
export function subscribeToSessions(
  callback: (sessions: ChatSessionDocument[]) => void
): () => void {
  let subscription: any = null;
  let isSubscribed = false;

  // Initialize database and set up subscription
  getChatHistoryDatabase()
    .then((db) => {
      if (isSubscribed) {
        // Create observable query
        const query = db.sessions
          .find()
          .sort({ updatedAt: 'desc' });

        // Subscribe to changes
        subscription = query.$.subscribe({
          next: (sessions: any[]) => {
            try {
              callback(sessions.map((s) => s.toJSON() as ChatSessionDocument));
            } catch (error) {
              console.error('Error in session callback:', error);
            }
          },
          error: (error: Error) => {
            console.error('Error in session subscription:', error);
          }
        });
      }
    })
    .catch((error) => {
      console.error('Failed to initialize chat history database subscription:', error);
    });

  // Mark as interested in subscription
  isSubscribed = true;

  // Return cleanup function
  return () => {
    isSubscribed = false;
    if (subscription) {
      subscription.unsubscribe();
      subscription = null;
    }
  };
}
