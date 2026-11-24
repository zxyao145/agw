import {
  IconAbc,
  IconArrowDown,
  IconArrowUp,
  IconChevronDown,
  IconChevronRight,
  IconLetterCase,
  IconRegex,
  IconX,
} from "@tabler/icons-react";
import { Tooltip } from "radix-ui";
import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type KeyboardEvent,
  type ReactNode,
} from "react";

import {
  EMPTY_QUERY,
  findShortcut,
  modHeld,
  shortcutText,
  type FindCommand,
  type FindQuery,
  type FindResult,
  type Platform,
} from "./find-query";

/**
 * 搜索栏操作的内容（Markdown 搜索替换设计第 3.1 节）：Mixed 为 EditSearch，Preview 为 PreviewSearch。
 * The content the find bar works on (Markdown find and replace design, section 3.1): EditSearch in Mixed,
 * PreviewSearch in Preview.
 */
export interface FindTarget {
  subscribe(listener: () => void): () => void;
  /** 结果不变时返回同一个对象。Returns the same object while the result is unchanged. */
  getResult(): FindResult;
  /** null 表示搜索栏关闭。null means the bar is closed. */
  setQuery(query: FindQuery | null): void;
  findNext(): void;
  findPrevious(): void;
  /** 没有替换能力时为 null。null without replacing. */
  readonly replace: { next(): void; all(): void } | null;
  /** 内容中单行、非空的选中文字，没有时为 null。Single-line, non-empty selected text in the content, or null. */
  selectedText(): string | null;
  /** 关闭搜索栏后把焦点交还给内容。Gives focus back to the content after the bar closes. */
  focusContent(): void;
}

/**
 * 查看器页面中搜索栏的状态，FindBar 挂载时读取、变化时写回；Preview 按文件重新挂载后由此恢复。
 * The find bar's state in a viewer page, read when FindBar mounts and written back on every change; Preview restores
 * it after remounting per file.
 */
export type FindBarMemory = { open: boolean; replaceOpen: boolean; query: FindQuery };

export function createFindBarMemory(): FindBarMemory {
  return { open: false, replaceOpen: false, query: EMPTY_QUERY };
}

type FocusRequest = { input: "find" | "replace"; id: number };

type ToggleKey = "caseSensitive" | "wholeWord" | "regexp";

function countText(result: FindResult): string {
  switch (result.status) {
    case "idle":
      return "";
    case "invalid":
      return "Invalid regex";
    case "matches":
      if (result.count === 0) return "No results";
      return `${result.current === null ? "?" : result.current + 1} of ${result.count}`;
  }
}

/** 带 tooltip 的按钮，tooltip 为名称加快捷键。A button with a tooltip showing its name and shortcut. */
function BarButton({
  className,
  label,
  shortcut,
  pressed,
  expanded,
  disabled,
  onClick,
  children,
}: {
  className: "find-button" | "find-text-button";
  label: string;
  shortcut?: string;
  pressed?: boolean;
  expanded?: boolean;
  disabled?: boolean;
  onClick(): void;
  children: ReactNode;
}) {
  return (
    <Tooltip.Root>
      <Tooltip.Trigger asChild>
        <button
          type="button"
          className={className}
          aria-label={label}
          aria-pressed={pressed}
          aria-expanded={expanded}
          disabled={disabled}
          onClick={onClick}
        >
          {children}
        </button>
      </Tooltip.Trigger>
      <Tooltip.Portal>
        <Tooltip.Content className="tooltip" side="bottom" sideOffset={6}>
          {shortcut ? `${label} (${shortcut})` : label}
          <Tooltip.Arrow className="tooltip-arrow" />
        </Tooltip.Content>
      </Tooltip.Portal>
    </Tooltip.Root>
  );
}

const ICON_SIZE = 16;

/**
 * 搜索栏（Markdown 搜索替换设计第 3 节）。快捷键在 window 的 keydown 捕获阶段处理，`enabled` 为 false 时不处理；
 * Esc 只在焦点位于搜索栏中时关闭搜索栏。卸载时不调用 `setQuery`：Mixed 卸载时编辑器先于 React 销毁。
 * The find bar (Markdown find and replace design, section 3). Shortcuts are handled in the window's keydown capture
 * phase, and not at all while `enabled` is false; Esc closes the bar only while focus is inside it. Unmounting calls
 * no `setQuery`: Mixed destroys its editor before React unmounts.
 */
