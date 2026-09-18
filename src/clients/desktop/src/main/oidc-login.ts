import { createHash, randomBytes } from "node:crypto";

import type { ServerProfile } from "../shared/contracts";

export type LoginProvider = { id: string; displayName: string; type?: "Oidc" | "OAuth2" };
type AuthRedirect = { state: string; code?: string; error?: string };
type PendingLogin = {
  profileId: string;
  baseUrl: string;
  state: string;
  verifier: string;
  deadline: number;
  processing: boolean;
  committing: boolean;
  controller: AbortController;
  timer: ReturnType<typeof setTimeout>;
  resolve(): void;
  reject(error: Error): void;
};

export type OidcLoginDependencies = {
  getProfile(profileId: string): Promise<ServerProfile>;
  openExternal(url: string): Promise<void>;
  fetch(url: string, init: RequestInit): Promise<Response>;
  saveToken(profileId: string, token: string, beforeCommit: () => Promise<void>): Promise<void>;
  loadToken(profileId: string): Promise<string | null>;
  deleteToken(profileId: string): Promise<void>;
};

const PROOF = /^[A-Za-z0-9_-]{43}$/u;
const PROVIDER = /^[a-z0-9]+(-[a-z0-9]+)*$/u;
const LOGIN_TIMEOUT = 10 * 60 * 1000;

export function parseDesktopAuthRedirect(value: string): AuthRedirect | null {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (
    url.protocol !== "agw-desktop:" ||
    url.host !== "auth" ||
    url.pathname !== "/complete" ||
    url.username ||
    url.password ||
    url.hash
  )
    return null;
  const keys = Array.from(url.searchParams.keys());
  if (
    new Set(keys).size !== keys.length ||
    keys.some((key) => !["state", "code", "error"].includes(key))
  )
    return null;
  const state = url.searchParams.get("state");
  const code = url.searchParams.get("code");
  const error = url.searchParams.get("error");
  if (!state || !PROOF.test(state) || Boolean(code) === Boolean(error)) return null;
  if (code && !PROOF.test(code)) return null;
  return { state, ...(code ? { code } : { error: error! }) };
}

function serverOrigin(profile: ServerProfile): string {
  const url = new URL(profile.baseUrl);
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
  if (
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    url.pathname !== "/" ||
    (url.protocol !== "https:" && !(url.protocol === "http:" && local))
  ) {
    throw new Error(
      "Third-party sign-in requires an HTTPS Server URL, or HTTP loopback for development.",
    );
  }
  return url.origin;
}

export class DesktopOidcLogin {
  private readonly pending = new Map<string, PendingLogin>();

  public constructor(private readonly dependencies: OidcLoginDependencies) {}

  public async providers(profileId: string): Promise<LoginProvider[]> {
    const profile = await this.dependencies.getProfile(profileId);
    const response = await this.dependencies.fetch(
      serverOrigin(profile) + "/api/auth/oidc/providers",
      {
        credentials: "omit",
        redirect: "error",
        signal: AbortSignal.timeout(15_000),
      },
    );
    if (response.status === 404 || response.status === 401) return [];
    const envelope = (await response.json().catch(() => {
      throw new Error("The Server returned an invalid sign-in response.");
    })) as { code?: number; data?: LoginProvider[] };
    if (
      !response.ok ||
      envelope.code !== 0 ||
      !Array.isArray(envelope.data) ||
      envelope.data.some(
        (provider) =>
          typeof provider.id !== "string" ||
          !PROVIDER.test(provider.id) ||
          provider.id.length > 64 ||
          typeof provider.displayName !== "string",
      )
    ) {
      throw new Error("Unable to load sign-in providers from this Server.");
    }
    return envelope.data;
  }

  public async login(profileId: string, providerId: string): Promise<void> {
    const profile = await this.dependencies.getProfile(profileId);
    const baseUrl = serverOrigin(profile);
    if (!PROVIDER.test(providerId) || providerId.length > 64)
      throw new Error("Invalid sign-in provider.");
    this.cancel(profileId);
    if (this.pending.has(profileId)) throw new Error("Sign-in is being saved. Please wait.");
    const verifier = randomBytes(32).toString("base64url");
    const state = randomBytes(32).toString("base64url");
    const challenge = createHash("sha256").update(verifier, "ascii").digest("base64url");
    const query = new URLSearchParams({
      providerId,
      client: "desktop",
      clientState: state,
      codeChallenge: challenge,
      codeChallengeMethod: "S256",
    });

    return new Promise<void>((resolve, reject) => {
      const pending: PendingLogin = {
        profileId,
        baseUrl,
        state,
        verifier,
        deadline: Date.now() + LOGIN_TIMEOUT,
        processing: false,
        committing: false,
        controller: new AbortController(),
        resolve,
        reject,
        timer: setTimeout(
          () => this.cancel(profileId, "Sign-in timed out. Please start again."),
          LOGIN_TIMEOUT,
        ),
      };
      pending.timer.unref();
      this.pending.set(profileId, pending);
      void this.dependencies
        .openExternal(baseUrl + "/api/auth/oidc/login?" + query.toString())
        .catch(() => this.cancel(profileId, "Unable to open the system browser."));
    });
  }

