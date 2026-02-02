'use client';

// Only import PouchDB on the client side
import PouchDB from 'pouchdb';
import PouchDBFind from 'pouchdb-find';
import type { AiMessage } from '@/types';

// Register the find plugin
if (typeof window !== 'undefined') {
  PouchDB.plugin(PouchDBFind);
}

// Chat session document type
export interface ChatSessionDocument {
  _id: string; // ULID (PouchDB uses _id)
  _rev?: string; // PouchDB revision
  threadId: string;
  title: string; // Generated from first message or custom
  messages: AiMessage[];
  createdAt: number; // Unix timestamp
  updatedAt: number; // Unix timestamp
  size: number; // Approximate size in bytes for quota management
}

// Database type
export const DB_NAME = 'claudecode_chat_history';
let dbInstance: PouchDB.Database<ChatSessionDocument> | null = null;
let dbPromise: Promise<PouchDB.Database<ChatSessionDocument>> | null = null;

/**
 * Get or create the chat history database instance
 * Uses Promise-based singleton pattern to prevent multiple instantiations
 */
export async function getChatHistoryDatabase(): Promise<PouchDB.Database<ChatSessionDocument>> {
  // Ensure we're in a browser environment
  if (typeof window === 'undefined') {
    throw new Error('PouchDB can only be used in a browser environment');
  }

  // Return existing instance if available
  if (dbInstance) {
    return dbInstance;
  }

  // Return existing promise if database is being created
  if (dbPromise) {
    return dbPromise;
  }

  // Create new database
  dbPromise = (async () => {
    try {
      const db = new PouchDB<ChatSessionDocument>(DB_NAME);

      // Create an index for faster queries
      await db.createIndex({
        index: {
          fields: ['threadId', 'updatedAt', 'createdAt']
        }
      });

      // Create an index for faster queries
      await db.createIndex({
        index: {
          fields: ['updatedAt']
        }
      });

      // Create an index for faster queries
      await db.createIndex({
        index: {
          fields: ['createdAt']
        }
      });

      dbInstance = db;
      return db;
    } catch (error) {
      // Reset promise on error so retry is possible
      dbPromise = null;
      throw error;
    }
  })();

  return dbPromise;
}

/**
 * Calculate approximate size of a session in bytes
 */
export function calculateSessionSize(session: Omit<ChatSessionDocument, 'size' | '_rev'>): number {
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
export async function getTotalSize(db: PouchDB.Database<ChatSessionDocument>): Promise<number> {
  const result = await db.allDocs({ include_docs: true });
  return result.rows.reduce((total, row) => {
    return total + (row.doc?.size || 0);
  }, 0);
}

/**
 * Get session count
 */
export async function getSessionCount(db: PouchDB.Database<ChatSessionDocument>): Promise<number> {
  const result = await db.allDocs();
  return result.total_rows;
}

/**
 * Clean up old sessions if limits are exceeded
 * Returns number of sessions deleted
 */
export async function cleanupOldSessions(db: PouchDB.Database<ChatSessionDocument>): Promise<number> {
  let deletedCount = 0;

  try {
    // Check session count limit
    const count = await getSessionCount(db);
    if (count > MAX_SESSIONS) {
      console.warn(`Session count ${count} exceeds limit of ${MAX_SESSIONS}, cleaning up...`);
      const excess = count - MAX_SESSIONS;
      // Get all sessions sorted by updatedAt
      const result = await db.find({
        selector: {},
        sort: [{ createdAt: 'desc' }],
        limit: excess
      });

      for (const doc of result.docs) {
        await db.remove(doc);
        deletedCount++;
      }
    }

    // Check size limit
    let totalSize = await getTotalSize(db);
    while (totalSize > MAX_SIZE_BYTES) {
      console.warn(`Total size ${totalSize} exceeds limit of ${MAX_SIZE_BYTES}, cleaning up...`);
      // Get oldest session
      const result = await db.find({
        selector: {},
        sort: [{ createdAt: 'desc' }],
        limit: 1
      });

      if (result.docs.length === 0) break;

      const oldestDoc = result.docs[0];
      totalSize -= oldestDoc.size;
      await db.remove(oldestDoc);
      deletedCount++;
    }
  } catch (error) {
    console.error('Error cleaning up old sessions:', error);
  }

  console.warn(`Cleanup complete, deleted ${deletedCount} sessions`);

  return deletedCount;
}

interface UpsertResult {
  updated: boolean;
  rev: string;
  id: string;
}

    /* istanbul ignore next */
const upsertInner = (db: PouchDB.Database<ChatSessionDocument>, 
   docId : any,
   diffFun: Function
  ): Promise<UpsertResult> =>{
   if (typeof docId !== 'string') {
    return Promise.reject(new Error('doc id is required'));
  }

  return db.get(docId).catch(function (err) {
    /* istanbul ignore next */
    if (err.status !== 404) {
      throw err;
    }
    return {};
  }).then((doc) => {
    // the user might change the _rev, so save it for posterity
    var docRev = (doc as any)._rev;
    var newDoc = diffFun(doc);

    if (!newDoc) {
      // if the diffFun returns falsy, we short-circuit as
      // an optimization
      return { updated: false, rev: docRev, id: docId };
    }

    // users aren't allowed to modify these values,
    // so reset them here
    newDoc._id = docId;
    newDoc._rev = docRev;
    return tryAndPut(db, newDoc, diffFun);
  });
}

function tryAndPut(
  db: PouchDB.Database<ChatSessionDocument>,
  doc: ChatSessionDocument,
  diffFun: Function,
) {
  return db.put(doc).then(
    function (res) {
      return {
        updated: true,
        rev: res.rev,
        id: doc._id,
      };
    },
    function (err) {
      /* istanbul ignore next */
      if (err.status !== 409) {
        throw err;
      }
      return upsertInner(db, doc._id, diffFun);
    },
  );
}
export async function upsert(
  db: PouchDB.Database<ChatSessionDocument>,
  docId: any,
  diffFun: (doc: ChatSessionDocument) => ChatSessionDocument,
) {
  var promise = await upsertInner(db, docId, diffFun);
  return promise;
};