export function FindBar({
  target,
  memory,
  platform,
  enabled,
}: {
  target: FindTarget;
  memory: FindBarMemory;
  platform: Platform;
  enabled: boolean;
}) {
  const [open, setOpen] = useState(memory.open);
  const [replaceOpen, setReplaceOpen] = useState(memory.replaceOpen);
  const [query, setQuery] = useState(memory.query);
  const [focusRequest, setFocusRequest] = useState<FocusRequest | null>(null);
  const findInput = useRef<HTMLInputElement>(null);
  const replaceInput = useRef<HTMLInputElement>(null);
  const result = useSyncExternalStore(target.subscribe, target.getResult);
  const { replace } = target;
  const showReplace = replace !== null && replaceOpen;
  const invalid = result.status === "invalid";
  const hasMatches = result.status === "matches" && result.count > 0;

  useEffect(() => {
    memory.open = open;
    memory.replaceOpen = replaceOpen;
    memory.query = query;
  }, [memory, open, replaceOpen, query]);

  useEffect(() => {
    target.setQuery(open ? query : null);
  }, [target, open, query]);

  useLayoutEffect(() => {
    if (!focusRequest) return;
    const input = focusRequest.input === "replace" ? replaceInput.current : findInput.current;
    input!.focus();
    input!.select();
  }, [focusRequest]);

  const toggle = (key: ToggleKey) => setQuery((current) => ({ ...current, [key]: !current[key] }));

  const openBar = (withReplace: boolean) => {
    const seed = target.selectedText();
    if (seed !== null) setQuery((current) => ({ ...current, search: seed }));
    const replacing = withReplace && replace !== null;
    if (replacing) setReplaceOpen(true);
    setOpen(true);
    const search = seed ?? query.search;
    setFocusRequest((current) => ({
      input: replacing && search !== "" ? "replace" : "find",
      id: (current?.id ?? 0) + 1,
    }));
  };

  const close = () => {
    setOpen(false);
    target.focusContent();
  };

  const run = (command: Exclude<FindCommand, "find" | "replace">) => {
    switch (command) {
      case "next":
        target.findNext();
        return;
      case "previous":
        target.findPrevious();
        return;
      case "toggleCase":
        toggle("caseSensitive");
        return;
      case "toggleWholeWord":
        toggle("wholeWord");
        return;
      case "toggleRegexp":
        toggle("regexp");
        return;
    }
  };

  // 每次渲染重新注册，处理函数读到的总是最新状态。Registered again on every render, so the handler reads current state.
  useEffect(() => {
    if (!enabled) return;
    const onKeyDown = (event: globalThis.KeyboardEvent) => {
      const command = findShortcut(event, platform);
      if (command === null) return;
      if (command === "find" || command === "replace") {
        event.preventDefault();
        openBar(command === "replace");
        return;
      }
      if (!open) return;
      event.preventDefault();
      run(command);
    };
    window.addEventListener("keydown", onKeyDown, true);
    return () => window.removeEventListener("keydown", onKeyDown, true);
  });

  const onBarKeyDown = (event: KeyboardEvent) => {
    if (event.key !== "Escape" || event.nativeEvent.isComposing) return;
    event.preventDefault();
    close();
  };

  const onFindKeyDown = (event: KeyboardEvent) => {
    if (event.key !== "Enter" || event.nativeEvent.isComposing) return;
    event.preventDefault();
    if (event.shiftKey) target.findPrevious();
    else target.findNext();
  };

  const onReplaceKeyDown = (event: KeyboardEvent) => {
    if (event.key !== "Enter" || event.nativeEvent.isComposing) return;
    event.preventDefault();
    if (modHeld(event, platform)) replace!.all();
    else replace!.next();
  };

  return (
    <div className="find-bar-anchor">
      {open ? (
        <Tooltip.Provider delayDuration={300}>
          <div
            className="find-bar"
            role="dialog"
            aria-label="Find and replace"
            onKeyDown={onBarKeyDown}
          >
            <div className="find-row">
              {replace !== null ? (
                <BarButton
                  className="find-button"
                  label="Toggle Replace"
                  expanded={showReplace}
                  onClick={() => setReplaceOpen(!replaceOpen)}
                >
                  {showReplace ? (
                    <IconChevronDown size={ICON_SIZE} aria-hidden />
                  ) : (
                    <IconChevronRight size={ICON_SIZE} aria-hidden />
                  )}
                </BarButton>
              ) : null}
              <div className="find-field" data-invalid={invalid ? "" : undefined}>
                <input
                  ref={findInput}
                  aria-label="Find"
                  placeholder="Find"
                  value={query.search}
                  aria-invalid={invalid || undefined}
                  autoComplete="off"
                  spellCheck={false}
                  onChange={(event) => {
                    const search = event.target.value;
                    setQuery((current) => ({ ...current, search }));
                  }}
                  onKeyDown={onFindKeyDown}
                />
                <BarButton
                  className="find-button"
                  label="Match Case"
                  shortcut={shortcutText("toggleCase", platform)}
                  pressed={query.caseSensitive}
                  onClick={() => toggle("caseSensitive")}
                >
                  <IconLetterCase size={ICON_SIZE} aria-hidden />
                </BarButton>
                <BarButton
                  className="find-button"
                  label="Match Whole Word"
                  shortcut={shortcutText("toggleWholeWord", platform)}
                  pressed={query.wholeWord}
                  onClick={() => toggle("wholeWord")}
                >
                  <IconAbc size={ICON_SIZE} aria-hidden />
                </BarButton>
                <BarButton
                  className="find-button"
                  label="Use Regular Expression"
                  shortcut={shortcutText("toggleRegexp", platform)}
                  pressed={query.regexp}
                  onClick={() => toggle("regexp")}
                >
                  <IconRegex size={ICON_SIZE} aria-hidden />
                </BarButton>
              </div>
              <span
                className="find-count"
                aria-live="polite"
                data-invalid={invalid ? "" : undefined}
              >
                {countText(result)}
              </span>
              <BarButton
                className="find-button"
                label="Previous Match"
                shortcut={shortcutText("previous", platform)}
                disabled={!hasMatches}
                onClick={() => target.findPrevious()}
              >
                <IconArrowUp size={ICON_SIZE} aria-hidden />
              </BarButton>
              <BarButton
                className="find-button"
                label="Next Match"
                shortcut={shortcutText("next", platform)}
                disabled={!hasMatches}
                onClick={() => target.findNext()}
              >
                <IconArrowDown size={ICON_SIZE} aria-hidden />
              </BarButton>
              <BarButton
                className="find-button"
                label="Close"
                shortcut={shortcutText("close", platform)}
                onClick={close}
              >
                <IconX size={ICON_SIZE} aria-hidden />
              </BarButton>
            </div>
            {showReplace ? (
              <div className="find-row find-replace-row">
                <div className="find-field">
                  <input
                    ref={replaceInput}
                    aria-label="Replace"
                    placeholder="Replace"
                    value={query.replace}
                    autoComplete="off"
                    spellCheck={false}
                    onChange={(event) => {
                      const replacement = event.target.value;
                      setQuery((current) => ({ ...current, replace: replacement }));
                    }}
                    onKeyDown={onReplaceKeyDown}
                  />
                </div>
                <BarButton
                  className="find-text-button"
                  label="Replace"
                  shortcut={shortcutText("replaceOne", platform)}
                  disabled={!hasMatches}
                  onClick={() => replace.next()}
                >
                  Replace
                </BarButton>
                <BarButton
                  className="find-text-button"
                  label="Replace All"
                  shortcut={shortcutText("replaceAll", platform)}
                  disabled={!hasMatches}
                  onClick={() => replace.all()}
                >
                  Replace All
                </BarButton>
              </div>
            ) : null}
          </div>
        </Tooltip.Provider>
      ) : null}
    </div>
  );
}