  public handleRedirect(value: string): boolean {
    const result = parseDesktopAuthRedirect(value);
    if (!result) return false;
    const pending = Array.from(this.pending.values()).find((login) => login.state === result.state);
    if (!pending || pending.processing) return true;
    pending.processing = true;
    void this.complete(pending, result);
    return true;
  }

  public cancel(profileId: string, message = "Sign-in was cancelled."): void {
    const pending = this.pending.get(profileId);
    if (!pending || pending.committing) return;
    pending.controller.abort();
    this.finish(pending, new Error(message));
  }

  public cancelAll(): void {
    for (const profileId of this.pending.keys()) this.cancel(profileId);
  }

  public async logout(profileId: string): Promise<void> {
    this.cancel(profileId);
    const profile = await this.dependencies.getProfile(profileId);
    const token = await this.dependencies.loadToken(profileId);
    let failed = false;
    try {
      if (token) {
        const response = await this.dependencies.fetch(
          serverOrigin(profile) + "/api/auth/desktop/logout",
          {
            method: "POST",
            credentials: "omit",
            redirect: "error",
            headers: { Authorization: "Bearer " + token },
            signal: AbortSignal.timeout(15_000),
          },
        );
        failed = !response.ok && response.status !== 401;
      }
    } catch {
      failed = true;
    } finally {
      await this.dependencies.deleteToken(profileId);
    }
    if (failed)
      throw new Error(
        "Signed out on this device. Server revocation was not confirmed; revoke the token from Web settings.",
      );
  }

  private async validate(pending: PendingLogin): Promise<void> {
    if (
      this.pending.get(pending.profileId) !== pending ||
      pending.controller.signal.aborted ||
      Date.now() >= pending.deadline
    )
      throw new Error("This sign-in request is no longer active.");
    const profile = await this.dependencies.getProfile(pending.profileId);
    if (serverOrigin(profile) !== pending.baseUrl)
      throw new Error("The Server address changed during sign-in. Please start again.");
  }

  private async complete(pending: PendingLogin, redirect: AuthRedirect): Promise<void> {
    let issuedToken: string | null = null;
    try {
      await this.validate(pending);
      if (redirect.error)
        throw new Error(
          redirect.error === "authorization-denied"
            ? "Sign-in was cancelled."
            : "Sign-in failed. Please start again.",
        );
      const response = await this.dependencies.fetch(
        pending.baseUrl + "/api/auth/desktop/exchange",
        {
          method: "POST",
          credentials: "omit",
          redirect: "error",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ code: redirect.code, codeVerifier: pending.verifier }),
          signal: AbortSignal.any([pending.controller.signal, AbortSignal.timeout(15_000)]),
        },
      );
      const result = (await response.json().catch(() => {
        throw new Error("The Server returned an invalid sign-in response.");
      })) as { code?: number; data?: { token?: string; userId?: string } };
      if (
        !response.ok ||
        result.code !== 0 ||
        typeof result.data?.token !== "string" ||
        !result.data.token.startsWith("agw_") ||
        typeof result.data.userId !== "string" ||
        !/^[0-9]+$/u.test(result.data.userId)
      ) {
        throw new Error(
          "Unable to complete sign-in. The request may have expired; please start again.",
        );
      }
      issuedToken = result.data.token;
      await this.validate(pending);
      await this.dependencies.saveToken(pending.profileId, issuedToken, async () => {
        await this.validate(pending);
        pending.committing = true;
      });
      this.finish(pending);
    } catch (error) {
      if (issuedToken) {
        // A cancelled or failed local save must not leave an unused credential behind.
        try {
          await this.dependencies.fetch(pending.baseUrl + "/api/auth/desktop/logout", {
            method: "POST",
            credentials: "omit",
            redirect: "error",
            headers: { Authorization: "Bearer " + issuedToken },
            signal: AbortSignal.timeout(15_000),
          });
        } catch {
          /* The user can revoke the named token through Web settings. */
        }
      }
      this.finish(pending, error instanceof Error ? error : new Error("Sign-in failed."));
    }
  }

  private finish(pending: PendingLogin, error?: Error): void {
    if (this.pending.get(pending.profileId) !== pending) return;
    clearTimeout(pending.timer);
    this.pending.delete(pending.profileId);
    if (error) pending.reject(error);
    else pending.resolve();
  }
}
