"use client";

import type { ChatProjectSettingsStorageValues } from "./types";

const STORAGE_KEY_PREFIX = "agw:chat-settings:";

function canUseStorage() {
  return typeof window !== "undefined" && typeof window.localStorage !== "undefined";
}

function getStorageKey(projectId: string) {
  return `${STORAGE_KEY_PREFIX}${projectId}`;
}

function parseJson<T>(value: string | null): T | undefined {
  if (!value) {
    return undefined;
  }

  try {
    return JSON.parse(value) as T;
  } catch (error) {
    console.warn("Failed to parse chat settings from local storage.", error);
    return undefined;
  }
}

export const chatSettingsStorage = {
  get(projectId: string): ChatProjectSettingsStorageValues {
    if (!canUseStorage() || !projectId) {
      return {};
    }

    try {
      return (
        parseJson<ChatProjectSettingsStorageValues>(
          localStorage.getItem(getStorageKey(projectId)),
        ) ?? {}
      );
    } catch (error) {
      console.warn("Storage access failed while reading chat settings.", error);
      return {};
    }
  },
  set(projectId: string, values: ChatProjectSettingsStorageValues): void {
    if (!canUseStorage() || !projectId) {
      return;
    }

    try {
      const current = chatSettingsStorage.get(projectId);
      localStorage.setItem(getStorageKey(projectId), JSON.stringify({ ...current, ...values }));
    } catch (error) {
      console.warn("Storage access failed while writing chat settings.", error);
    }
  },
};
