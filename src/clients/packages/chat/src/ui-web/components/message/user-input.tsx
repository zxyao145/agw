"use client";

import {
  ArrowUp,
  ArrowUpToLine,
  Blocks,
  Check,
  ChevronUp,
  FileText,
  Maximize2,
  Minimize2,
  Target,
  Wrench,
} from "lucide-react";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@agw/components";
import { Textarea } from "@agw/components";
import {
  ClipboardEvent,
  KeyboardEvent,
  ReactNode,
  useRef,
  useImperativeHandle,
  forwardRef,
} from "react";
import React from "react";
import { Item, ItemContent, ItemGroup } from "@agw/components";
import { Badge } from "@agw/components";
import { replaceSuggestion, type SuggestionItem } from "@agw/chat-core";
import type { SubmissionMode } from "@agw/chat-runtime";
import {
  ALTERNATE_SUBMIT_SHORTCUT,
  getComposerPlaceholder,
  getSendOptionLabel,
  getSubmitButtonLabel,
  getSubmitShortcutHint,
  PRIMARY_SUBMIT_SHORTCUT,
  resolveComposerKey,
  resolveComposerSubmit,
  SUBMIT_MODE_LABELS,
  type ComposerSubmitState,
  type SteerAvailability,
} from "./composer-submit";

export type { SuggestionItem } from "@agw/chat-core";

// Main UserInput component
export interface UserInputProps {
  onSuggestion?: (
    value: string,
    caretIndex: number,
  ) => SuggestionItem[] | Promise<SuggestionItem[]>;
  // Execution state
  /** 执行进行中：输入框仍可编辑，没有可提交内容时按钮变为终止。A turn is running: the input stays editable, and the button stops it when nothing can be submitted. */
  isExecuting?: boolean;
  isDisabled?: boolean;
  isSubmitDisabled?: boolean;
  /** 没有文字也能提交，例如附带了代码评论。Content can be submitted without text, for example with code comments attached. */
  canSubmitWithoutText?: boolean;

  // Actions
  /** 提交当前文字；受理后清空并收起输入框，保持焦点。mode 是这次提交使用的方式。Submits the text; acceptance clears and collapses the input while keeping focus. mode is the submission mode of this send. */
  onExecute?: (value: string, mode: SubmissionMode) => boolean;
  onStop?: () => void;
  /**
   * 目标对 Steer 的支持情况。提供后，执行中有内容的发送按钮变成分体按钮，可以选择 Queue 或 Steer；省略时只有普通发送。
   * Whether the target supports Steer. When given, the send button of a running turn with content becomes a split button offering Queue or Steer; omitted, there is only the plain send.
   */
  steerAvailability?: SteerAvailability;
  /** 默认提交方式，回车使用它；默认 queue。The default submission mode Enter uses; queue by default. */
  preferredSubmitMode?: SubmissionMode;
  onPreferredSubmitModeChange?: (mode: SubmissionMode) => void;
  /** 备用快捷键选中了不可用的方式时报告原因，这时不发送。Reports the reason when the alternate shortcut picks an unavailable mode; nothing is sent. */
  onSubmitBlocked?: (reason: string) => void;
  onPaste?: (event: ClipboardEvent<HTMLTextAreaElement>) => void;
  /**
   * 用户编辑输入（含发送后清空）时通知新值；ref 的 setInput 不触发。
   * Reports the new value after user edits, including the clear after sending; ref setInput does not.
   */
  onValueChange?: (value: string) => void;

  // Textarea configuration
  placeholder?: string;
  rows?: number;
  maxHeight?: string;
  /** 展开时保持原有占位并向上覆盖显示。Keeps the layout footprint and expands upward as an overlay. */
  expandOverlay?: boolean;

  // Children
  children?: ReactNode;
}

interface UserInputSlots {
  context: ReactNode[];
  topLeft: ReactNode[];
  topRight: ReactNode[];
  bottomLeft: ReactNode[];
  help: ReactNode[];
  sender: ReactNode[];
  queue: ReactNode[];
}

