import type { ExtensionApi } from "@agw/extension-sdk";
import type { FormEvent } from "react";
import { useEffect, useState } from "react";

type FeishuAppMode = "Webhook" | "App";
type TargetKind = "Chat" | "User";

const OPEN_DOMAINS = {
  feishu: "https://open.feishu.cn",
  lark: "https://open.larksuite.com",
} as const;

interface FeishuApp {
  id: string;
  name: string;
  mode: FeishuAppMode;
  openDomain: string;
  hasWebhookUrl: boolean;
  hasWebhookSecret: boolean;
  appId: string | null;
  hasAppSecret: boolean;
  enabled: boolean;
  version: number;
  connectionStatus: string;
  lastError?: string | null;
}

interface FeishuTarget {
  id: string;
  appId: string;
  kind: TargetKind;
  receiveId: string;
  name: string;
}

interface FeishuBinding {
  appId: string;
  openId: string;
  userId: string;
}

interface BindStart {
  code: string;
  expiresAt: string;
  confirmationUrl: string;
}

interface AppForm {
  name: string;
  mode: FeishuAppMode;
  openDomain: string;
  webhookUrl: string;
  webhookSecret: string;
  appId: string;
  appSecret: string;
}

interface TargetForm {
  kind: TargetKind;
  receiveId: string;
  name: string;
}

interface ApiEnvelope<T> {
  code: number;
  title?: string;
  data?: T;
}

const emptyForm: AppForm = {
  name: "",
  mode: "Webhook",
  openDomain: OPEN_DOMAINS.feishu,
  webhookUrl: "",
  webhookSecret: "",
  appId: "",
  appSecret: "",
};

