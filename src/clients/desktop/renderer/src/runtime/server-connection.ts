import {
  getEffectiveActiveServerProfile,
  type DesktopRuntimeState,
  type ServerInfo,
  type ServerProfile,
} from "./runtime-model";

class ServerConnectionError extends Error {}

export async function probeServer(
  profile: ServerProfile,
  token: string | null,
  signal?: AbortSignal,
): Promise<ServerInfo> {
  let response: Response;
  try {
    response = await fetch(`${profile.baseUrl.replace(/\/+$/u, "")}/api/server-info`, {
      credentials: "omit",
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      signal,
    });
  } catch (error) {
    signal?.throwIfAborted();
    throw new ServerConnectionError(
      error instanceof Error ? error.message : "Unable to reach Agw Server.",
    );
  }
  let body: { title?: string; data?: ServerInfo };
  try {
    body = (await response.json()) as typeof body;
  } catch (error) {
    signal?.throwIfAborted();
    if (error instanceof TypeError) throw new ServerConnectionError(error.message);
    throw error;
  }
  if (!response.ok || !body.data) throw new Error(body.title || "Unable to reach Agw Server.");
  return body.data;
}

function abortable<T>(operation: Promise<T>, signal: AbortSignal): Promise<T> {
  return new Promise((resolve, reject) => {
    const abort = () => {
      signal.removeEventListener("abort", abort);
      reject(signal.reason);
    };
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) abort();
    operation.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}

export async function connectServer(
  initialState: DesktopRuntimeState,
  readRuntimeState: () => Promise<DesktopRuntimeState>,
  signal: AbortSignal,
): Promise<{ state: DesktopRuntimeState; info: ServerInfo }> {
  if (getEffectiveActiveServerProfile(initialState).kind !== "local") {
    return {
      state: initialState,
      info: await probeServer(
        getEffectiveActiveServerProfile(initialState),
        initialState.activeToken,
        signal,
      ),
    };
  }

  const deadline = new AbortController();
  const timeout = setTimeout(() => deadline.abort(), 30_000);
  const waitingSignal = AbortSignal.any([signal, deadline.signal]);
  let state = initialState;
  let lastError: unknown;
  try {
    for (;;) {
      waitingSignal.throwIfAborted();
      const attempt = new AbortController();
      const attemptTimeout = setTimeout(() => attempt.abort(), 3_000);
      try {
        const info = await probeServer(
          getEffectiveActiveServerProfile(state),
          state.activeToken,
          AbortSignal.any([waitingSignal, attempt.signal]),
        );
        waitingSignal.throwIfAborted();
        return { state, info };
      } catch (error) {
        waitingSignal.throwIfAborted();
        if (!attempt.signal.aborted && !(error instanceof ServerConnectionError)) throw error;
        lastError = error;
      } finally {
        clearTimeout(attemptTimeout);
      }

      let retryTimer: ReturnType<typeof setTimeout> | undefined;
      try {
        await abortable(
          new Promise<void>((resolve) => {
            retryTimer = setTimeout(resolve, 1_000);
          }),
          waitingSignal,
        );
      } finally {
        clearTimeout(retryTimer);
      }
      state = await abortable(readRuntimeState(), waitingSignal);
    }
  } catch (error) {
    signal.throwIfAborted();
    if (deadline.signal.aborted) {
      throw new Error("Unable to reach Agw Server after 30 seconds.", { cause: lastError });
    }
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}
