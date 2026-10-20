"use client";

import * as React from "react";
import { CircleAlert, Info, TriangleAlert } from "lucide-react";
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import type { ExtensionHost, HostConfirmation } from "../../extensions/extension-host";

type MessageConfirmation = Extract<HostConfirmation, { kind: "message" }>;

const MESSAGE_ICONS = {
  info: { Icon: Info, className: "text-muted-foreground" },
  warning: { Icon: TriangleAlert, className: "text-amber-600 dark:text-amber-400" },
  error: { Icon: CircleAlert, className: "text-destructive" },
} as const;

/**
 * 宿主的确认对话框：放弃文件查看器的未保存修改、打开未批准域名的链接、extension 的消息框。
 * The host's confirmation dialog: discarding a file viewer's unsaved changes, opening a link outside the approved
 * hosts, or an extension's message box.
 */
export function HostConfirmationDialog({ host }: { host: ExtensionHost }) {
  const snapshot = React.useSyncExternalStore(host.subscribe, host.getSnapshot, host.getSnapshot);
  return (
    <HostConfirmationDialogView
      confirmation={snapshot.confirmation}
      onAnswer={(answer) => host.resolveConfirmation(answer)}
    />
  );
}

/**
 * 显示一个确认对话框；按 `Esc` 或关闭时 `message` 回答 `cancelId`，其他回答 false。
 * Shows one confirmation; `Esc` or closing answers `cancelId` for `message` and false for the others.
 */
export function HostConfirmationDialogView({
  confirmation,
  onAnswer,
}: {
  confirmation: HostConfirmation | null;
  onAnswer(answer: boolean | number): void;
}) {
  // 每次从关闭到打开都使用新的对话框实例。遮罩与内容各在 body 下的一个 portal 中，各自播放退出动画（遮罩的动画短于内容）；
  // 同一个实例在内容退出期间重新打开时，遮罩重新挂到 body 末尾、位于仍在的内容之后并盖住它，焦点也不会进入对话框。
  // 排队中的确认依次显示时对话框一直打开，沿用同一个实例。
  // Every opening after a close uses a new dialog instance. The overlay and the content sit in separate portals under
  // body and play their exit animations separately (the overlay's is shorter); reopening the same instance while the
  // content is still leaving appends the overlay to the end of body, after the remaining content, covering it, and
  // focus never enters the dialog. Queued confirmations shown one after another keep the dialog open and the instance.
  const [session, setSession] = React.useState({ key: 0, open: false });
  const open = confirmation !== null;
  if (open !== session.open) setSession({ key: open ? session.key + 1 : session.key, open });
  // 实例之间共用的焦点状态：上一个实例关闭期间打开的新实例沿用第一个对话框打开前的焦点。
  // Focus state shared by the instances: a new instance that opens while the previous one is closing keeps the focus
  // recorded before the first dialog opened.
  const focus = React.useRef<DialogFocus>({ open: false, restore: null }).current;
  React.useEffect(() => {
    if (confirmation === null) focus.open = false;
  }, [confirmation, focus]);
  return (
    <ConfirmationDialog
      key={session.key}
      confirmation={confirmation}
      focus={focus}
      onAnswer={onAnswer}
    />
  );
}

type DialogFocus = {
  /** 有对话框打开。A dialog is open. */
  open: boolean;
  /**
   * 第一个对话框打开前的焦点。Radix 的模态对话框关闭时把焦点交给 DialogTrigger，这里没有 trigger，由对话框自己还给它。
   * The focus before the first dialog opened. Radix's modal dialog focuses its DialogTrigger on close; there is none
   * here, so the dialog restores this element itself.
   */
  restore: HTMLElement | null;
};

