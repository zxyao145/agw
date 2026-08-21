import { createUuidV7 } from "@agw/api";
import React from "react";

import { getErrorMessage } from "@/lib/errors";
import { normalizeProfileName, normalizeServerUrl, normalizeToken } from "./config-codec";
import {
  deleteProfileToken,
  emptyProfilesState,
  loadProfiles,
  persistProfilesState,
  readProfileToken,
  writeProfileToken,
} from "./profile-store";
import { verifyServerProfile } from "./server-verification";
import type { ProfileDraft, ServerProfile, ServerProfilesStateV1, VerifiedServer } from "./types";

export type SessionStatus = "booting" | "unauthenticated" | "verifying" | "authenticated" | "error";

type SessionContextValue = {
  status: SessionStatus;
  state: ServerProfilesStateV1;
  verifiedServer: VerifiedServer | null;
  activeProfile: ServerProfile | null;
  error: string | null;
  isMutating: boolean;
  migratedProfileId: string | null;
  saveProfile(draft: ProfileDraft): Promise<ServerProfile>;
  activateProfile(profileId: string): Promise<void>;
  confirmInsecureHttp(profileId: string): Promise<void>;
  deleteProfile(profileId: string): Promise<void>;
  retryActiveProfile(): Promise<void>;
  markUnauthorized(): void;
};

const SessionContext = React.createContext<SessionContextValue | null>(null);