export function FeishuSettings({ api }: { api: ExtensionApi }) {
  const [apps, setApps] = useState<FeishuApp[]>([]);
  const [selected, setSelected] = useState<FeishuApp | null>(null);
  const [form, setForm] = useState<AppForm>(emptyForm);
  const [targets, setTargets] = useState<FeishuTarget[]>([]);
  const [bindings, setBindings] = useState<FeishuBinding[]>([]);
  const [discovered, setDiscovered] = useState<Array<{ receiveId: string; name: string }>>([]);
  const [targetForm, setTargetForm] = useState<TargetForm>({
    kind: "Chat",
    receiveId: "",
    name: "",
  });
  const [bindStart, setBindStart] = useState<BindStart | null>(null);
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void loadApps();
  }, []);

  async function loadApps() {
    const items = await request<FeishuApp[]>(api, "apps/list");
    setApps(items);
    if (selected) {
      const refreshed = items.find((app) => app.id === selected.id) ?? null;
      setSelected(refreshed);
      if (refreshed) await loadDetails(refreshed);
    }
  }

  async function loadDetails(app: FeishuApp) {
    const [nextTargets, nextBindings] = await Promise.all([
      request<FeishuTarget[]>(api, "targets/list", { query: { appId: app.id } }),
      request<FeishuBinding[]>(api, "bind/list", { query: { appId: app.id } }),
    ]);
    setTargets(nextTargets);
    setBindings(nextBindings);
  }

  async function selectApp(app: FeishuApp) {
    setSelected(app);
    setForm({
      name: app.name,
      mode: app.mode,
      openDomain: app.openDomain,
      webhookUrl: "",
      webhookSecret: "",
      appId: app.appId ?? "",
      appSecret: "",
    });
    setBindStart(null);
    setDiscovered([]);
    await loadDetails(app);
  }

  function newApp() {
    setSelected(null);
    setForm(emptyForm);
    setTargets([]);
    setBindings([]);
    setBindStart(null);
    setDiscovered([]);
    setNotice("");
  }

  async function saveApp(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setNotice("");
    try {
      const body = selected ? { ...form, id: selected.id, version: selected.version } : form;
      const saved = await request<FeishuApp>(api, selected ? "apps/update" : "apps/create", {
        method: "POST",
        body,
      });
      setNotice("Application settings saved.");
      await loadApps();
      await selectApp(saved);
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function deleteApp() {
    if (!selected || !window.confirm(`Delete ${selected.name} and its saved targets and bindings?`))
      return;
    setBusy(true);
    try {
      await request<void>(api, "apps/delete", {
        method: "POST",
        body: { id: selected.id, version: selected.version },
      });
      setNotice("Application deleted.");
      newApp();
      await loadApps();
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function verifyApp() {
    if (!selected) return;
    setBusy(true);
    try {
      const result = await request<{ valid: boolean; error?: string }>(api, "apps/verify", {
        method: "POST",
        body: { appId: selected.id },
      });
      setNotice(
        result.valid
          ? "Feishu accepted the app credentials."
          : (result.error ?? "Feishu rejected the credentials."),
      );
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function discoverTargets() {
    if (!selected) return;
    setBusy(true);
    try {
      const discoveredChats = await request<Array<{ receiveId: string; name: string }>>(
        api,
        "targets/discover",
        {
          query: { appId: selected.id },
        },
      );
      setNotice(`Found ${discoveredChats.length} Feishu chats.`);
      if (discoveredChats[0])
        setTargetForm({
          kind: "Chat",
          receiveId: discoveredChats[0].receiveId,
          name: discoveredChats[0].name,
        });
      setDiscovered(discoveredChats);
      setTargets(
        await request<FeishuTarget[]>(api, "targets/list", { query: { appId: selected.id } }),
      );
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function saveTarget(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    setBusy(true);
    try {
      await request<FeishuTarget>(api, "targets/save", {
        method: "POST",
        body: { appId: selected.id, ...targetForm },
      });
      setTargetForm({ kind: "Chat", receiveId: "", name: "" });
      setNotice("Destination saved.");
      await loadDetails(selected);
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function deleteTarget(target: FeishuTarget) {
    if (!selected) return;
    setBusy(true);
    try {
      await request<void>(api, "targets/delete", {
        method: "POST",
        body: { appId: selected.id, id: target.id },
      });
      await loadDetails(selected);
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function startBinding() {
    if (!selected) return;
    setBusy(true);
    setBindStart(null);
    try {
      const result = await request<BindStart>(api, "bind/start", {
        method: "POST",
        body: { appId: selected.id },
      });
      setBindStart(result);
      setNotice("Send this code to the Feishu bot. The bot will return a confirmation link.");
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function deleteBinding(binding: FeishuBinding) {
    if (!selected) return;
    setBusy(true);
    try {
      await request<void>(api, "bind/delete", {
        method: "POST",
        body: { appId: selected.id, openId: binding.openId },
      });
      await loadDetails(selected);
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  async function testSend(target: FeishuTarget) {
    if (!selected || !window.confirm(`Send a test message to ${target.name}?`)) return;
    setBusy(true);
    try {
      await request(api, "test-send", {
        method: "POST",
        body: { appId: selected.id, targetId: target.id },
      });
      setNotice(`Test message sent to ${target.name}.`);
    } catch (error) {
      showError(error);
    } finally {
      setBusy(false);
    }
  }

  function showError(error: unknown) {
    setNotice(error instanceof Error ? error.message : "The request failed.");
  }

  return (
    <main className="feishu-page">
      <header className="page-header">
        <div>
          <p className="eyebrow">Server extension</p>
          <h1>Feishu</h1>
          <p>Configure Feishu bots, notification destinations and account links.</p>
        </div>
        <button type="button" onClick={newApp}>
          Add application
        </button>
      </header>

      {notice && (
        <p className="notice" role="status">
          {notice}
        </p>
      )}

      <section className="app-list" aria-label="Feishu applications">
        {apps.length === 0 && <p>No Feishu applications configured.</p>}
        {apps.map((app) => (
          <button
            className="app-row"
            key={app.id}
            type="button"
            onClick={() => void selectApp(app)}
          >
            <span>
              <strong>{app.name}</strong>
              <small>{app.mode}</small>
            </span>
            <span className={`status status-${app.connectionStatus.toLowerCase()}`}>
              {app.connectionStatus}
            </span>
          </button>
        ))}
      </section>

      <section className="settings-card">
        <h2>{selected ? `Edit ${selected.name}` : "New application"}</h2>
        <form className="form-grid" onSubmit={(event) => void saveApp(event)}>
          <label>
            Name
            <input
              value={form.name}
              maxLength={200}
              required
              onChange={(event) => setForm({ ...form, name: event.target.value })}
            />
          </label>
          <label>
            Connection mode
            <select
              value={form.mode}
              onChange={(event) => setForm({ ...form, mode: event.target.value as FeishuAppMode })}
            >
              <option value="Webhook">Group bot Webhook</option>
              <option value="App">Custom app</option>
            </select>
          </label>
          {form.mode === "Webhook" ? (
            <>
              <label className="wide">
                Webhook URL{" "}
                {selected?.hasWebhookUrl && (
                  <small>Saved URL is hidden; leave blank to keep it.</small>
                )}
                <input
                  type="url"
                  required={!selected?.hasWebhookUrl}
                  placeholder={
                    selected?.hasWebhookUrl
                      ? "Saved"
                      : `${OPEN_DOMAINS.feishu}/open-apis/bot/v2/hook/…`
                  }
                  value={form.webhookUrl}
                  onChange={(event) => setForm({ ...form, webhookUrl: event.target.value })}
                />
              </label>
              <SecretField
                label="Signing secret"
                saved={selected?.hasWebhookSecret ?? false}
                value={form.webhookSecret}
                onChange={(value) => setForm({ ...form, webhookSecret: value })}
              />
            </>
          ) : (
            <>
              <label>
                Open platform domain
                <select
                  value={form.openDomain}
                  onChange={(event) => setForm({ ...form, openDomain: event.target.value })}
                >
                  <option value={OPEN_DOMAINS.feishu}>Feishu</option>
                  <option value={OPEN_DOMAINS.lark}>Lark</option>
                </select>
              </label>
              <label>
                App ID
                <input
                  value={form.appId}
                  required
                  onChange={(event) => setForm({ ...form, appId: event.target.value })}
                />
              </label>
              <SecretField
                label="App Secret"
                saved={selected?.hasAppSecret ?? false}
                required={!selected?.hasAppSecret}
                value={form.appSecret}
                onChange={(value) => setForm({ ...form, appSecret: value })}
              />
            </>
          )}
          <div className="form-actions wide">
            <button type="submit" disabled={busy}>
              {selected ? "Save application" : "Create application"}
            </button>
            {selected && (
              <button
                type="button"
                className="secondary"
                disabled={busy}
                onClick={() => void deleteApp()}
              >
                Delete
              </button>
            )}
            {selected?.mode === "App" && (
              <button
                type="button"
                className="secondary"
                disabled={busy}
                onClick={() => void verifyApp()}
              >
                Verify credentials
              </button>
            )}
          </div>
        </form>
      </section>

      {selected && (
        <div className="detail-grid">
          <section className="settings-card">
            <div className="section-heading">
              <div>
                <h2>Destinations</h2>
                <p>Choose where Feishu notifications are sent.</p>
              </div>
              {selected.mode === "App" && (
                <button
                  type="button"
                  className="secondary"
                  disabled={busy}
                  onClick={() => void discoverTargets()}
                >
                  Discover chats
                </button>
              )}
            </div>
            {discovered.length > 0 && (
              <label>
                Discovered chats
                <select
                  value=""
                  onChange={(event) => {
                    const chat = discovered.find((item) => item.receiveId === event.target.value);
                    if (chat)
                      setTargetForm({ kind: "Chat", receiveId: chat.receiveId, name: chat.name });
                  }}
                >
                  <option value="">Select a chat</option>
                  {discovered.map((chat) => (
                    <option key={chat.receiveId} value={chat.receiveId}>
                      {chat.name}
                    </option>
                  ))}
                </select>
              </label>
            )}
            {targets.map((target) => (
              <div className="list-row" key={target.id}>
                <span>
                  <strong>{target.name}</strong>
                  <small>
                    {target.kind} · {target.receiveId}
                  </small>
                </span>
                <span className="row-actions">
                  <button
                    type="button"
                    className="secondary"
                    disabled={busy}
                    onClick={() => void testSend(target)}
                  >
                    Test
                  </button>
                  {target.id !== selected.id && (
                    <button
                      type="button"
                      className="secondary"
                      disabled={busy}
                      onClick={() => void deleteTarget(target)}
                    >
                      Remove
                    </button>
                  )}
                </span>
              </div>
            ))}
            {selected.mode === "App" && (
              <form className="form-grid compact" onSubmit={(event) => void saveTarget(event)}>
                <label>
                  Kind
                  <select
                    value={targetForm.kind}
                    onChange={(event) =>
                      setTargetForm({ ...targetForm, kind: event.target.value as TargetKind })
                    }
                  >
                    <option value="Chat">Chat</option>
                    <option value="User">User</option>
                  </select>
                </label>
                <label>
                  Name
                  <input
                    value={targetForm.name}
                    required
                    onChange={(event) => setTargetForm({ ...targetForm, name: event.target.value })}
                  />
                </label>
                <label className="wide">
                  Receive ID
                  <input
                    value={targetForm.receiveId}
                    required
                    onChange={(event) =>
                      setTargetForm({ ...targetForm, receiveId: event.target.value })
                    }
                  />
                </label>
                <button type="submit" disabled={busy}>
                  Add destination
                </button>
              </form>
            )}
          </section>

          {selected.mode === "App" && (
            <section className="settings-card">
              <h2>Account links</h2>
              <p>
                Start a link here, then send the code to the bot. The bot returns a confirmation
                link.
              </p>
              <button type="button" disabled={busy} onClick={() => void startBinding()}>
                Start account link
              </button>
              {bindStart && (
                <p className="bind-code">
                  Send <code>/bind {bindStart.code}</code> to the bot. Expires{" "}
                  {new Date(bindStart.expiresAt).toLocaleString()}.
                </p>
              )}
              <div className="binding-list">
                {bindings.map((binding) => (
                  <div className="list-row" key={binding.openId}>
                    <span>
                      <strong>{binding.openId}</strong>
                      <small>Agw user {binding.userId}</small>
                    </span>
                    <button
                      type="button"
                      className="secondary"
                      disabled={busy}
                      onClick={() => void deleteBinding(binding)}
                    >
                      Unlink
                    </button>
                  </div>
                ))}
                {bindings.length === 0 && <p>No linked Feishu accounts.</p>}
              </div>
            </section>
          )}
        </div>
      )}
    </main>
  );
}

function SecretField({
  label,
  saved,
  value,
  onChange,
  required = false,
}: {
  label: string;
  saved: boolean;
  value: string;
  onChange: (value: string) => void;
  required?: boolean;
}) {
  return (
    <label>
      {label} {saved && <small>Saved; leave blank to keep it.</small>}
      <input
        type="password"
        autoComplete="new-password"
        value={value}
        required={required}
        onChange={(event) => onChange(event.target.value)}
      />
    </label>
  );
}

async function request<T>(
  api: ExtensionApi,
  path: string,
  options?: {
    method?: "GET" | "POST" | "PUT" | "DELETE";
    query?: Record<string, string | number | boolean>;
    body?: unknown;
  },
): Promise<T> {
  let value: unknown = await api.server.request<unknown>(path, options);
  while (isEnvelope(value)) {
    if (value.code !== 0) throw new Error(value.title ?? "The Feishu request failed.");
    value = value.data;
  }
  return value as T;
}

function isEnvelope(value: unknown): value is ApiEnvelope<unknown> {
  return (
    typeof value === "object" && value !== null && "code" in value && typeof value.code === "number"
  );
}
