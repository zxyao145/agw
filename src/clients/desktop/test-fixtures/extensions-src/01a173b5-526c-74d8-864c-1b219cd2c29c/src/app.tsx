import { useEffect, useState } from "react";
import type { ExtensionApi } from "/__agw/sdk.js";

type RoundTripState = { status: "pending" } | { status: "ok" } | { status: "error"; message: string };

/** 读取 context，在存储目录中写入并读回 react.json。Reads the context, then writes and reads back react.json in storage. */
async function roundTrip(api: ExtensionApi): Promise<void> {
  const { extension } = api.context.get();
  const uri = `${extension.storageUri}/react.json`;
  const written = JSON.stringify({ extensionId: extension.id, nonce: crypto.randomUUID() });
  await api.fs.writeFile(uri, written);
  const read = await api.fs.readTextFile(uri);
  if (read !== written) throw new Error(`Storage round-trip mismatch: ${read}`);
}

export function App({ api }: { api: ExtensionApi }) {
  const [state, setState] = useState<RoundTripState>({ status: "pending" });

  useEffect(() => {
    void roundTrip(api).then(
      () => setState({ status: "ok" }),
      (error: unknown) => {
        setState({ status: "error", message: String(error) });
        throw error;
      },
    );
  }, [api]);

  if (state.status === "ok") return <p id="result">react-ts ok</p>;
  if (state.status === "error") return <p id="error">{state.message}</p>;
  return <p id="status">Loading</p>;
}
