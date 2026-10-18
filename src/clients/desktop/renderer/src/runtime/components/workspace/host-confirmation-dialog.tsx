"use client";

import * as React from "react";
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import type { ExtensionHost } from "../../extensions/extension-host";

/**
 * 宿主的确认对话框：放弃文件查看器的未保存修改、打开未批准域名的链接。
 * The host's confirmation dialog: discarding a file viewer's unsaved changes, or opening a link outside the approved hosts.
 */
export function HostConfirmationDialog({ host }: { host: ExtensionHost }) {
  const snapshot = React.useSyncExternalStore(host.subscribe, host.getSnapshot, host.getSnapshot);
  const confirmation = snapshot.confirmation;
  return (
    <Dialog
      open={confirmation !== null}
      onOpenChange={(open) => (open ? undefined : host.resolveConfirmation(false))}
    >
      <DialogContent size="sm" data-host-confirmation={confirmation?.kind}>
        {confirmation?.kind === "discard-unsaved" ? (
          <>
            <DialogHeader>
              <DialogTitle>Discard unsaved changes?</DialogTitle>
              <DialogDescription>These changes have not been saved:</DialogDescription>
            </DialogHeader>
            <ul className="space-y-1 text-sm">
              {confirmation.items.map((item, index) => (
                <li key={index}>
                  <span className="font-medium">{item.fileName}</span>
                  <span className="text-muted-foreground">
                    {" "}
                    · {item.viewerTitle} ({item.extensionName})
                  </span>
                </li>
              ))}
            </ul>
            <DialogFooter>
              <Button variant="outline" onClick={() => host.resolveConfirmation(false)}>
                Cancel
              </Button>
              <Button variant="destructive" onClick={() => host.resolveConfirmation(true)}>
                Discard
              </Button>
            </DialogFooter>
          </>
        ) : confirmation?.kind === "open-link" ? (
          <>
            <DialogHeader>
              <DialogTitle>Open link?</DialogTitle>
              <DialogDescription>
                {confirmation.extensionName} wants to open this link in your browser.
              </DialogDescription>
            </DialogHeader>
            <p className="font-mono text-xs break-all">{confirmation.url}</p>
            <DialogFooter>
              <Button variant="outline" onClick={() => host.resolveConfirmation(false)}>
                Cancel
              </Button>
              <Button onClick={() => host.resolveConfirmation(true)}>Open</Button>
            </DialogFooter>
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}