interface UserInputRootProps {
  isExpanded: boolean;
  expandOverlay: boolean;
  onToggleExpanded: () => void;
  isExecuting: boolean;
  isDisabled: boolean;
  isSubmitDisabled: boolean;
  canSubmitWithoutText: boolean;
  placeholder: string;
  rows: number;
  maxHeight: string;
  input: string;
  suggestions?: ReactNode;
  activeSuggestionId?: string;
  suggestionListId?: string;
  hasSuggestions: boolean;
  slots: UserInputSlots;
  onInputChange: (value: string, caretIndex: number) => void;
  onKeyDown: (event: KeyboardEvent<HTMLTextAreaElement>) => void;
  onPaste?: (event: ClipboardEvent<HTMLTextAreaElement>) => void;
  onSend: (mode?: SubmissionMode) => void;
  onStop?: () => void;
  /** 执行期间的提交状态；没有提供 Steer 支持信息时为 null，保持普通发送。The submit state of a running turn; null without Steer support information, which keeps the plain send. */
  submitState: ComposerSubmitState | null;
  preferredSubmitMode: SubmissionMode;
  onPreferredSubmitModeChange?: (mode: SubmissionMode) => void;
  textareaRef?: React.RefObject<HTMLTextAreaElement | null>;
}

function UserInputRoot({
  isExpanded,
  expandOverlay,
  onToggleExpanded,
  isExecuting,
  isDisabled,
  isSubmitDisabled,
  canSubmitWithoutText,
  placeholder,
  rows,
  maxHeight,
  input,
  suggestions,
  activeSuggestionId,
  suggestionListId,
  hasSuggestions,
  slots,
  onInputChange,
  onKeyDown,
  onPaste,
  onSend,
  onStop,
  submitState,
  preferredSubmitMode,
  onPreferredSubmitModeChange,
  textareaRef,
}: UserInputRootProps) {
  const { context, topLeft, topRight, bottomLeft, help, sender, queue } = slots;
  const hasSubmittableContent = hasSubmittableInput(input, canSubmitWithoutText);
  // 执行期间有可提交内容时按钮用于排队发送，否则用于终止执行。
  // While a turn runs the button queues submittable content, otherwise it stops the execution.
  const isStopAction = isExecuting && !hasSubmittableContent;
  const isSendDisabled =
    isDisabled || (isStopAction ? !onStop : isSubmitDisabled || !hasSubmittableContent);
  const handleClick = () => {
    if (isStopAction) {
      onStop?.();
      return;
    }
    onSend();
  };
  const shortcutHint = submitState ? getSubmitShortcutHint(submitState) : null;
  // 输入框为空时才显示占位文字，这时按钮是停止，所以占位文字跟随执行中的主方式而不是按钮状态。
  // The placeholder shows only while the input is empty, when the button is Stop, so it follows the running turn's primary mode and not the button state.
  const effectivePlaceholder =
    submitState && isExecuting ? getComposerPlaceholder(submitState.primaryMode) : placeholder;
  const expandButton = (
    <Button
      type="button"
      variant="ghost"
      size="icon-sm"
      className="size-7 rounded-full"
      onClick={onToggleExpanded}
      aria-label={isExpanded ? "Collapse input" : "Expand input"}
      aria-expanded={isExpanded}
      title={isExpanded ? "Collapse input" : "Expand input"}
    >
      {isExpanded ? <Minimize2 className="size-4" /> : <Maximize2 className="size-4" />}
    </Button>
  );

  return (
    <div
      className={
        isExpanded && expandOverlay
          ? "absolute inset-x-0 bottom-0 flex max-h-[100cqh] flex-col"
          : "relative"
      }
    >
      {/* Top bar with tools and actions */}
      <div className="flex shrink-0 mb-2 gap-2 pointer-events-auto">
        <div className="bg-background rounded-md flex items-center p-0">
          {topLeft.length > 0 && <>{topLeft}</>}
        </div>
        <div className="flex-1" />
        <div
          className={`bg-background rounded-md flex items-center p-0 ${topRight.length > 0 ? "border" : ""}`}
        >
          {topRight.length > 0 && <>{topRight}</>}
        </div>
      </div>
      {queue.length > 0 ? (
        <div
          className={`agw-input-queue ${isExpanded && expandOverlay ? "agw-scrollbar min-h-0 overflow-y-auto bg-background" : ""}`}
        >
          {queue}
        </div>
      ) : null}
      {/* Input area with textarea and action button */}
      <div className="relative shrink-0">
        {suggestions}

        <div
          className={`pointer-events-auto relative px-2 pt-2 pb-11 rounded-xl border bg-background shadow-sm ${isExpanded ? "flex h-[70cqh] min-h-0 flex-col" : ""}`}
        >
          {context.length > 0 ? (
            <div
              className={`mb-2 ${isExpanded ? "agw-scrollbar max-h-[35%] shrink-0 overflow-y-auto" : ""}`}
            >
              {context}
            </div>
          ) : null}
          <Textarea
            ref={textareaRef}
            value={input}
            onChange={(e) => onInputChange(e.target.value, e.target.selectionStart)}
            onKeyDown={onKeyDown}
            onPaste={onPaste}
            role="combobox"
            aria-autocomplete="list"
            aria-haspopup="listbox"
            aria-expanded={hasSuggestions}
            aria-controls={hasSuggestions ? suggestionListId : undefined}
            aria-activedescendant={hasSuggestions ? activeSuggestionId : undefined}
            placeholder={effectivePlaceholder}
            rows={rows}
            className={`${isExpanded ? "h-0 flex-1 field-sizing-fixed" : maxHeight} agw-scrollbar min-h-[1lh] resize-none overflow-x-hidden
            overflow-y-auto rounded-none 
            border-0 p-0 mb-4
            shadow-none 
            focus-visible:ring-0 
            focus-visible:ring-offset-0`}
            disabled={isDisabled}
          />

          {/* Action button - comment mode or regular send */}
          <div className="absolute left-2 right-2 bottom-2 h-7 flex justify-between">
            <div className="flex min-w-0 items-center">{bottomLeft}</div>
            <div className="flex shrink-0 items-center gap-2">
              {expandButton}
              {submitState && submitState.action === "split" ? (
                <SplitSendButton
                  state={submitState}
                  preferredMode={preferredSubmitMode}
                  disabled={isSendDisabled}
                  onSend={onSend}
                  onPreferredModeChange={onPreferredSubmitModeChange}
                  onMenuClosed={() => textareaRef?.current?.focus()}
                />
              ) : (
                <Button
                  size="icon-sm"
                  className="rounded-full size-7"
                  onClick={handleClick}
                  disabled={isSendDisabled}
                  aria-label={isStopAction ? "Stop execution" : "Send message"}
                >
                  {isStopAction && sender.length > 0 ? (
                    <>{sender}</>
                  ) : (
                    <ArrowUp className="size-5" />
                  )}
                </Button>
              )}
            </div>
          </div>
        </div>
      </div>
      {/* Helper text */}
      {help.length > 0 || shortcutHint || submitState?.fallbackNote ? (
        <div className="grid shrink-0 text-xs text-muted-foreground mt-2">
          {help}
          {shortcutHint ? <p>{shortcutHint}</p> : null}
          {submitState?.fallbackNote ? <p role="status">{submitState.fallbackNote}</p> : null}
        </div>
      ) : null}
    </div>
  );
}

