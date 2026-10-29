"use client";

import * as React from "react";
import { apiDownloadBlob, MessageContentType, type AiMessage } from "@agw/api";
import { chatHttpClient } from "@agw/chat-runtime";
import type { ProjectDirectoryOption } from "@agw/projects";

type ConversationImageFileRequest = {
  path: string;
  directoryId?: string;
};

type ConversationImageFileResolution = ConversationImageFileRequest | { error: string };

type ConversationImageFileContextValue = {
  projectId: string | null;
  serverId: string;
  conversationId: string | null;
  directories: readonly ProjectDirectoryOption[];
  messages: readonly AiMessage[];
  currentMessage?: AiMessage;
};

const IMAGE_SCHEME = /^[a-z][a-z\d+.-]*:/iu;
const WINDOWS_ABSOLUTE_PATH = /^[a-z]:[\\/]/iu;
const EMPTY_DIRECTORIES: readonly ProjectDirectoryOption[] = [];
const EMPTY_MESSAGES: readonly AiMessage[] = [];

const ConversationImageFileContext = React.createContext<
  ConversationImageFileContextValue | undefined
>(undefined);

export function ConversationImageFileProvider({
  projectId,
  serverId,
  conversationId = null,
  directories = EMPTY_DIRECTORIES,
  messages = EMPTY_MESSAGES,
  children,
}: React.PropsWithChildren<{
  projectId: string | null;
  serverId: string;
  conversationId?: string | null;
  directories?: readonly ProjectDirectoryOption[];
  messages?: readonly AiMessage[];
}>) {
  const value = React.useMemo(
    () => ({ projectId, serverId, conversationId, directories, messages }),
    [conversationId, directories, messages, projectId, serverId],
  );

  return (
    <ConversationImageFileContext.Provider value={value}>
      {children}
    </ConversationImageFileContext.Provider>
  );
}

export function ConversationImageMessageProvider({
  message,
  children,
}: React.PropsWithChildren<{ message: AiMessage }>) {
  const fileContext = React.useContext(ConversationImageFileContext);
  const value = React.useMemo(
    () => (fileContext ? { ...fileContext, currentMessage: message } : undefined),
    [fileContext, message],
  );

  return value ? (
    <ConversationImageFileContext.Provider value={value}>
      {children}
    </ConversationImageFileContext.Provider>
  ) : (
    <>{children}</>
  );
}

function isWindowsAbsolutePath(path: string): boolean {
  return WINDOWS_ABSOLUTE_PATH.test(path) || path.startsWith("\\\\");
}

function normalizeDirectoryPath(path: string, caseInsensitive: boolean): string {
  const normalized = path.replaceAll("\\", "/");
  const trimmed = normalized.length > 1 ? normalized.replace(/\/+$/u, "") : normalized;
  return caseInsensitive ? trimmed.toLowerCase() : trimmed;
}