export function SessionProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const [state, setState] = React.useState<ServerProfilesStateV1>(emptyProfilesState);
  const [status, setStatus] = React.useState<SessionStatus>("booting");
  const [verifiedServer, setVerifiedServer] = React.useState<VerifiedServer | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [isMutating, setIsMutating] = React.useState(false);
  const [migratedProfileId, setMigratedProfileId] = React.useState<string | null>(null);

  const markUnauthorized = React.useCallback(() => {
    setVerifiedServer(null);
    setStatus("error");
    setError("The API token is invalid or has been revoked.");
  }, []);

  const verifyAndUse = React.useCallback(
    async (profile: ServerProfile, token: string) => {
      const verified = await verifyServerProfile(profile, token, markUnauthorized);
      setVerifiedServer(verified);
      setStatus("authenticated");
      setError(null);
      return verified;
    },
    [markUnauthorized],
  );

  const bootstrap = React.useCallback(async () => {
    setStatus("booting");
    setError(null);
    try {
      const loaded = await loadProfiles();
      setState(loaded.state);
      setMigratedProfileId(loaded.migratedProfileId);
      const active = loaded.state.profiles.find(
        (profile) => profile.id === loaded.state.activeProfileId,
      );
      if (!active) {
        setVerifiedServer(null);
        setStatus("unauthenticated");
        return;
      }
      const token = await readProfileToken(active.id);
      if (!token) throw new Error("The selected server profile is missing its API token.");
      setStatus("verifying");
      await verifyAndUse(active, token);
    } catch (caught) {
      setVerifiedServer(null);
      setStatus("error");
      setError(getErrorMessage(caught));
    }
  }, [verifyAndUse]);

  React.useEffect(() => {
    void bootstrap();
  }, [bootstrap]);

  const saveProfile = React.useCallback(
    async (draft: ProfileDraft): Promise<ServerProfile> => {
      setIsMutating(true);
      try {
        const existing = draft.id
          ? state.profiles.find((profile) => profile.id === draft.id)
          : undefined;
        const id = existing?.id ?? createUuidV7();
        const serverUrl = normalizeServerUrl(draft.serverUrl);
        const name = normalizeProfileName(draft.name, serverUrl);
        const previousToken = existing ? await readProfileToken(id) : null;
        const token = draft.token.trim()
          ? normalizeToken(draft.token)
          : normalizeToken(previousToken ?? "");
        const profile: ServerProfile = {
          id,
          name,
          serverUrl,
          apiMajorVersion: 1,
          allowInsecureHttp: serverUrl.startsWith("http://") ? draft.allowInsecureHttp : false,
        };

        setStatus("verifying");
        const verified = await verifyServerProfile(profile, token, markUnauthorized);
        const nextProfiles = existing
          ? state.profiles.map((item) => (item.id === id ? profile : item))
          : [...state.profiles, profile];
        const nextState: ServerProfilesStateV1 = {
          version: 1,
          activeProfileId: id,
          profiles: nextProfiles,
        };

        await writeProfileToken(id, token);
        try {
          await persistProfilesState(nextState);
        } catch (persistError) {
          if (previousToken) await writeProfileToken(id, previousToken);
          else await deleteProfileToken(id);
          throw persistError;
        }

        setState(nextState);
        setVerifiedServer(verified);
        setStatus("authenticated");
        setError(null);
        setMigratedProfileId((current) => (current === id ? null : current));
        return profile;
      } catch (caught) {
        if (verifiedServer) setStatus("authenticated");
        else setStatus("error");
        setError(getErrorMessage(caught));
        throw caught;
      } finally {
        setIsMutating(false);
      }
    },
    [markUnauthorized, state, verifiedServer],
  );

  const activateProfile = React.useCallback(
    async (profileId: string): Promise<void> => {
      const profile = state.profiles.find((item) => item.id === profileId);
      if (!profile) throw new Error("Server profile not found.");
      const token = await readProfileToken(profileId);
      if (!token) throw new Error("This server profile is missing its API token.");

      setIsMutating(true);
      try {
        const verified = await verifyServerProfile(profile, token, markUnauthorized);
        const nextState = { ...state, activeProfileId: profileId };
        await persistProfilesState(nextState);
        setState(nextState);
        setVerifiedServer(verified);
        setStatus("authenticated");
        setError(null);
      } catch (caught) {
        setError(getErrorMessage(caught));
        throw caught;
      } finally {
        setIsMutating(false);
      }
    },
    [markUnauthorized, state],
  );

  const confirmInsecureHttp = React.useCallback(
    async (profileId: string): Promise<void> => {
      const profile = state.profiles.find((item) => item.id === profileId);
      if (!profile) throw new Error("Server profile not found.");
      const token = await readProfileToken(profileId);
      if (!token) throw new Error("This server profile is missing its API token.");
      const confirmed = { ...profile, allowInsecureHttp: true };

      setIsMutating(true);
      try {
        const verified = await verifyServerProfile(confirmed, token, markUnauthorized);
        const nextState: ServerProfilesStateV1 = {
          ...state,
          activeProfileId: profileId,
          profiles: state.profiles.map((item) => (item.id === profileId ? confirmed : item)),
        };
        await persistProfilesState(nextState);
        setState(nextState);
        setVerifiedServer(verified);
        setStatus("authenticated");
        setError(null);
        setMigratedProfileId((current) => (current === profileId ? null : current));
      } catch (caught) {
        setError(getErrorMessage(caught));
        throw caught;
      } finally {
        setIsMutating(false);
      }
    },
    [markUnauthorized, state],
  );

  const deleteProfile = React.useCallback(
    async (profileId: string): Promise<void> => {
      setIsMutating(true);
      try {
        const nextState: ServerProfilesStateV1 = {
          version: 1,
          activeProfileId: state.activeProfileId === profileId ? null : state.activeProfileId,
          profiles: state.profiles.filter((profile) => profile.id !== profileId),
        };
        await persistProfilesState(nextState);
        await deleteProfileToken(profileId);
        setState(nextState);
        if (state.activeProfileId === profileId) {
          setVerifiedServer(null);
          setStatus("unauthenticated");
        }
        setError(null);
        setMigratedProfileId((current) => (current === profileId ? null : current));
      } finally {
        setIsMutating(false);
      }
    },
    [state],
  );

  const activeProfile =
    state.profiles.find((profile) => profile.id === state.activeProfileId) ?? null;
  const value = React.useMemo<SessionContextValue>(
    () => ({
      status,
      state,
      verifiedServer,
      activeProfile,
      error,
      isMutating,
      migratedProfileId,
      saveProfile,
      activateProfile,
      confirmInsecureHttp,
      deleteProfile,
      retryActiveProfile: bootstrap,
      markUnauthorized,
    }),
    [
      status,
      state,
      verifiedServer,
      activeProfile,
      error,
      isMutating,
      migratedProfileId,
      saveProfile,
      activateProfile,
      confirmInsecureHttp,
      deleteProfile,
      bootstrap,
      markUnauthorized,
    ],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = React.useContext(SessionContext);
  if (!value) throw new Error("useSession must be used inside SessionProvider.");
  return value;
}