function SubmitModeIcon({ mode, className }: { mode: SubmissionMode; className?: string }) {
  return mode === "steer" ? (
    <Target className={className} aria-hidden="true" />
  ) : (
    <ArrowUpToLine className={className} aria-hidden="true" />
  );
}

/** 菜单中两种方式的顺序与说明。The order and descriptions of the two modes in the menu. */
const SUBMIT_MODE_MENU: ReadonlyArray<{
  mode: SubmissionMode;
  description: string;
  iconClassName: string;
}> = [
  { mode: "steer", description: "Process this message now", iconClassName: "text-primary" },
  {
    mode: "queue",
    description: "Run after current work finishes",
    iconClassName: "text-amber-700 dark:text-amber-500",
  },
];

/**
 * 执行中有内容时的分体发送按钮：主按钮使用默认方式，箭头菜单提供另一种方式与默认方式的切换。
 * The split send button for a running turn with content: the primary button uses the default mode, and the arrow menu offers the other mode and the switch of the default.
 */
function SplitSendButton({
  state,
  preferredMode,
  disabled,
  onSend,
  onPreferredModeChange,
  onMenuClosed,
}: {
  state: ComposerSubmitState;
  preferredMode: SubmissionMode;
  disabled: boolean;
  onSend: (mode: SubmissionMode) => void;
  onPreferredModeChange?: (mode: SubmissionMode) => void;
  /** 菜单关闭后把焦点交还输入框。Hands focus back to the input after the menu closes. */
  onMenuClosed: () => void;
}) {
  const { primaryMode, alternateMode, alternateBlockedReason } = state;
  if (alternateMode === null) throw new Error("A split send button needs an alternate mode.");
  const label = getSubmitButtonLabel(primaryMode);

  return (
    <div
      role="group"
      aria-label="Send"
      className="flex h-7 items-stretch overflow-hidden rounded-full"
    >
      <Button
        type="button"
        className="h-7 w-9 rounded-none px-0"
        onClick={() => onSend(primaryMode)}
        disabled={disabled}
        aria-label={label}
        title={`${label} (${PRIMARY_SUBMIT_SHORTCUT})`}
      >
        <SubmitModeIcon mode={primaryMode} className="size-4" />
      </Button>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            type="button"
            className="h-7 w-7 rounded-none border-l border-primary-foreground/30 px-0 brightness-90 hover:brightness-75"
            disabled={disabled}
            aria-label="More send options"
            title="More send options"
          >
            <ChevronUp className="size-4" aria-hidden="true" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent
          side="top"
          align="end"
          className="w-72 rounded-2xl p-2"
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            onMenuClosed();
          }}
        >
          {onPreferredModeChange ? (
            <>
              {SUBMIT_MODE_MENU.map((entry) => {
                const blockedReason = entry.mode === "steer" ? alternateBlockedReason : null;
                const selected = entry.mode === primaryMode;
                return (
                  <DropdownMenuItem
                    key={entry.mode}
                    role="menuitemradio"
                    aria-checked={selected}
                    disabled={blockedReason !== null}
                    className="cursor-pointer items-start gap-3 rounded-lg px-3 py-2"
                    onSelect={() => {
                      if (entry.mode !== preferredMode) onPreferredModeChange(entry.mode);
                    }}
                  >
                    <SubmitModeIcon
                      mode={entry.mode}
                      className={`mt-0.5 size-5 shrink-0 ${entry.iconClassName}`}
                    />
                    <span className="min-w-0 flex-1">
                      <span className="block text-sm font-medium">
                        {SUBMIT_MODE_LABELS[entry.mode]}
                      </span>
                      <span className="block text-xs text-muted-foreground">
                        {blockedReason ?? entry.description}
                      </span>
                    </span>
                    {selected ? (
                      <Check className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden="true" />
                    ) : null}
                  </DropdownMenuItem>
                );
              })}
              <DropdownMenuSeparator />
            </>
          ) : null}
          <DropdownMenuItem
            disabled={alternateBlockedReason !== null}
            className="cursor-pointer gap-3 rounded-lg px-3 py-2"
            onSelect={() => onSend(alternateMode)}
          >
            <kbd className="shrink-0 rounded border bg-muted px-1 font-mono text-11 text-muted-foreground">
              {ALTERNATE_SUBMIT_SHORTCUT}
            </kbd>
            <span className="min-w-0 text-sm">
              {onPreferredModeChange
                ? getSendOptionLabel(alternateMode)
                : (alternateBlockedReason ?? getSendOptionLabel(alternateMode))}
            </span>
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}

