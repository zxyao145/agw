import AsyncStorage from "@react-native-async-storage/async-storage";
import { createUuidV7 } from "@agw/api";
import * as SecureStore from "expo-secure-store";

import { parseLegacyConfig } from "./config-codec";
import type { ServerProfile, ServerProfilesStateV1 } from "./types";

export const PROFILES_STATE_KEY = "agw.serverProfiles.v1";
export const LEGACY_CONFIG_KEY = "agw.localConfig";
const tokenKeyPrefix = "agw.serverProfile.token.";

export type LoadedProfiles = {
  state: ServerProfilesStateV1;
  migratedProfileId: string | null;
};

export const emptyProfilesState = (): ServerProfilesStateV1 => ({
  version: 1,
  activeProfileId: null,
  profiles: [],
});

export function getProfileTokenKey(profileId: string): string {
  return `${tokenKeyPrefix}${profileId}`;
}

export async function loadProfiles(): Promise<LoadedProfiles> {
  const storedState = await AsyncStorage.getItem(PROFILES_STATE_KEY);
  if (storedState) return { state: parseProfilesState(storedState), migratedProfileId: null };

  const legacyContent = await SecureStore.getItemAsync(LEGACY_CONFIG_KEY);
  if (!legacyContent) return { state: emptyProfilesState(), migratedProfileId: null };

  const legacy = parseLegacyConfig(JSON.parse(legacyContent));
  const id = createUuidV7();
  const usesHttp = legacy.serverUrl.startsWith("http://");
  const profile: ServerProfile = {
    id,
    name: new URL(legacy.serverUrl).host,
    serverUrl: legacy.serverUrl,
    apiMajorVersion: 1,
    allowInsecureHttp: false,
  };
  const state: ServerProfilesStateV1 = {
    version: 1,
    activeProfileId: usesHttp ? null : id,
    profiles: [profile],
  };

  await SecureStore.setItemAsync(getProfileTokenKey(id), legacy.token);
  await AsyncStorage.setItem(PROFILES_STATE_KEY, JSON.stringify(state));
  await SecureStore.deleteItemAsync(LEGACY_CONFIG_KEY);
  return { state, migratedProfileId: id };
}

export async function persistProfilesState(state: ServerProfilesStateV1): Promise<void> {
  await AsyncStorage.setItem(PROFILES_STATE_KEY, JSON.stringify(state));
}

export function readProfileToken(profileId: string): Promise<string | null> {
  return SecureStore.getItemAsync(getProfileTokenKey(profileId));
}

export function writeProfileToken(profileId: string, token: string): Promise<void> {
  return SecureStore.setItemAsync(getProfileTokenKey(profileId), token);
}

export function deleteProfileToken(profileId: string): Promise<void> {
  return SecureStore.deleteItemAsync(getProfileTokenKey(profileId));
}

function parseProfilesState(content: string): ServerProfilesStateV1 {
  let value: unknown;
  try {
    value = JSON.parse(content);
  } catch {
    throw new Error("Saved server profiles are invalid.");
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("Saved server profiles are invalid.");
  }
  const raw = value as Partial<ServerProfilesStateV1>;
  if (raw.version !== 1 || !Array.isArray(raw.profiles)) {
    throw new Error("Saved server profiles use an unsupported version.");
  }

  const profiles = raw.profiles.filter(isServerProfile);
  if (profiles.length !== raw.profiles.length) {
    throw new Error("A saved server profile is invalid.");
  }
  const activeProfileId =
    typeof raw.activeProfileId === "string" &&
    profiles.some((profile) => profile.id === raw.activeProfileId)
      ? raw.activeProfileId
      : null;
  return { version: 1, activeProfileId, profiles };
}

function isServerProfile(value: unknown): value is ServerProfile {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const profile = value as Partial<ServerProfile>;
  return (
    typeof profile.id === "string" &&
    typeof profile.name === "string" &&
    typeof profile.serverUrl === "string" &&
    profile.apiMajorVersion === 1 &&
    typeof profile.allowInsecureHttp === "boolean"
  );
}
