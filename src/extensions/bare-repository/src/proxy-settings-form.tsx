import type { ContextProject, ExtensionApi } from "@agw/extension-sdk";
import { useEffect, useState, type FormEvent } from "react";

import { readProxyUrl, writeProxyUrl } from "./proxy-settings";

export function ProxySettingsForm({
  api,
  project,
}: {
  api: ExtensionApi;
  project: ContextProject;
}) {
  const [proxyUrl, setProxyUrl] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);
  const server = api.context.get().server;

  useEffect(() => {
    const controller = new AbortController();
    readProxyUrl(api, controller.signal).then(
      (value) => {
        if (controller.signal.aborted) return;
        setProxyUrl(value ?? "");
        setLoading(false);
      },
      (caught: unknown) => {
        if (controller.signal.aborted) return;
        setError(caught instanceof Error ? caught.message : String(caught));
        setLoading(false);
      },
    );
    return () => controller.abort();
  }, [api]);

  async function save(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setSaved(false);
    setError("");
    try {
      await writeProxyUrl(api, proxyUrl);
      setProxyUrl(proxyUrl.trim());
      setSaved(true);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="panel proxy-settings" aria-labelledby="repository-proxy-heading">
      <h2 id="repository-proxy-heading">Repository proxy</h2>
      <p>
        Proxy for HTTPS repositories in {project.name} on {server.name}.
      </p>
      <p>Used by Clone, Sync and Sync all. The proxy address must be reachable from that Server.</p>
      <form onSubmit={(event) => void save(event)} aria-busy={loading || saving}>
        <fieldset disabled={loading || saving}>
          <label htmlFor="proxy-url">Proxy URL</label>
          <input
            id="proxy-url"
            type="url"
            value={proxyUrl}
            onChange={(event) => {
              setProxyUrl(event.target.value);
              setSaved(false);
            }}
            placeholder="http://127.0.0.1:7890"
            aria-describedby="proxy-help"
          />
          <p id="proxy-help">
            HTTP, HTTPS, SOCKS4 and SOCKS5 proxy URLs are supported. Leave empty to use the Server's
            existing Git proxy configuration.
          </p>
          <button type="submit" className="primary">
            {saving ? "Saving…" : "Save"}
          </button>
        </fieldset>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {saved && <p role="status">Saved.</p>}
    </section>
  );
}