function getUserInputSlots(children?: ReactNode): UserInputSlots {
  const slots: UserInputSlots = {
    context: [],
    topLeft: [],
    topRight: [],
    bottomLeft: [],
    help: [],
    sender: [],
    queue: [],
  };

  if (!children) {
    return slots;
  }

  const childArray = Array.isArray(children) ? children : [children];
  childArray.forEach((child) => {
    if (!child || typeof child !== "object" || !("type" in child)) {
      return;
    }

    const element = child as { type: { displayName?: string } };
    switch (element.type?.displayName) {
      case "UserInput.Context":
        slots.context.push(child);
        break;
      case "UserInput.TopLeft":
        slots.topLeft.push(child);
        break;
      case "UserInput.TopRight":
        slots.topRight.push(child);
        break;
      case "UserInput.BottomLeft":
        slots.bottomLeft.push(child);
        break;
      case "UserInput.Help":
        slots.help.push(child);
        break;
      case "UserInput.Sender":
        slots.sender.push(child);
        break;
      case "UserInput.Queue":
        slots.queue.push(child);
        break;
      default:
        break;
    }
  });

  return slots;
}

interface UserInputSuggestionsProps {
  suggestions: SuggestionItem[];
  activeIndex: number;
  id: string;
  onSelect: (suggestion: SuggestionItem) => void;
  onActiveIndexChange: (index: number) => void;
}

