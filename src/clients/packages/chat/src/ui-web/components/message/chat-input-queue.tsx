"use client";

import * as React from "react";
import { Check, CornerDownRight, ImageIcon, Pencil, Play, Trash2, X } from "lucide-react";

import { Button } from "@agw/components";
import { Textarea } from "@agw/components";
import type { ExecutionQueueSnapshot, QueuedExecution } from "@agw/chat-runtime";

export interface ChatInputQueueProps {
  queue: ExecutionQueueSnapshot;
  disabled?: boolean;
  /** 开始编辑一条；返回 false 表示不能编辑。Starts editing an entry; false means it cannot be edited. */
  onEditStart: (itemId: string) => boolean;
  onEditCancel: () => void;
  /** 保存编辑后的文字；返回 false 时保留编辑状态。Saves the edited text; false keeps the editor open. */
  onEditSave: (itemId: string, text: string) => boolean;
  onRemove: (itemId: string) => void;
  onResume: () => void;
}

/**
 * 输入框上方的待发送队列：按顺序列出内容摘要与附件，每条可以编辑或删除，暂停时提供继续入口。
 * The pending queue above the input: lists summaries and attachments in order, each entry can be edited or removed, and a paused queue offers a way to continue.
 */
export function ChatInputQueue({
  queue,
  disabled = false,
  onEditStart,
  onEditCancel,
  onEditSave,
  onRemove,
  onResume,
}: ChatInputQueueProps) {
  const [draft, setDraft] = React.useState("");

  if (queue.items.length === 0) {
    return null;
  }

  const startEdit = (item: QueuedExecution) => {
    if (onEditStart(item.id)) setDraft(item.text);
  };

  return (
    <div
      className="space-y-1.5 border border-b-0 rounded-lg rounded-b-none"
      aria-label="Queued messages"
    >
      {queue.paused ? (
        <div
          role="status"
          className="flex items-center justify-between gap-3 rounded-xl border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm"
        >
          <span className="min-w-0 text-amber-900 dark:text-amber-200">
            {queue.pauseReason ?? "The queue is paused."}
          </span>
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="shrink-0"
            onClick={onResume}
            disabled={disabled || queue.editingItemId !== null}
          >
            <Play className="size-3.5" />
            Continue queue
          </Button>
        </div>
      ) : null}
      <ol className="space-y-1.5 divide-y">
        {queue.items.map((item, index) => (
          <li key={item.id} className="text-sm mb-0" aria-label={`Queued message ${index + 1}`}>
            {queue.editingItemId === item.id ? (
              <QueueItemEditor
                value={draft}
                onChange={setDraft}
                onSave={() => onEditSave(item.id, draft)}
                onCancel={onEditCancel}
              />
            ) : (
              <div className="flex items-center gap-2 px-3">
                <div className="min-w-0 flex-1">
                  <p className="line-clamp-2 break-words whitespace-pre-wrap">
                    {item.text.trim() || (
                      <span className="text-muted-foreground">Code comments only</span>
                    )}
                  </p>
                  <QueueItemDetails item={item} />
                </div>
                <div className="flex shrink-0 items-center gap-0.5">
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    className="size-7"
                    onClick={() => startEdit(item)}
                    disabled={disabled || item.uncertain || queue.editingItemId !== null}
                    aria-label={`Edit queued message ${index + 1}`}
                    title={
                      item.uncertain
                        ? "This message may have been sent already and cannot be edited"
                        : "Edit"
                    }
                  >
                    <Pencil className="size-3.5" />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    className="size-7"
                    onClick={() => onRemove(item.id)}
                    disabled={disabled}
                    aria-label={`Remove queued message ${index + 1}`}
                    title="Remove"
                  >
                    <Trash2 className="size-3.5" />
                  </Button>
                </div>
              </div>
            )}
          </li>
        ))}
      </ol>
    </div>
  );
}

function QueueItemDetails({ item }: { item: QueuedExecution }) {
  const imageCount = item.attachments.length;
  if (imageCount === 0 && item.fileCommentCount === 0 && !item.uncertain) {
    return null;
  }

  return (
    <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
      {imageCount > 0 ? (
        <span className="inline-flex items-center gap-1">
          <ImageIcon className="size-3.5" />
          {imageCount} image{imageCount === 1 ? "" : "s"}
        </span>
      ) : null}
      {item.fileCommentCount > 0 ? (
        <span className="inline-flex items-center gap-1">
          <CornerDownRight className="size-3.5" />
          {item.fileCommentCount} code comment{item.fileCommentCount === 1 ? "" : "s"}
        </span>
      ) : null}
      {item.uncertain ? (
        <span className="text-amber-700 dark:text-amber-300">May have been sent already</span>
      ) : null}
    </div>
  );
}

function QueueItemEditor({
  value,
  onChange,
  onSave,
  onCancel,
}: {
  value: string;
  onChange: (value: string) => void;
  onSave: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="space-y-2">
      <Textarea
        autoFocus
        value={value}
        onChange={(event) => onChange(event.target.value)}
        onKeyDown={(event) => {
          if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
          if (event.key === "Enter" && (event.ctrlKey || event.shiftKey)) {
            event.preventDefault();
            onSave();
          } else if (event.key === "Escape") {
            event.preventDefault();
            onCancel();
          }
        }}
        aria-label="Edit queued message"
        className="agw-scrollbar max-h-40 min-h-[2lh] resize-none text-sm"
      />
      <div className="flex justify-end gap-1">
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          <X className="size-3.5" />
          Cancel
        </Button>
        <Button type="button" size="sm" onClick={onSave}>
          <Check className="size-3.5" />
          Save
        </Button>
      </div>
    </div>
  );
}
