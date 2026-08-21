import type { AgwApiClient } from "@agw/api";

export type ServerProfile = {
  id: string;
  name: string;
  serverUrl: string;
  apiMajorVersion: 1;
  allowInsecureHttp: boolean;
};

export type ServerProfilesStateV1 = {
  version: 1;
  activeProfileId: string | null;
  profiles: ServerProfile[];
};

export type LegacyLocalConfigV2 = {
  version: 2;
  apiMajorVersion: 1;
  serverUrl: string;
  token: string;
};

export type VerifiedServer = {
  profile: ServerProfile;
  token: string;
  client: AgwApiClient;
};

export type ProfileDraft = {
  id?: string;
  name: string;
  serverUrl: string;
  token: string;
  allowInsecureHttp: boolean;
};