function UserInputSuggestions({
  suggestions,
  activeIndex,
  id,
  onSelect,
  onActiveIndexChange,
}: UserInputSuggestionsProps) {
  if (suggestions.length === 0) {
    return null;
  }

  return (
    <div className="absolute z-99 bottom-full left-0 right-0 pointer-events-auto mb-0 py-2 px-1 rounded-xl border bg-background/95 shadow-sm backdrop-blur-sm">
      <div className="flex items-center justify-between px-2 pb-1 text-11 font-semibold uppercase tracking-wide text-muted-foreground">
        Suggestions
      </div>
      <ItemGroup id={id} role="listbox" aria-label="Suggestions" className="gap-0">
        {suggestions.map((suggestion, index) => {
          const isFileSuggestion = suggestion.text.startsWith("@");
          const fileParts = isFileSuggestion ? getFileSuggestionParts(suggestion) : undefined;
          const name = fileParts?.name ?? suggestion.text;
          const description = fileParts ? fileParts.directory : suggestion.description;

          return (
            <Item
              key={`${suggestion.text}-${index}`}
              id={`${id}-option-${index}`}
              role="option"
              aria-selected={activeIndex === index}
              onClick={() => onSelect(suggestion)}
              onMouseEnter={() => onActiveIndexChange(index)}
              variant="outline"
              size="sm"
              className={`p-2 cursor-pointer rounded-sm text-sm text-foreground transition hover:border-border hover:bg-accent/50 ${activeIndex === index ? "border-border bg-accent/50" : "border-transparent bg-transparent"}`}
            >
              <ItemContent className="min-w-0 flex-row items-center gap-1 overflow-hidden whitespace-nowrap">
                {fileParts ? (
                  <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                ) : (
                  <Badge
                    data-kind={suggestion.kind}
                    className="px-1.5 h-fit shrink-0 gap-1 text-xs leading-none border-0 data-[kind=skill]:bg-orange-100 data-[kind=skill]:text-orange-900 dark:data-[kind=skill]:bg-orange-950 dark:data-[kind=skill]:text-orange-200 data-[kind=tool]:bg-blue-100 data-[kind=tool]:text-blue-900 dark:data-[kind=tool]:bg-blue-950 dark:data-[kind=tool]:text-blue-200"
                  >
                    {suggestion.kind === "skill" ? (
                      <Blocks aria-hidden="true" />
                    ) : suggestion.kind === "tool" ? (
                      <Wrench aria-hidden="true" />
                    ) : null}
                    {suggestion.kind ?? "command"}
                  </Badge>
                )}
                <span
                  className="max-w-[55%] shrink-0 truncate font-medium text-foreground"
                  title={name}
                >
                  {name}
                </span>
                {description ? (
                  <span
                    className="min-w-0 truncate text-11 text-muted-foreground"
                    title={description}
                  >
                    {description}
                  </span>
                ) : null}
              </ItemContent>
            </Item>
          );
        })}
      </ItemGroup>
    </div>
  );
}