function pathWithoutQueryAndFragment(src: string): string {
  const queryOrFragmentIndex = src.search(/[?#]/u);
  return queryOrFragmentIndex === -1 ? src : src.slice(0, queryOrFragmentIndex);
}

function containsTraversalSegment(path: string): boolean {
  return /(?:^|[\\/])(?:\.|%2e){1,2}(?:[\\/]|$)/iu.test(path);
}

function getWindowsImagePath(src: string): string | { error: string } {
  try {
    const isUncPath = src.startsWith("\\\\");
    const urlPath = src.replaceAll("\\", "/");
    const url = isUncPath ? new URL(urlPath, "http://agw.invalid") : new URL(`file:///${urlPath}`);
    const sourcePath = pathWithoutQueryAndFragment(src);
    if (containsTraversalSegment(sourcePath)) return decodeURIComponent(sourcePath);

    const decodedPath = decodeURIComponent(url.pathname);
    if (isUncPath) {
      return `\\\\${url.hostname}${decodedPath.replaceAll("/", "\\")}`;
    }

    const drivePath = /^\/[a-z]:\//iu.test(decodedPath) ? decodedPath.slice(1) : decodedPath;
    return drivePath.replaceAll("/", src.includes("\\") ? "\\" : "/");
  } catch (error) {
    return { error: getErrorMessage(error) };
  }
}

function getLocalImagePath(src: string): string | null | { error: string } {
  if (!src) return null;
  if (src.startsWith("//")) return null;
  if (isWindowsAbsolutePath(src)) return getWindowsImagePath(src);

  if (IMAGE_SCHEME.test(src)) {
    if (!/^file:/iu.test(src)) return null;

    try {
      const url = new URL(src);
      const decodedPath = decodeURIComponent(url.pathname);
      if (url.hostname && url.hostname !== "localhost") {
        return `\\\\${url.hostname}${decodedPath.replaceAll("/", "\\")}`;
      }

      return /^\/[a-z]:\//iu.test(decodedPath) ? decodedPath.slice(1) : decodedPath;
    } catch (error) {
      return { error: getErrorMessage(error) };
    }
  }

  try {
    const url = new URL(src, "http://agw.invalid");
    const isAbsolutePath = src.startsWith("/");
    const sourcePath = pathWithoutQueryAndFragment(src);
    const containsTraversal = containsTraversalSegment(sourcePath);
    const decodedPath = decodeURIComponent(url.pathname);
    const path = containsTraversal
      ? decodeURIComponent(sourcePath)
      : isAbsolutePath
        ? decodedPath
        : decodedPath.slice(1);
    return path;
  } catch (error) {
    return { error: getErrorMessage(error) };
  }
}

export function resolveConversationImageFileRequest(
  src: string,
  directories: readonly ProjectDirectoryOption[],
): ConversationImageFileResolution | null {
  const path = getLocalImagePath(src);
  if (path === null || typeof path !== "string") return path;

  const absolutePath = path.startsWith("/") || isWindowsAbsolutePath(path);
  if (!absolutePath) return { path };

  const windowsPath = isWindowsAbsolutePath(path);
  const normalizedPath = normalizeDirectoryPath(path, windowsPath);
  const matchingDirectory = directories
    .filter((directory) => directory.id && !directory.isPrimary && directory.path)
    .map((directory) => ({
      directory,
      normalizedPath: normalizeDirectoryPath(directory.path, windowsPath),
    }))
    .filter(({ normalizedPath: root }) => {
      const boundary = root.endsWith("/") ? root : `${root}/`;
      return normalizedPath === root || normalizedPath.startsWith(boundary);
    })
    .sort((left, right) => right.normalizedPath.length - left.normalizedPath.length)[0]?.directory;

  return {
    path,
    ...(matchingDirectory?.id ? { directoryId: matchingDirectory.id } : {}),
  };
}

function readMessageProperty(message: AiMessage, key: string): string | null {
  const value = message.additionalProperties?.[key];
  return typeof value === "string" && value.length > 0 ? value : null;
}

function readContentProperty(content: AiMessage["contents"][number], key: string): string | null {
  const value = content.additionalProperties?.[key];
  return typeof value === "string" && value.length > 0 ? value : null;
}

function getMessageIdentity(message: AiMessage): string {
  return JSON.stringify([
    message.messageId,
    readMessageProperty(message, "sourceMessageId"),
    readMessageProperty(message, "threadId"),
    readMessageProperty(message, "turnId"),
    readMessageProperty(message, "itemId"),
    readMessageProperty(message, "producerScopeId"),
  ]);
}

function findCurrentMessageIndex(
  messages: readonly AiMessage[],
  currentMessage: AiMessage,
): number {
  let referenceIndex = -1;
  for (const [index, message] of messages.entries()) {
    if (message !== currentMessage) continue;
    if (referenceIndex >= 0) return -1;
    referenceIndex = index;
  }
  if (referenceIndex >= 0) return referenceIndex;

  let messageIdIndex = -1;
  for (const [index, message] of messages.entries()) {
    if (message.messageId !== currentMessage.messageId) continue;
    if (messageIdIndex >= 0) {
      messageIdIndex = -1;
      break;
    }
    messageIdIndex = index;
  }
  if (messageIdIndex >= 0) return messageIdIndex;

  const identity = getMessageIdentity(currentMessage);
  let identityIndex = -1;
  for (const [index, message] of messages.entries()) {
    if (getMessageIdentity(message) !== identity) continue;
    if (identityIndex >= 0) return -1;
    identityIndex = index;
  }
  return identityIndex;
}

function hasSameImageScope(candidate: AiMessage, current: AiMessage): boolean {
  const currentTurnId = readMessageProperty(current, "turnId");
  if (!currentTurnId) return false;

  const candidateTurnId = readMessageProperty(candidate, "turnId");
  if (candidateTurnId !== currentTurnId) return false;

  const currentProducerScopeId = readMessageProperty(current, "producerScopeId");
  const candidateProducerScopeId = readMessageProperty(candidate, "producerScopeId");
  const currentThreadId = readMessageProperty(current, "threadId");
  const candidateThreadId = readMessageProperty(candidate, "threadId");

  if (!currentProducerScopeId && !currentThreadId) return false;
  if (currentProducerScopeId && candidateProducerScopeId !== currentProducerScopeId) return false;
  if (currentThreadId && candidateThreadId !== currentThreadId) return false;
  return true;
}

export function findPreviousConversationImageSnapshot(
  messages: readonly AiMessage[],
  currentMessage: AiMessage,
  path: string,
): string | null {
  const currentIndex = findCurrentMessageIndex(messages, currentMessage);
  if (currentIndex < 0) return null;

  let dataUri: string | null = null;
  for (let messageIndex = 0; messageIndex < currentIndex; messageIndex++) {
    const candidate = messages[messageIndex]!;
    for (const content of candidate.contents) {
      if (
        content.type !== MessageContentType.DataContent ||
        readContentProperty(content, "imagePath") !== path ||
        !hasSameImageScope(candidate, currentMessage)
      ) {
        continue;
      }

      const uri = content.uri;
      if (typeof uri === "string" && uri.startsWith("data:image/")) dataUri = uri;
    }
  }
  return dataUri;
}

type ImageFileLoadState =
  | { key: string; status: "loading" }
  | { key: string; status: "loaded"; src: string }
  | { key: string; status: "error"; message: string };

function readBlobAsDataUrl(blob: Blob, signal: AbortSignal): Promise<string> {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    const abortReading = () => reader.abort();
    const cleanup = () => signal.removeEventListener("abort", abortReading);

    reader.onload = () => {
      cleanup();
      if (typeof reader.result !== "string") {
        reject(new Error("Unable to read image data."));
        return;
      }
      resolve(reader.result);
    };
    reader.onerror = () => {
      cleanup();
      reject(reader.error ?? new Error("Unable to read image data."));
    };
    reader.onabort = () => {
      cleanup();
      reject(signal.reason ?? new DOMException("Image reading was aborted.", "AbortError"));
    };
    signal.addEventListener("abort", abortReading, { once: true });
    try {
      signal.throwIfAborted();
      reader.readAsDataURL(blob);
    } catch (error) {
      cleanup();
      reject(error);
    }
  });
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function useConversationImageFileSource(src: string): {
  src: string | undefined;
  isLoading: boolean;
  error: string | undefined;
} {
  const fileContext = React.useContext(ConversationImageFileContext);
  const hasFileContext = fileContext !== undefined;
  const directories = fileContext?.directories ?? EMPTY_DIRECTORIES;
  const resolution = React.useMemo(
    () => (hasFileContext ? resolveConversationImageFileRequest(src, directories) : null),
    [directories, hasFileContext, src],
  );
  const request = resolution && "path" in resolution ? resolution : null;
  const resolutionError = resolution && "error" in resolution ? resolution.error : undefined;
  const projectId = fileContext?.projectId ?? null;
  const serverId = fileContext?.serverId ?? "";
  const conversationId = fileContext?.conversationId ?? null;
  const currentMessage = fileContext?.currentMessage;
  const messages = fileContext?.messages ?? EMPTY_MESSAGES;
  const localSnapshot = React.useMemo(
    () =>
      currentMessage && request
        ? findPreviousConversationImageSnapshot(messages, currentMessage, request.path)
        : null,
    [currentMessage, messages, request],
  );
  const requestKey =
    fileContext && request
      ? JSON.stringify([
          serverId,
          projectId,
          conversationId,
          currentMessage ? getMessageIdentity(currentMessage) : null,
          request.path,
          request.directoryId ?? null,
        ])
      : null;
  const [loadState, setLoadState] = React.useState<ImageFileLoadState | null>(null);

  React.useEffect(() => {
    if (!hasFileContext || !request || !requestKey || localSnapshot !== null) return;

    const controller = new AbortController();
    setLoadState({ key: requestKey, status: "loading" });

    if (!projectId) {
      setLoadState({
        key: requestKey,
        status: "error",
        message: "A project is required to load a local image.",
      });
      return () => controller.abort();
    }

    void (async () => {
      try {
        const messageId = currentMessage?.messageId;
        if (projectId && conversationId && messageId) {
          const savedImage = await chatHttpClient.getConversationImage(
            {
              serverId,
              projectId,
              conversationId,
            },
            { messageId, path: request.path, signal: controller.signal },
          );
          if (savedImage.dataUri !== null) {
            if (!controller.signal.aborted) {
              setLoadState({ key: requestKey, status: "loaded", src: savedImage.dataUri });
            }
            return;
          }
        }

        const blob = await apiDownloadBlob("/api/files/image", {
          params: {
            query: {
              projectId,
              path: request.path,
              directoryId: request.directoryId,
            },
          },
          signal: controller.signal,
        });
        if (!blob.type.toLowerCase().startsWith("image/")) {
          throw new Error("The server returned a non-image file.");
        }

        const dataUrl = await readBlobAsDataUrl(blob, controller.signal);
        if (!controller.signal.aborted) {
          setLoadState({ key: requestKey, status: "loaded", src: dataUrl });
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          setLoadState({ key: requestKey, status: "error", message: getErrorMessage(error) });
        }
      }
    })();

    return () => controller.abort();
  }, [
    conversationId,
    currentMessage?.messageId,
    hasFileContext,
    localSnapshot,
    projectId,
    request,
    requestKey,
    serverId,
  ]);

  if (resolutionError) return { src: undefined, isLoading: false, error: resolutionError };
  if (!hasFileContext || !request) return { src, isLoading: false, error: undefined };
  if (localSnapshot !== null) return { src: localSnapshot, isLoading: false, error: undefined };

  const currentLoadState = loadState?.key === requestKey ? loadState : null;
  return {
    src: currentLoadState?.status === "loaded" ? currentLoadState.src : undefined,
    isLoading: currentLoadState === null || currentLoadState.status === "loading",
    error: currentLoadState?.status === "error" ? currentLoadState.message : undefined,
  };
}
