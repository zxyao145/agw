type MobileLocalConfigEnvelope = {
  title?: string;
  detail?: string | null;
  data?: {
    payload?: unknown;
  };
};

type FetchResponse = Pick<Response, "ok" | "status" | "statusText" | "json">;

type FetchRequest = (input: string, init: RequestInit) => Promise<FetchResponse>;

export type CopyMobileLocalConfigToClipboardOptions = {
  serverDomain: string;
  request?: FetchRequest;
  writeText: (value: string) => Promise<void>;
};

export async function copyMobileLocalConfigToClipboard({
  serverDomain,
  request = fetch,
  writeText,
}: CopyMobileLocalConfigToClipboardOptions): Promise<void> {
  const response = await request("/api/setup/mobile-local-config", {
    method: "POST",
    headers: {
      "content-type": "application/json",
    },
    body: JSON.stringify({ serverDomain }),
  });

  const body = (await response.json()) as MobileLocalConfigEnvelope;

  if (!response.ok) {
    throw new Error(
      body.detail ?? body.title ?? `Request failed: ${response.status} ${response.statusText}`,
    );
  }

  const payload = body.data?.payload;
  if (typeof payload !== "string" || payload.length === 0) {
    throw new Error("Mobile local config response payload is missing.");
  }

  await writeText(payload);
}