function getFileSuggestionParts(suggestion: SuggestionItem): { name: string; directory: string } {
  const path = (suggestion.description || suggestion.text.slice(1).replace(/^"|"$/g, "")).replace(
    /\\/g,
    "/",
  );
  const separatorIndex = path.lastIndexOf("/");

  return {
    name: path.slice(separatorIndex + 1),
    directory: separatorIndex === -1 ? "" : path.slice(0, separatorIndex),
  };
}

export interface UserInputRef {
  value: string;
  /** 载入草稿并收起输入框。Loads a draft and collapses the input. */
  setInput: (value: string) => void;
  insertText: (text: string) => void;
}

/**
 * 可提交的内容：非空白文字，或者没有文字也能提交的附加内容（例如代码评论）。
 * Submittable content: non-blank text, or extras that can be submitted without text (such as code comments).
 */
function hasSubmittableInput(input: string, canSubmitWithoutText: boolean): boolean {
  return input.trim().length > 0 || canSubmitWithoutText;
}

function UserInputContainer({
  isExecuting = false,
  isDisabled = false,
  isSubmitDisabled = false,
  canSubmitWithoutText = false,
  onExecute,
  onStop,
  onPaste,
  onValueChange,
  placeholder = "Type your message...",
  rows = 1,
  maxHeight = "max-h-60",
  expandOverlay = false,
  children,
  onSuggestion,
  steerAvailability,
  preferredSubmitMode = "queue",
  onPreferredSubmitModeChange,
  onSubmitBlocked,
  inputRef,
}: UserInputProps & { inputRef: React.Ref<UserInputRef> }) {
  const [input, setInput] = React.useState("");
  const [isExpanded, setIsExpanded] = React.useState(false);
  const [collapsedHeight, setCollapsedHeight] = React.useState(0);
  const [suggestions, setSuggestions] = React.useState<SuggestionItem[]>([]);
  const [activeSuggestionIndex, setActiveSuggestionIndex] = React.useState(0);
  const slots = getUserInputSlots(children);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const layoutRef = useRef<HTMLDivElement>(null);
  const suggestionRequestRef = useRef(0);
  const suggestionCaretRef = useRef(0);
  const suggestionListId = React.useId();

  useImperativeHandle(
    inputRef,
    () => ({
      get value() {
        return input;
      },
      setInput: (value: string) => {
        suggestionRequestRef.current += 1;
        setInput(value);
        setIsExpanded(false);
        setSuggestions([]);
        setActiveSuggestionIndex(0);
      },
      insertText: (text: string) => {
        const textarea = textareaRef.current;
        const selectionStart = textarea?.selectionStart ?? input.length;
        const selectionEnd = textarea?.selectionEnd ?? selectionStart;
        const insertedText = `${text} `;
        const newValue = input.slice(0, selectionStart) + insertedText + input.slice(selectionEnd);
        const nextCursor = selectionStart + insertedText.length;
        suggestionRequestRef.current += 1;
        setInput(newValue);
        onValueChange?.(newValue);
        setSuggestions([]);
        setActiveSuggestionIndex(0);
        setTimeout(() => {
          const currentTextarea = textareaRef.current;
          if (currentTextarea) {
            currentTextarea.focus();
            currentTextarea.setSelectionRange(nextCursor, nextCursor);
          }
        }, 0);
      },
    }),
    [input, onValueChange],
  );

  const handleSuggestionClick = (suggestion: SuggestionItem) => {
    const replacement = replaceSuggestion(input, suggestion.text, suggestionCaretRef.current);
    suggestionRequestRef.current += 1;
    setInput(replacement.value);
    onValueChange?.(replacement.value);
    setSuggestions([]);
    setActiveSuggestionIndex(0);

    // Focus textarea and move cursor after the selected suggestion
    setTimeout(() => {
      const textarea = textareaRef.current;
      if (textarea) {
        textarea.focus();
        textarea.setSelectionRange(replacement.caretIndex, replacement.caretIndex);
      }
    }, 0);
  };

  const handleInputChange = (value: string, caretIndex: number) => {
    const requestId = suggestionRequestRef.current + 1;
    suggestionRequestRef.current = requestId;
    suggestionCaretRef.current = caretIndex;
    setInput(value);
    onValueChange?.(value);
    setActiveSuggestionIndex(0);
    if (!onSuggestion) {
      if (suggestions.length > 0) {
        setSuggestions([]);
      }
      return;
    }

    const result = onSuggestion(value, caretIndex);
    if (result instanceof Promise) {
      // Async suggestion - clear current suggestions while loading
      setSuggestions([]);
      result
        .then((nextSuggestions) => {
          if (suggestionRequestRef.current === requestId) {
            setSuggestions(nextSuggestions);
            setActiveSuggestionIndex(0);
          }
        })
        .catch(() => {
          if (suggestionRequestRef.current === requestId) {
            setSuggestions([]);
          }
        });
    } else {
      // Sync suggestion
      setSuggestions(result ?? []);
    }
  };

  const suggestionContent = (
    <UserInputSuggestions
      suggestions={suggestions}
      activeIndex={activeSuggestionIndex}
      id={suggestionListId}
      onSelect={handleSuggestionClick}
      onActiveIndexChange={setActiveSuggestionIndex}
    />
  );

  const submitState = steerAvailability
    ? resolveComposerSubmit({
        isExecuting,
        hasContent: hasSubmittableInput(input, canSubmitWithoutText),
        preferredMode: preferredSubmitMode,
        steer: steerAvailability,
      })
    : null;

  // 发送按钮与键盘快捷键共用这个入口；被拒绝时保留草稿。
  // The send button and keyboard shortcuts share this entry; a rejected submission keeps the draft.
  const handleSend = (mode: SubmissionMode = submitState?.primaryMode ?? "queue") => {
    if (isDisabled || isSubmitDisabled || !hasSubmittableInput(input, canSubmitWithoutText)) {
      return;
    }
    if (!onExecute?.(input, mode)) {
      return;
    }
    suggestionRequestRef.current += 1;
    setInput("");
    setIsExpanded(false);
    onValueChange?.("");
    setSuggestions([]);
    setActiveSuggestionIndex(0);
    textareaRef.current?.focus();
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) {
      return;
    }

    if (isExpanded && event.key === "Enter") {
      if (event.ctrlKey || event.metaKey || event.altKey) {
        event.preventDefault();
        const textarea = event.currentTarget;
        textarea.setRangeText("\n", textarea.selectionStart, textarea.selectionEnd, "end");
        handleInputChange(textarea.value, textarea.selectionStart);
      }
      return;
    }

    if (event.key === "Enter" && event.ctrlKey && !event.shiftKey) {
      event.preventDefault();
      handleSend();
      return;
    }

    if (suggestions.length > 0 && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
      event.preventDefault();
      const direction = event.key === "ArrowDown" ? 1 : -1;
      setActiveSuggestionIndex(
        (index) => (index + direction + suggestions.length) % suggestions.length,
      );
      return;
    }

    if (
      suggestions.length > 0 &&
      (event.key === "Enter" || event.key === "Tab") &&
      !event.altKey &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.shiftKey
    ) {
      event.preventDefault();
      const suggestion = suggestions[activeSuggestionIndex];
      if (suggestion) {
        handleSuggestionClick(suggestion);
      }
      return;
    }

    if (
      submitState &&
      event.key === "Enter" &&
      event.altKey &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.shiftKey
    ) {
      // 备用快捷键：用另一种提交方式发送；该方式不可用时只报告原因。
      // The alternate shortcut sends with the other mode; when that mode is unavailable it only reports the reason.
      event.preventDefault();
      const action = resolveComposerKey(submitState, "alternate");
      if (action.type === "submit") handleSend(action.mode);
      else if (action.type === "blocked") onSubmitBlocked?.(action.reason);
      return;
    }

    if (
      !isExpanded &&
      event.key === "Enter" &&
      !event.shiftKey &&
      !event.altKey &&
      !event.metaKey
    ) {
      event.preventDefault();
      handleSend();
    }
  };

  return (
    <div
      ref={layoutRef}
      className="relative"
      style={{ height: isExpanded && expandOverlay ? collapsedHeight : undefined }}
    >
      <UserInputRoot
        isExpanded={isExpanded}
        expandOverlay={expandOverlay}
        onToggleExpanded={() => {
          if (!isExpanded) {
            setCollapsedHeight(layoutRef.current!.getBoundingClientRect().height);
          }
          setIsExpanded((expanded) => !expanded);
          textareaRef.current?.focus({ preventScroll: true });
        }}
        isExecuting={isExecuting}
        isDisabled={isDisabled}
        isSubmitDisabled={isSubmitDisabled}
        canSubmitWithoutText={canSubmitWithoutText}
        placeholder={placeholder}
        rows={rows}
        maxHeight={maxHeight}
        input={input}
        suggestions={suggestionContent}
        activeSuggestionId={
          suggestions.length > 0 ? `${suggestionListId}-option-${activeSuggestionIndex}` : undefined
        }
        suggestionListId={suggestionListId}
        hasSuggestions={suggestions.length > 0}
        slots={slots}
        onInputChange={handleInputChange}
        onKeyDown={handleKeyDown}
        onPaste={onPaste}
        onSend={handleSend}
        onStop={onStop}
        submitState={submitState}
        preferredSubmitMode={preferredSubmitMode}
        onPreferredSubmitModeChange={onPreferredSubmitModeChange}
        textareaRef={textareaRef}
      />
    </div>
  );
}