function ConfirmationDialog({
  confirmation,
  focus,
  onAnswer,
}: {
  confirmation: HostConfirmation | null;
  focus: DialogFocus;
  onAnswer(answer: boolean | number): void;
}) {
  // 这个实例打开之后为 true：之后的消息框自己设置焦点。True once this instance opened: later message boxes set focus
  // themselves.
  const opened = React.useRef(false);
  React.useEffect(() => {
    if (confirmation === null) opened.current = false;
  }, [confirmation]);
  return (
    <Dialog
      open={confirmation !== null}
      onOpenChange={(open) =>
        open
          ? undefined
          : onAnswer(confirmation?.kind === "message" ? confirmation.cancelId : false)
      }
    >
      <DialogContent
        size="sm"
        data-host-confirmation={confirmation?.kind}
        onOpenAutoFocus={(event) => {
          opened.current = true;
          focus.open = true;
          if (focus.restore === null && document.activeElement instanceof HTMLElement) {
            focus.restore = document.activeElement;
          }
          if (confirmation?.kind !== "message") return;
          event.preventDefault();
          focusMessageButton(event.currentTarget as HTMLElement, confirmation);
        }}
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          // 关闭之后又打开了新的对话框时，焦点留在新对话框中，打开前的焦点留到它关闭时再还。When a new dialog opened
          // meanwhile, focus stays in it, and the earlier focus is restored when that one closes.
          if (focus.open) return;
          focus.restore?.focus();
          focus.restore = null;
        }}
      >
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
              <Button variant="outline" onClick={() => onAnswer(false)}>
                Cancel
              </Button>
              <Button variant="destructive" onClick={() => onAnswer(true)}>
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
              <Button variant="outline" onClick={() => onAnswer(false)}>
                Cancel
              </Button>
              <Button onClick={() => onAnswer(true)}>Open</Button>
            </DialogFooter>
          </>
        ) : confirmation?.kind === "message" ? (
          <MessageBox
            key={confirmation.id}
            confirmation={confirmation}
            opened={opened}
            onAnswer={onAnswer}
          />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

/** 让 defaultId 对应的按钮获得焦点，未指定时为第一个按钮。Focuses the defaultId button, or the first button when none is given. */
function focusMessageButton(container: HTMLElement, confirmation: MessageConfirmation): void {
  container
    .querySelector<HTMLButtonElement>(`[data-message-button="${confirmation.defaultId ?? 0}"]`)!
    .focus();
}

/**
 * extension 的消息框：标题为 extension 名称，下方依次为 message、detail 与按钮。文字与按钮都可以换行，
 * 规定范围内的任何内容都留在对话框中。
 * An extension's message box: titled with the extension name, then message, detail and buttons. Text and buttons
 * wrap, so any content within the allowed limits stays inside the dialog.
 */
function MessageBox({
  confirmation,
  opened,
  onAnswer,
}: {
  confirmation: MessageConfirmation;
  opened: React.RefObject<boolean>;
  onAnswer(answer: number): void;
}) {
  const footer = React.useRef<HTMLDivElement>(null);
  // 对话框已经打开时由消息框自己设置焦点；第一个消息框的焦点由 onOpenAutoFocus 设置。
  // Once the dialog is open the message box sets focus itself; onOpenAutoFocus focuses the first one.
  React.useEffect(() => {
    if (opened.current) focusMessageButton(footer.current!, confirmation);
  }, [confirmation, opened]);
  const icon = confirmation.type === null ? null : MESSAGE_ICONS[confirmation.type];
  return (
    <>
      <DialogHeader>
        <DialogTitle>{confirmation.extensionName}</DialogTitle>
        <DialogDescription className="flex gap-2 text-foreground">
          {icon ? (
            <icon.Icon className={`mt-0.5 size-4 shrink-0 ${icon.className}`} aria-hidden="true" />
          ) : null}
          <span className="min-w-0 whitespace-pre-wrap wrap-anywhere">{confirmation.message}</span>
        </DialogDescription>
      </DialogHeader>
      {confirmation.detail !== null ? (
        <p className="text-muted-foreground text-sm whitespace-pre-wrap wrap-anywhere">
          {confirmation.detail}
        </p>
      ) : null}
      <DialogFooter ref={footer} className="flex-wrap">
        {confirmation.buttons.map((label, index) => (
          <Button
            key={index}
            data-message-button={index}
            className="h-auto min-h-9 max-w-full min-w-0 shrink whitespace-normal wrap-anywhere"
            variant={index === confirmation.defaultId ? "default" : "outline"}
            onClick={() => onAnswer(index)}
          >
            {label}
          </Button>
        ))}
      </DialogFooter>
    </>
  );
}
