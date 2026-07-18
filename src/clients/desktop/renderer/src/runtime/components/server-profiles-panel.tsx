"use client";

import * as React from "react";
import { Cloud, Pencil, Plus, Server, Trash2 } from "lucide-react";
import { toast } from "sonner";

import type { ServerProfile } from "@desktop/shared/contracts";
import {
  Button,
  Card,
  CardContent,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
} from "@agw/components";
import { useDesktopRuntime } from "../runtime-provider";

type ProfileDraft = {
  id: string | null;
  kind: ServerProfile["kind"];
  name: string;
  baseUrl: string;
  token: string;
  allowInsecureHttp: boolean;
};

const NEW_REMOTE_DRAFT: ProfileDraft = {
  id: null,
  kind: "remote",
  name: "Remote",
  baseUrl: "https://",
  token: "",
  allowInsecureHttp: false,
};

function normalizeHttpUrl(value: string): string {
  const url = new URL(value.trim());
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error("Server URL must use HTTP or HTTPS.");
  }
  url.pathname = url.pathname.replace(/\/+$/u, "");
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/u, "");
}

export function ServerProfilesPanel() {
  const desktop = useDesktopRuntime();
  const settings = desktop.runtimeState?.settings;
  const [dialogOpen, setDialogOpen] = React.useState(false);
  const [draft, setDraft] = React.useState<ProfileDraft>({ ...NEW_REMOTE_DRAFT });
  const [busy, setBusy] = React.useState(false);

  const openAddRemote = () => {
    setDraft({ ...NEW_REMOTE_DRAFT });
    setDialogOpen(true);
  };

  const openEditProfile = (profile: ServerProfile) => {
    setDraft({
      id: profile.id,
      kind: profile.kind,
      name: profile.name,
      baseUrl: profile.baseUrl,
      token: "",
      allowInsecureHttp: profile.allowInsecureHttp,
    });
    setDialogOpen(true);
  };

  const saveProfile = async () => {
    if (!settings) return;
    setBusy(true);
    try {
      const baseUrl = normalizeHttpUrl(draft.baseUrl);
      if (draft.kind === "remote" && baseUrl.startsWith("http://") && !draft.allowInsecureHttp) {
        throw new Error("Remote HTTP requires explicit consent.");
      }

      const profileId = draft.id ?? `remote-${crypto.randomUUID()}`;
      const profile: ServerProfile = {
        id: profileId,
        kind: draft.kind,
        name: draft.name.trim() || (draft.kind === "local" ? "Local" : "Remote"),
        baseUrl,
        apiMajorVersion: 1,
        allowInsecureHttp: draft.kind === "local" ? true : draft.allowInsecureHttp,
      };
      const profileExists = settings.profiles.some((item) => item.id === profileId);
      const profiles = profileExists
        ? settings.profiles.map((item) => (item.id === profileId ? profile : item))
        : [...settings.profiles, profile];

      if (profile.kind === "remote" && draft.token.trim()) {
        await window.agwDesktop?.saveToken(profileId, draft.token.trim());
      }
      await desktop.saveSettings({ ...settings, profiles });

      setDialogOpen(false);
      setDraft({ ...NEW_REMOTE_DRAFT });
      toast.success(profileExists ? "Server profile updated" : "Remote Server added");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to save Server profile");
    } finally {
      setBusy(false);
    }
  };

  const deleteProfile = async (profile: ServerProfile) => {
    if (!settings || profile.kind === "local") return;
    if (!window.confirm(`Delete the Server profile “${profile.name}”?`)) return;

    setBusy(true);
    try {
      const projectTabsByServer = { ...settings.projectTabsByServer };
      delete projectTabsByServer[profile.id];
      await desktop.saveSettings({
        ...settings,
        profiles: settings.profiles.filter((item) => item.id !== profile.id),
        activeServerId: settings.activeServerId === profile.id ? "local" : settings.activeServerId,
        projectTabsByServer,
      });
      await window.agwDesktop?.deleteToken(profile.id);
      toast.success("Server profile deleted");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to delete Server profile");
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <section id="local-server" className="scroll-mt-4 space-y-5">
        <div className="flex flex-col ">
          <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">
            Desktop & Server
          </p>
          <div className="flex items-start justify-between gap-4">
            <div>
              <h1 className="mt-1 text-2xl font-semibold">Connections and app</h1>
              <p className="mt-1 text-sm text-muted-foreground">
                Manage Server profiles, close behavior, package details, and uninstall data.
              </p>
            </div>
            <Button
              type="button"
              size="icon-sm"
              className="mt-1 rounded-xl"
              aria-label="Add remote Server"
              title="Add remote Server"
              disabled={!settings || busy}
              onClick={openAddRemote}
            >
              <Plus className="size-5" />
            </Button>
          </div>
        </div>

        <Card className="gap-0 overflow-hidden py-0">
          <CardContent className="p-0">
            {!settings ? (
              <div className="px-4 py-6 text-sm text-muted-foreground">
                Loading Server profiles…
              </div>
            ) : (
              settings.profiles.map((profile) => {
                const active = profile.id === settings.activeServerId;
                const ProfileIcon = profile.kind === "remote" ? Cloud : Server;
                return (
                  <div
                    key={profile.id}
                    className="grid gap-3 border-b px-4 py-4 last:border-b-0 sm:grid-cols-[minmax(0,0.9fr)_7rem_minmax(0,1.5fr)_auto] sm:items-center"
                  >
                    <div className="flex min-w-0 items-center gap-2">
                      <span className="truncate text-sm font-medium">{profile.name}</span>
                      {active ? (
                        <span className="shrink-0 rounded-full bg-emerald-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-emerald-700 dark:text-emerald-300">
                          Active
                        </span>
                      ) : null}
                    </div>
                    <span className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
                      <ProfileIcon className="size-3.5" />
                      {profile.kind === "remote" ? "Remote" : "Local"}
                    </span>
                    <span
                      className="min-w-0 truncate font-mono text-xs text-muted-foreground"
                      title={profile.baseUrl}
                    >
                      {profile.baseUrl}
                    </span>
                    <div className="flex items-center justify-end gap-1 w-17">
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Edit ${profile.name}`}
                        title={`Edit ${profile.name}`}
                        disabled={busy}
                        onClick={() => openEditProfile(profile)}
                      >
                        <Pencil className="size-4" />
                      </Button>
                      {profile.kind === "remote" ? (
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon-sm"
                          className="text-destructive hover:bg-destructive/10 hover:text-destructive"
                          aria-label={`Delete ${profile.name}`}
                          title={`Delete ${profile.name}`}
                          disabled={busy}
                          onClick={() => void deleteProfile(profile)}
                        >
                          <Trash2 className="size-4" />
                        </Button>
                      ) : null}
                    </div>
                  </div>
                );
              })
            )}
          </CardContent>
        </Card>
      </section>

      <Dialog
        open={dialogOpen}
        onOpenChange={(nextOpen) => {
          if (!busy) setDialogOpen(nextOpen);
        }}
      >
        <DialogContent size="md">
          <form
            className="space-y-5"
            onSubmit={(event) => {
              event.preventDefault();
              void saveProfile();
            }}
          >
            <DialogHeader>
              <DialogTitle>
                {draft.kind === "remote"
                  ? "Configure a remote Server"
                  : "Configure the local Server"}
              </DialogTitle>
              <DialogDescription>
                {draft.kind === "remote"
                  ? "Connect Desktop to another Agw Server. Credentials are stored securely on this device."
                  : "Update the display name and address used for the local Agw Server."}
              </DialogDescription>
            </DialogHeader>

            <div className="grid gap-4">
              <div className="grid gap-2">
                <Label htmlFor="server-profile-name">Name</Label>
                <Input
                  id="server-profile-name"
                  value={draft.name}
                  autoFocus
                  onChange={(event) =>
                    setDraft((current) => ({
                      ...current,
                      name: event.target.value,
                    }))
                  }
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="server-profile-url">Server URL</Label>
                <Input
                  id="server-profile-url"
                  value={draft.baseUrl}
                  placeholder={draft.kind === "remote" ? "https://agw.example.com" : undefined}
                  onChange={(event) =>
                    setDraft((current) => ({
                      ...current,
                      baseUrl: event.target.value,
                    }))
                  }
                />
              </div>
              {draft.kind === "remote" ? (
                <>
                  <div className="grid gap-2">
                    <Label htmlFor="server-profile-token">API token</Label>
                    <Input
                      id="server-profile-token"
                      type="password"
                      value={draft.token}
                      placeholder={draft.id ? "Leave blank to keep the saved token" : "agw_…"}
                      onChange={(event) =>
                        setDraft((current) => ({
                          ...current,
                          token: event.target.value,
                        }))
                      }
                    />
                  </div>
                  <label className="flex items-start gap-2 rounded-xl border bg-muted/25 p-3 text-xs text-muted-foreground">
                    <input
                      type="checkbox"
                      className="mt-0.5"
                      checked={draft.allowInsecureHttp}
                      onChange={(event) =>
                        setDraft((current) => ({
                          ...current,
                          allowInsecureHttp: event.target.checked,
                        }))
                      }
                    />
                    <span>
                      I understand that remote HTTP exposes the API token and traffic on the
                      network.
                    </span>
                  </label>
                </>
              ) : null}
            </div>

            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                disabled={busy}
                onClick={() => setDialogOpen(false)}
              >
                Cancel
              </Button>
              <Button type="submit" disabled={busy}>
                {draft.id ? "Save changes" : "Add Server"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  );
}