interface ContextProps {
  children: ReactNode;
}

function Context({ children }: ContextProps) {
  return <>{children}</>;
}
Context.displayName = "UserInput.Context";

// TopLeftSlots component
interface TopLeftProps {
  children: ReactNode;
}

function TopLeft({ children }: TopLeftProps) {
  return <>{children}</>;
}
TopLeft.displayName = "UserInput.TopLeft";

// TopRightActions component
interface TopRightProps {
  children: ReactNode;
}

function TopRight({ children }: TopRightProps) {
  return <>{children}</>;
}
TopRight.displayName = "UserInput.TopRight";

interface BottomLeftProps {
  children: ReactNode;
}

function BottomLeft({ children }: BottomLeftProps) {
  return <>{children}</>;
}
BottomLeft.displayName = "UserInput.BottomLeft";

// Help component
interface HelpProps {
  children: ReactNode;
}

function Help({ children }: HelpProps) {
  return <>{children}</>;
}
Help.displayName = "UserInput.Help";

// Help component
interface SenderProps {
  children: ReactNode;
}

function Sender({ children }: SenderProps) {
  return <>{children}</>;
}
Sender.displayName = "UserInput.Sender";

// 输入框上方的待发送队列。The pending queue above the input box.
interface QueueProps {
  children: ReactNode;
}

function Queue({ children }: QueueProps) {
  return <>{children}</>;
}
Queue.displayName = "UserInput.Queue";

// Export compound component
const UserInputWithRef = forwardRef<UserInputRef, UserInputProps>((props, ref) => (
  <UserInputContainer {...props} inputRef={ref} />
));

UserInputWithRef.displayName = "UserInput";

export const UserInput = Object.assign(UserInputWithRef, {
  Context,
  TopLeft: TopLeft,
  TopRight: TopRight,
  BottomLeft: BottomLeft,
  Help: Help,
  Sender: Sender,
  Queue: Queue,
});
